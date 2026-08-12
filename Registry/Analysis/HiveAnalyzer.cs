using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Registry.Abstractions;
using Registry.Cells;

namespace Registry.Analysis;

/// <summary>
///     Provides read-only, diagnostic analysis of a parsed <see cref="RegistryHive" />, aimed at identifying
///     structural bloat, duplicate/templated key or value patterns, and orphaned records.
///     <remarks>
///         This grew out of investigating a real-world 2GB NTUSER.DAT hive that loaded and parsed
///         successfully (no corruption) but was nonetheless unusually large. The analysis performed here
///         (tree-walk reachability, cell-record duplicate hashing, and subkey-count bloat detection)
///         is what identified a Windows Remote Desktop Client bug that leaves an unbounded number of
///         diagnostic-cache subkeys under
///         <c>Software\Microsoft\RdClientRadc\DiagConnectionCache</c>. See
///         <c>docs/NTUSER_BLOAT_FINDINGS.md</c> for the full write-up of that investigation.
///     </remarks>
/// </summary>
public static class HiveAnalyzer
{
    /// <summary>
    ///     Walks the hive's key tree starting at <paramref name="hive" />.Root and reports how many NK/VK
    ///     records are actually reachable (i.e. structurally referenced by the tree), compared against the
    ///     total number of cells the parser flagged as "in use". A large gap between these numbers indicates
    ///     either a genuinely corrupted/inconsistent hive, or a parser bug -- NOT normal, healthy bloat (which
    ///     instead shows up as very deep/wide, but fully reachable, trees; see
    ///     <see cref="GetTopSubkeyCounts" />).
    /// </summary>
    public static ReachabilityReport AnalyzeReachability(RegistryHive hive)
    {
        if (hive?.Root == null)
        {
            throw new ArgumentException("Hive must be parsed (hive.Root must not be null) before analysis.", nameof(hive));
        }

        long liveKeys = 0;
        long liveValues = 0;
        var visitedOffsets = new HashSet<long>();

        void Walk(RegistryKey key)
        {
            // Guard against cycles/re-visits; a well-formed hive should never revisit the same NK offset.
            if (!visitedOffsets.Add(key.NkRecord.AbsoluteOffset))
            {
                return;
            }

            liveKeys++;
            liveValues += key.Values.Count;

            foreach (var subKey in key.SubKeys)
            {
                Walk(subKey);
            }
        }

        Walk(hive.Root);

        var totalInUseKeys = hive.CellRecords.Values.Count(c => c is NkCellRecord && !c.IsFree);
        var totalInUseValues = hive.CellRecords.Values.Count(c => c is VkCellRecord && !c.IsFree);

        return new ReachabilityReport
        {
            ReachableKeys = liveKeys,
            ReachableValues = liveValues,
            TotalInUseKeyCells = totalInUseKeys,
            TotalInUseValueCells = totalInUseValues
        };
    }

