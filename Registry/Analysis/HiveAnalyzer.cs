using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Registry.Abstractions;
using Registry.Cells;
using Registry.Lists;

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
    ///     Sums the total on-disk bytes consumed by every category of cell/record in the hive (NK, VK header,
    ///     SK, LK, each subkey-list record signature, and value-data payload cells), plus free-cell/free-list
    ///     bytes. This directly answers "where did the gigabytes go" for a large hive: counts alone
    ///     (<see cref="GetTopSubkeyCounts" />) only show *how many* keys/values exist, not how many bytes they
    ///     and their value data actually consume on disk.
    ///     <remarks>
    ///         Value data is the category most commonly missing from a naive size estimate: a VK cell record
    ///         only stores a small header (name + type + length + a 4-byte pointer/offset), never the value's
    ///         actual bytes (unless the value is "resident", i.e. &lt;= 4 bytes and stored inline). All
    ///         non-resident value data -- which for large hives is typically the overwhelming majority of the
    ///         file -- lives in separate, untyped "data" cells that the base parser does not track as first
    ///         class records at all (see <c>HBinRecord.Process</c>'s <c>DataNode</c> fallback case). This
    ///         method walks every in-use VK record's <c>OffsetToData</c> (and, for "big data" values &gt; 16,344
    ///         bytes, every fragment referenced by its <c>db</c> list record) to size those otherwise-invisible
    ///         cells directly from the hive bytes, without materializing their full payloads in memory.
    ///     </remarks>
    /// </summary>
    public static CellSizeBreakdownReport GetCellSizeBreakdown(RegistryHive hive)
    {
        if (hive?.CellRecords == null)
        {
            throw new ArgumentException("Hive must be parsed before analysis.", nameof(hive));
        }

        var report = new CellSizeBreakdownReport();

        foreach (var cell in hive.CellRecords.Values)
        {
            var size = Math.Abs(cell.Size);

            if (cell.IsFree)
            {
                report.FreeCellBytes += size;
                continue;
            }

            switch (cell.Signature)
            {
                case "nk":
                    report.NkCellBytes += size;
                    break;
                case "vk":
                    report.VkCellBytes += size;
                    break;
                case "sk":
                    report.SkCellBytes += size;
                    break;
                case "lk":
                    report.LkCellBytes += size;
                    break;
            }

            if (cell is VkCellRecord vk)
            {
                report.ValueDataBytes += GetValueDataCellSize(hive, vk);
            }
        }

        foreach (var list in hive.ListRecords.Values)
        {
            var size = Math.Abs(list.Size);

            if (list.IsFree)
            {
                report.FreeListBytes += size;
                continue;
            }

            report.ListRecordBytesBySignature.TryGetValue(list.Signature, out var existing);
            report.ListRecordBytesBySignature[list.Signature] = existing + size;
        }

        report.HbinHeaderBytes = (long) hive.HBinRecordCount * 0x20;
        report.RegfHeaderBytes = 0x1000;
        report.TotalHiveBytes = report.RegfHeaderBytes + hive.HBinRecordTotalSize;

        report.AccountedBytes = report.NkCellBytes + report.VkCellBytes + report.SkCellBytes + report.LkCellBytes
                                 + report.ListRecordBytesBySignature.Values.Sum()
                                 + report.ValueDataBytes + report.FreeCellBytes + report.FreeListBytes
                                 + report.HbinHeaderBytes + report.RegfHeaderBytes;

        report.UnaccountedBytes = report.TotalHiveBytes - report.AccountedBytes;

        return report;
    }

    /// <summary>
    ///     Walks the hive's key tree and, for every key, computes both its own direct byte footprint (its NK
    ///     cell, its values' VK cells, and those values' data cells) and the total footprint of its entire
    ///     subtree (itself plus every descendant), then returns the keys with the largest subtree footprint.
    ///     <remarks>
    ///         This is the byte-size counterpart to <see cref="GetTopSubkeyCounts" />: a key can have a huge
    ///         subkey *count* but a small subkey-list-chain overhead (a few bytes per entry), while its true
    ///         cost lies in the cumulative size of every descendant's NK/VK cells and value data. Ranking by
    ///         subtree bytes (rather than count alone) correctly identifies the actual "top offender" to target
    ///         with <c>--pruneKey</c> when several candidate keys have a similar subkey count but very
    ///         different value-data payloads. Subkey-list-record (lf/lh/li/ri) chain overhead is intentionally
    ///         excluded from per-key totals (it is reported in aggregate by
    ///         <see cref="GetCellSizeBreakdown" /> instead) since it is small and shared/rebuilt as a whole
    ///         chain rather than cleanly attributable to a single key.
    ///     </remarks>
    /// </summary>
    public static List<KeySizeReport> GetTopKeysBySize(RegistryHive hive, int topN = 20, long minSubtreeBytes = 1024 * 1024)
    {
        if (hive?.Root == null)
        {
            throw new ArgumentException("Hive must be parsed (hive.Root must not be null) before analysis.", nameof(hive));
        }

        var results = new List<KeySizeReport>();

        (long directBytes, long subtreeBytes, long subtreeKeyCount) Walk(RegistryKey key)
        {
            long directBytes = Math.Abs(key.NkRecord.Size);

            foreach (var value in key.Values)
            {
                directBytes += Math.Abs(value.VkRecord.Size);
                directBytes += GetValueDataCellSize(hive, value.VkRecord);
            }

            var subtreeBytes = directBytes;
            var subtreeKeyCount = 1L;

            foreach (var subKey in key.SubKeys)
            {
                var (_, childSubtreeBytes, childKeyCount) = Walk(subKey);
                subtreeBytes += childSubtreeBytes;
                subtreeKeyCount += childKeyCount;
            }

            if (subtreeBytes >= minSubtreeBytes)
            {
                results.Add(new KeySizeReport
                {
                    KeyPath = key.KeyPath,
                    DirectBytes = directBytes,
                    SubtreeBytes = subtreeBytes,
                    SubtreeKeyCount = subtreeKeyCount,
                    SubkeyCount = key.SubKeys.Count,
                    ValueCount = key.Values.Count
                });
            }

            return (directBytes, subtreeBytes, subtreeKeyCount);
        }

        Walk(hive.Root);

        return results
            .OrderByDescending(r => r.SubtreeBytes)
            .ThenByDescending(r => r.SubtreeKeyCount)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    ///     Computes the number of bytes consumed by a single VK record's non-resident data cell(s), without
    ///     reading the actual value payload into memory. Returns 0 for resident values (data stored inline in
    ///     the VK cell itself, already counted as part of the VK cell's own size).
    /// </summary>
    private static long GetValueDataCellSize(RegistryHive hive, VkCellRecord vk)
    {
        const uint residentFlag = 0x80000000;

        if ((vk.DataLength & residentFlag) != 0)
        {
            // Data is resident (stored inline in the VK cell itself); no separate data cell exists.
            return 0;
        }

        try
        {
            var sizeRaw = hive.ReadBytesFromHive(4096 + vk.OffsetToData, 4);
            if (sizeRaw.Length < 4)
            {
                return 0;
            }

            var dataBlockSize = Math.Abs(BitConverter.ToInt32(sizeRaw, 0));

            if (vk.DataLength > 16344 && hive.Header.MinorVersion > 3)
            {
                // "Big data" case: the cell at OffsetToData is itself a 'db' list record (already counted
                // in ListRecordBytesBySignature["db"]); walk it to size the offsets-array cell and every
                // fragment cell it points to, none of which are tracked anywhere else.
                var dbRaw = hive.ReadBytesFromHive(4096 + vk.OffsetToData, dataBlockSize);
                var db = new DbListRecord(dbRaw, 4096 + vk.OffsetToData);

                var offsetsSizeRaw = hive.ReadBytesFromHive(4096 + db.OffsetToOffsets, 4);
                var offsetsBlockSize = Math.Abs(BitConverter.ToInt32(offsetsSizeRaw, 0));
                var offsetsRaw = hive.ReadBytesFromHive(4096 + db.OffsetToOffsets, offsetsBlockSize);

                long total = offsetsBlockSize;

                for (var i = 1; i <= db.NumberOfEntries; i++)
                {
                    var fragOffset = BitConverter.ToUInt32(offsetsRaw, i * 4);
                    var fragSizeRaw = hive.ReadBytesFromHive(4096 + fragOffset, 4);
                    total += Math.Abs(BitConverter.ToInt32(fragSizeRaw, 0));
                }

                return total;
            }

            return dataBlockSize;
        }
        catch
        {
            // Best-effort accounting; a malformed/free record's data pointer should not abort analysis.
            return 0;
        }
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
///     Result of <see cref="HiveAnalyzer.GetCellSizeBreakdown" />: total on-disk bytes consumed by each
///     category of cell/record in the hive.
/// </summary>
public class CellSizeBreakdownReport
{
    public long NkCellBytes { get; set; }
    public long VkCellBytes { get; set; }
    public long SkCellBytes { get; set; }
    public long LkCellBytes { get; set; }
    public Dictionary<string, long> ListRecordBytesBySignature { get; } = new();
    public long ValueDataBytes { get; set; }
    public long FreeCellBytes { get; set; }
    public long FreeListBytes { get; set; }
    public long HbinHeaderBytes { get; set; }
    public long RegfHeaderBytes { get; set; }

    /// <summary>The declared total size of the hive (regf header + all hbins).</summary>
    public long TotalHiveBytes { get; set; }

    /// <summary>Sum of every category above.</summary>
    public long AccountedBytes { get; set; }

    /// <summary>
    ///     <see cref="TotalHiveBytes" /> minus <see cref="AccountedBytes" />. A non-trivial value here
    ///     represents bytes the analyzer could not attribute to a specific tracked category -- most commonly
    ///     free/deleted "data" cells (which, unlike free NK/VK/SK/LK cells, are never wrapped in a typed
    ///     record and so are invisible to <c>hive.CellRecords</c>/<c>hive.ListRecords</c> entirely), and any
    ///     other slack/padding space within hbins.
    /// </summary>
    public long UnaccountedBytes { get; set; }
}

/// <summary>
///     Result entry from <see cref="HiveAnalyzer.GetTopKeysBySize" />.
/// </summary>
public class KeySizeReport
{
    public string KeyPath { get; set; }

    /// <summary>Bytes consumed by this key's own NK cell, its values' VK cells, and their value data.</summary>
    public long DirectBytes { get; set; }

    /// <summary>Bytes consumed by this key and every descendant key/value in its subtree.</summary>
    public long SubtreeBytes { get; set; }

    /// <summary>Total number of keys (including itself) in this key's subtree.</summary>
    public long SubtreeKeyCount { get; set; }

    public int SubkeyCount { get; set; }
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
