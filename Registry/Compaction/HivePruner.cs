using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Abstractions;

namespace Registry.Compaction;

/// <summary>
///     Provides a high-level, targeted "prune and rewrite" workflow for cleaning up a specific bloated key in
///     a hive (e.g. a key with an unbounded/ever-growing number of subkeys, such as the Windows Remote Desktop
///     Client's <c>Software\Microsoft\RdClientRadc\DiagConnectionCache</c> diagnostic-cache bug -- see
///     <c>docs/NTUSER_BLOAT_FINDINGS.md</c>).
///     <remarks>
///         This builds on <see cref="RegistrySkeleton" />, which already knows how to reproduce an existing
///         hive's structure (headers, hbins, NK/VK/SK records, checksums, subkey/value list chaining including
///         "ri"-chained "lf" lists for large subkey counts) into a fresh, valid hive file. HivePruner adds:
///         (1) a simple retention policy (keep only the N most-recently-written subkeys under a target key),
///         and (2) the plumbing to copy everything else in the hive unchanged while excluding the pruned
///         subkeys from the copy.
///     </remarks>
/// </summary>
public static class HivePruner
{
    /// <summary>
    ///     Produces a plan describing which subkeys under <paramref name="keyPath" /> would be pruned if
    ///     <paramref name="retainCount" /> most-recently-written subkeys were kept. Does not modify anything;
    ///     use this to preview the effect of a prune before calling <see cref="PruneKeyToFile" />.
    /// </summary>
    /// <param name="hive">A parsed <see cref="RegistryHive" />.</param>
    /// <param name="keyPath">The path (relative to the hive root, e.g. "Software\Microsoft\...") of the key whose subkeys should be pruned.</param>
    /// <param name="retainCount">The number of most-recently-written subkeys to keep. All others are marked for removal.</param>
    public static PrunePlan PlanPrune(RegistryHive hive, string keyPath, int retainCount)
    {
        if (hive == null) throw new ArgumentNullException(nameof(hive));
        if (retainCount < 0) throw new ArgumentOutOfRangeException(nameof(retainCount), "Must be zero or greater.");

        var key = hive.GetKey(keyPath);
        if (key == null)
        {
            throw new ArgumentException($"Key not found: {keyPath}", nameof(keyPath));
        }

        // Sort newest-first so the retained set is the most recently written (most likely to still be relevant).
        var orderedSubkeys = key.SubKeys
            .OrderByDescending(sk => sk.LastWriteTime ?? DateTimeOffset.MinValue)
            .ToList();

        var toRetain = orderedSubkeys.Take(retainCount).ToList();
        var toPrune = orderedSubkeys.Skip(retainCount).ToList();

        return new PrunePlan
        {
            KeyPath = key.KeyPath,
            TotalSubkeyCount = orderedSubkeys.Count,
            RetainCount = toRetain.Count,
            PruneCount = toPrune.Count,
            SubkeysToPrune = toPrune.Select(sk => sk.KeyPath).ToList(),
            SubkeysToRetain = toRetain.Select(sk => sk.KeyPath).ToList()
        };
    }

    /// <summary>
    ///     Rewrites the entire hive to <paramref name="outputPath" />, keeping everything intact except the
    ///     subkeys of <paramref name="keyPath" /> beyond the <paramref name="retainCount" /> most-recently
    ///     written, which are dropped entirely (their NK/VK/SK cells will not exist in the output hive).
    /// </summary>
    /// <param name="hive">A parsed <see cref="RegistryHive" />.</param>
    /// <param name="keyPath">The path of the key whose subkeys should be pruned.</param>
    /// <param name="retainCount">The number of most-recently-written subkeys to keep.</param>
    /// <param name="outputPath">Path to write the resulting, compacted hive to.</param>
    /// <returns>The plan that was executed (see <see cref="PlanPrune" />).</returns>
    public static PrunePlan PruneKeyToFile(RegistryHive hive, string keyPath, int retainCount, string outputPath)
    {
        var plan = PlanPrune(hive, keyPath, retainCount);

        var skeleton = new RegistrySkeleton(hive);

        // Exclude only the specific subkeys selected for pruning; everything else (including the target key
        // itself and its retained subkeys) is copied through unchanged.
        foreach (var pruneKeyPath in plan.SubkeysToPrune)
        {
            skeleton.ExcludeSubtree(pruneKeyPath);
        }

        // Reproduce the entire hive from the root down (recursively, including values) directly against the
        // real RegistryKey tree, bypassing AddEntry/BuildKeyTree/SkeletonKey's redundant, fully-materialized
        // duplicate tree copies -- see WriteWholeHive's remarks for why that matters for large hives.
        skeleton.WriteWholeHive(outputPath);

        return plan;
    }
}

/// <summary>
///     Describes the outcome of a planned (or executed) prune operation. See
///     <see cref="HivePruner.PlanPrune" />.
/// </summary>
public class PrunePlan
{
    public string KeyPath { get; set; }
    public int TotalSubkeyCount { get; set; }
    public int RetainCount { get; set; }
    public int PruneCount { get; set; }
    public List<string> SubkeysToPrune { get; set; }
    public List<string> SubkeysToRetain { get; set; }
}