    /// <summary>
    ///     Groups all in-use NK/VK/SK cell records by a hash of the first <paramref name="hashByteLength" />
    ///     bytes of their payload (i.e. everything after the 4-byte cell-size prefix), returning only groups
    ///     with more than one member. This surfaces both literal duplicate records and templated/near-identical
    ///     records (e.g. many sibling keys created from the same code path with the same value layout).
    ///     <remarks>
    ///         Run with several different values of <paramref name="hashByteLength" /> (e.g. 4, 8, 16, 32, 64) --
    ///         short lengths mostly catch shared structural headers (flags/lengths), while longer lengths only
    ///         match truly repeated content.
    ///     </remarks>
    /// </summary>
    public static List<DuplicateCellGroup> FindDuplicateCellGroups(RegistryHive hive, int hashByteLength, int minGroupSize = 2)
    {
        if (hive?.CellRecords == null)
        {
            throw new ArgumentException("Hive must be parsed before analysis.", nameof(hive));
        }

        if (hashByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hashByteLength), "Must be greater than zero.");
        }

        var groups = new Dictionary<string, List<ICellTemplate>>();

        foreach (var cell in hive.CellRecords.Values)
        {
            if (cell.IsFree)
            {
                continue;
            }

            var rawBytes = cell.RawBytes;
            if (rawBytes == null || rawBytes.Length <= 4)
            {
                continue;
            }

            var len = Math.Min(hashByteLength, rawBytes.Length - 4);
            if (len <= 0)
            {
                continue;
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(rawBytes, 4, len);
            var key = cell.Signature + ":" + Convert.ToBase64String(hash);

            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<ICellTemplate>();
                groups[key] = list;
            }

            list.Add(cell);
        }

        return groups.Values
            .Where(g => g.Count >= minGroupSize)
            .OrderByDescending(g => g.Count)
            .Select(g => new DuplicateCellGroup
            {
                Signature = g[0].Signature,
                Count = g.Count,
                SampleSize = g[0].Size,
                SampleOffsets = g.Take(10).Select(c => c.AbsoluteOffset).ToList(),
                EstimatedWastedBytes = (long) (g.Count - 1) * Math.Abs(g[0].Size)
            })
            .ToList();
    }

    /// <summary>
    ///     Walks the hive's key tree and reports every key whose direct subkey count meets or exceeds
    ///     <paramref name="minSubkeyCount" />, sorted descending by subkey count. This is the primary signal
    ///     for "runaway growth" bugs -- a single key that accumulates an ever-increasing number of subkeys
    ///     without ever pruning old entries (as opposed to normal registry usage, where subkey counts under
    ///     any one key rarely exceed a few hundred).
    /// </summary>
    public static List<KeySubkeyCount> GetTopSubkeyCounts(RegistryHive hive, int minSubkeyCount = 50)
    {
        if (hive?.Root == null)
        {
            throw new ArgumentException("Hive must be parsed (hive.Root must not be null) before analysis.", nameof(hive));
        }

        var results = new List<KeySubkeyCount>();

        void Walk(RegistryKey key)
        {
            if (key.SubKeys.Count >= minSubkeyCount)
            {
                results.Add(new KeySubkeyCount
                {
                    KeyPath = key.KeyPath,
                    SubkeyCount = key.SubKeys.Count,
                    ValueCount = key.Values.Count
                });
            }

            foreach (var subKey in key.SubKeys)
            {
                Walk(subKey);
            }
        }

        Walk(hive.Root);

        return results.OrderByDescending(r => r.SubkeyCount).ToList();
    }

    /// <summary>
    ///     Walks the hive's key tree and reports every key whose direct value count meets or exceeds
    ///     <paramref name="minValueCount" />, sorted descending. Less commonly the source of pathological
    ///     bloat than subkey explosion, but still a useful signal (e.g. a single key accumulating thousands
    ///     of individually-named values instead of subkeys).
    /// </summary>
    public static List<KeyValueCount> GetTopValueCounts(RegistryHive hive, int minValueCount = 50)
    {
        if (hive?.Root == null)
        {
            throw new ArgumentException("Hive must be parsed (hive.Root must not be null) before analysis.", nameof(hive));
        }

        var results = new List<KeyValueCount>();

        void Walk(RegistryKey key)
        {
            if (key.Values.Count >= minValueCount)
            {
                results.Add(new KeyValueCount
                {
                    KeyPath = key.KeyPath,
                    ValueCount = key.Values.Count
                });
            }

            foreach (var subKey in key.SubKeys)
            {
                Walk(subKey);
            }
        }

        Walk(hive.Root);

        return results.OrderByDescending(r => r.ValueCount).ToList();
    }

    /// <summary>
    ///     Buckets all in-use/free cell records by their absolute offset into fixed-size ranges (default
    ///     128MB), reporting in-use vs. free counts per bucket. Useful for visualizing where in a large file
    ///     the bulk of activity/bloat is concentrated.
    /// </summary>
    public static List<OffsetBucket> GetOffsetDistribution(RegistryHive hive, long bucketSizeBytes = 128L * 1024 * 1024)
    {
        if (hive?.CellRecords == null)
        {
            throw new ArgumentException("Hive must be parsed before analysis.", nameof(hive));
        }

        if (bucketSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketSizeBytes), "Must be greater than zero.");
        }

        return hive.CellRecords.Values
            .GroupBy(c => c.AbsoluteOffset / bucketSizeBytes)
            .OrderBy(g => g.Key)
            .Select(g => new OffsetBucket
            {
                RangeStart = g.Key * bucketSizeBytes,
                RangeEnd = g.Key * bucketSizeBytes + bucketSizeBytes,
                InUseCount = g.Count(c => !c.IsFree),
                FreeCount = g.Count(c => c.IsFree)
            })
            .ToList();
    }

    /// <summary>
    ///     Finds the specified key by path (case-insensitive, backslash-delimited, starting from Root's own
    ///     name e.g. "ROOT\Software\...") and, if found, reports its subkeys grouped by a "template" name
    ///     derived by stripping trailing "#" + digits counters and GUID-like segments. This is useful for
    ///     confirming a suspected incrementing-counter bloat pattern (e.g. "{GUID}#1", "{GUID}#2", ...) and
    ///     estimating how many of the oldest entries could be safely pruned.
    /// </summary>
    public static KeyKeyTemplateReport AnalyzeSubkeyNamingPattern(RegistryHive hive, string keyPath)
    {
        var key = hive.GetKey(keyPath);
        if (key == null)
        {
            return null;
        }

        var counterPattern = new System.Text.RegularExpressions.Regex(@"#\d+$");

        var templates = key.SubKeys
            .GroupBy(sk => counterPattern.Replace(sk.KeyName, "#N"))
            .Select(g => new SubkeyTemplateGroup
            {
                Template = g.Key,
                Count = g.Count(),
                OldestLastWrite = g.Min(sk => sk.LastWriteTime),
                NewestLastWrite = g.Max(sk => sk.LastWriteTime)
            })
            .OrderByDescending(g => g.Count)
            .ToList();

        return new KeyKeyTemplateReport
        {
            KeyPath = key.KeyPath,
            TotalSubkeys = key.SubKeys.Count,
            TemplateGroups = templates
        };
    }
}

/// <summary>
///     Result of <see cref="HiveAnalyzer.AnalyzeReachability" />.
/// </summary>
public class ReachabilityReport
{
    public long ReachableKeys { get; set; }
    public long ReachableValues { get; set; }
    public long TotalInUseKeyCells { get; set; }
    public long TotalInUseValueCells { get; set; }

    public long OrphanedKeyCells => TotalInUseKeyCells - ReachableKeys;
    public long OrphanedValueCells => TotalInUseValueCells - ReachableValues;
}

/// <summary>
///     Result entry from <see cref="HiveAnalyzer.FindDuplicateCellGroups" />.
/// </summary>
public class DuplicateCellGroup
{
    public string Signature { get; set; }
    public int Count { get; set; }
    public int SampleSize { get; set; }
    public List<long> SampleOffsets { get; set; }
    public long EstimatedWastedBytes { get; set; }
}

/// <summary>
///     Result entry from <see cref="HiveAnalyzer.GetTopSubkeyCounts" />.
/// </summary>
public class KeySubkeyCount
{
    public string KeyPath { get; set; }
    public int SubkeyCount { get; set; }
    public int ValueCount { get; set; }
}

/// <summary>
///     Result entry from <see cref="HiveAnalyzer.GetTopValueCounts" />.
/// </summary>
public class KeyValueCount
{
    public string KeyPath { get; set; }
    public int ValueCount { get; set; }
}

/// <summary>
///     Result entry from <see cref="HiveAnalyzer.GetOffsetDistribution" />.
/// </summary>
public class OffsetBucket
{
    public long RangeStart { get; set; }
    public long RangeEnd { get; set; }
    public int InUseCount { get; set; }
    public int FreeCount { get; set; }
}

/// <summary>
///     Result of <see cref="HiveAnalyzer.AnalyzeSubkeyNamingPattern" />.
/// </summary>
public class KeyKeyTemplateReport
{
    public string KeyPath { get; set; }
    public int TotalSubkeys { get; set; }
    public List<SubkeyTemplateGroup> TemplateGroups { get; set; }
}

/// <summary>
///     One "template" group (subkey names with trailing "#N" counters normalized) within a
///     <see cref="KeyKeyTemplateReport" />.
/// </summary>
public class SubkeyTemplateGroup
{
    public string Template { get; set; }
    public int Count { get; set; }
    public DateTimeOffset? OldestLastWrite { get; set; }
    public DateTimeOffset? NewestLastWrite { get; set; }
}
