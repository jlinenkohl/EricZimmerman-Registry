using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommandLine;
using Registry;
using Registry.Analysis;
using Registry.Cells;
using Registry.Compaction;
using Serilog;
using Serilog.Events;

namespace RegistryToolkit;

internal class Program
{
    private static int Main(string[] args)
    {
        var exitCode = 1;

        Parser.Default.ParseArguments<Options>(args)
            .WithParsed(options => { exitCode = Run(options); })
            .WithNotParsed(_ =>
            {
                Console.WriteLine(Options.GetUsage());
                exitCode = 1;
            });

        return exitCode;
    }

    private static int Run(Options options)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        StreamWriter integrityLogWriter = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.IntegrityLogPath))
            {
                var integrityDir = Path.GetDirectoryName(options.IntegrityLogPath);
                if (!string.IsNullOrWhiteSpace(integrityDir) && !Directory.Exists(integrityDir))
                {
                    Directory.CreateDirectory(integrityDir);
                }

                integrityLogWriter = new StreamWriter(options.IntegrityLogPath, append: true) { AutoFlush = true };
                Console.SetOut(new TeeTextWriter(originalOut, integrityLogWriter));
                Console.SetError(new TeeTextWriter(originalError, integrityLogWriter));
            }

            return RunCore(options);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            integrityLogWriter?.Dispose();
        }
    }

    private static int RunCore(Options options)
    {
        Console.WriteLine(Options.GetUsage());

        if (string.IsNullOrWhiteSpace(options.HivePath) || !File.Exists(options.HivePath))
        {
            Console.WriteLine($"Hive file not found: {options.HivePath}");
            return 1;
        }

        ConfigureLogging(options.VerboseLevel);
        ConfigureParseSettings(options);

        var discoveredLogs = ResolveLogPaths(options).ToList();
        foreach (var logFile in discoveredLogs)
        {
            PrintTransactionLogSummary(logFile);
        }

        RegistryHive originalHive;

        try
        {
            originalHive = new RegistryHive(options.HivePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load hive: {ex.Message}");
            return 1;
        }

        PrintHiveSummary("Original hive", options.HivePath, originalHive, options.RecoverDeleted);

        if (options.AnalyzeBloat)
        {
            PrintBloatAnalysis(originalHive, options);
        }

        if (!string.IsNullOrWhiteSpace(options.PruneKeyPath))
        {
            RunPruneKey(originalHive, options);
            return 0;
        }

        if (options.Compact)
        {
            RunCompact(originalHive, options);
            return 0;
        }

        byte[] correctedBytes = null;

        var hiveIsDirty = originalHive.Header.PrimarySequenceNumber != originalHive.Header.SecondarySequenceNumber;

        if (hiveIsDirty)
        {
            if (discoveredLogs.Count == 0)
            {
                if (!options.Integrity && !options.Exceeds2GbRecovery)
                {
                    Console.WriteLine("Hive is dirty but no transaction logs were supplied or discovered.");
                }
                else
                {
                    Console.WriteLine("Hive is dirty and no transaction logs were supplied or discovered. Continuing due to integrity/recovery mode.");
                }
            }
            else
            {
                try
                {
                    correctedBytes = originalHive.ProcessTransactionLogs(discoveredLogs);
                    Console.WriteLine($"Transaction logs replayed successfully using {discoveredLogs.Count} log file(s).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Transaction log replay failed: {ex.Message}");
                }
            }
        }
        else
        {
            Console.WriteLine("Hive is not dirty (sequence numbers match). Transaction log replay not required.");
        }

        if (correctedBytes != null)
        {
            var correctedHiveName = options.OutputPath ?? "<in-memory corrected hive>";

            try
            {
                var correctedHive = new RegistryHive(correctedBytes, correctedHiveName);
                PrintHiveSummary("Corrected hive", correctedHiveName, correctedHive, options.RecoverDeleted);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Corrected hive could not be reloaded: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                try
                {
                    var outputDir = Path.GetDirectoryName(options.OutputPath);
                    if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                    }

                    File.WriteAllBytes(options.OutputPath, correctedBytes);
                    Console.WriteLine($"Corrected hive written to: {options.OutputPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unable to write corrected hive: {ex.Message}");
                    return 1;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            Console.WriteLine("No corrected hive bytes were produced, so --output was not written.");
            return 1;
        }

        return 0;
    }

    private static void ConfigureLogging(int verbosity)
    {
        if (verbosity < 0)
        {
            verbosity = 0;
        }

        if (verbosity > 2)
        {
            verbosity = 2;
        }

        var minimumLevel = LogEventLevel.Information;

        if (verbosity == 1)
        {
            minimumLevel = LogEventLevel.Debug;
        }
        else if (verbosity == 2)
        {
            minimumLevel = LogEventLevel.Verbose;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.Console()
            .CreateLogger();
    }

    private static void ConfigureParseSettings(Options options)
    {
        RegistryParseSettings.ContinueOnCorruption = options.Integrity || options.Exceeds2GbRecovery;
        RegistryParseSettings.Exceeds2GbRecovery = options.Exceeds2GbRecovery;
        RegistryParseSettings.CorruptionLogPath = options.IntegrityLogPath;

        if (RegistryParseSettings.ContinueOnCorruption)
        {
            Console.WriteLine(
                $"Integrity parsing enabled. ContinueOnCorruption={RegistryParseSettings.ContinueOnCorruption}, Exceeds2GbRecovery={RegistryParseSettings.Exceeds2GbRecovery}");
        }

        if (!string.IsNullOrWhiteSpace(RegistryParseSettings.CorruptionLogPath))
        {
            Console.WriteLine($"Corruption details will be written to: {RegistryParseSettings.CorruptionLogPath}");
        }
    }

    private static IEnumerable<string> ResolveLogPaths(Options options)
    {
        var logPaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.Log1Path))
        {
            logPaths.Add(options.Log1Path);
        }

        if (!string.IsNullOrWhiteSpace(options.Log2Path))
        {
            logPaths.Add(options.Log2Path);
        }

        if (!options.DisableAutoLogs)
        {
            var candidates = new[]
            {
                options.HivePath + ".LOG1",
                options.HivePath + ".LOG2",
                options.HivePath + ".log1",
                options.HivePath + ".log2"
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    logPaths.Add(candidate);
                }
            }
        }

        return logPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void PrintTransactionLogSummary(string logFile)
    {
        try
        {
            var transactionLog = new TransactionLog(logFile);
            transactionLog.ParseLog();

            Console.WriteLine("================================================================================");
            Console.WriteLine($"Transaction log: {logFile}");
            Console.WriteLine($"Header checksum valid: {transactionLog.Header.ValidateCheckSum()}");
            Console.WriteLine($"Header sequence: primary=0x{transactionLog.Header.PrimarySequenceNumber:X8}, secondary=0x{transactionLog.Header.SecondarySequenceNumber:X8}");
            Console.WriteLine($"Entries: {transactionLog.TransactionLogEntries.Count:N0}");

            for (var i = 0; i < transactionLog.TransactionLogEntries.Count; i++)
            {
                var entry = transactionLog.TransactionLogEntries[i];
                Console.WriteLine($"  Entry #{i}: sequence=0x{entry.SequenceNumber:X8}, dirtyPages={entry.DirtyPageCount:N0}, hashValid={entry.HasValidHashes()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unable to parse transaction log '{logFile}': {ex.Message}");
        }
    }

    private static void PrintHiveSummary(string label, string source, RegistryHive hive, bool recoverDeleted)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine(label);
        Console.WriteLine($"Source: {source}");
        Console.WriteLine($"Embedded hive name: {hive.Header.FileName}");
        Console.WriteLine($"Hive type: {hive.HiveType}");
        Console.WriteLine($"Version: {hive.Version}");
        Console.WriteLine($"Header last write (UTC): {hive.Header.LastWriteTimestamp:O}");
        Console.WriteLine($"Header length: 0x{hive.Header.Length:X8} ({hive.Header.Length:N0})");
        Console.WriteLine($"File byte length: {hive.FileBytes.Length:N0}");
        Console.WriteLine($"Checksum valid: {hive.Header.ValidateCheckSum()} (header=0x{hive.Header.CheckSum:X8}, calculated=0x{hive.Header.CalculatedChecksum:X8})");
        Console.WriteLine($"Sequence numbers: primary=0x{hive.Header.PrimarySequenceNumber:X8}, secondary=0x{hive.Header.SecondarySequenceNumber:X8}, dirty={hive.Header.PrimarySequenceNumber != hive.Header.SecondarySequenceNumber}");

        var declaredTotalLength = (ulong)hive.Header.Length + 0x1000UL;
        var loadedLength = (ulong)hive.FileBytes.Length;

        if (declaredTotalLength > loadedLength)
        {
            var missingBytes = declaredTotalLength - loadedLength;
            Console.WriteLine(
                $"WARNING: This hive declares a total length of 0x{declaredTotalLength:X} bytes, but only 0x{loadedLength:X} bytes were loaded ({missingBytes:N0} bytes / ~{missingBytes / 4096:N0} potential hbins were not read). The statistics below reflect ONLY the loaded portion of the hive and are therefore incomplete.");
        }
        else if (loadedLength > declaredTotalLength)
        {
            var extraBytes = loadedLength - declaredTotalLength;
            Console.WriteLine(
                $"Note: {extraBytes:N0} bytes beyond the hive's declared length (0x{declaredTotalLength:X}) were loaded but are outside the hive itself (e.g. trailing padding/slack space) and are excluded from parsing.");
        }

        hive.RecoverDeleted = recoverDeleted;
        hive.FlushRecordListsAfterParse = false;

        try
        {
            hive.ParseHive();

            var freeCells = hive.CellRecords.Count(t => t.Value.IsFree);
            var freeLists = hive.ListRecords.Count(t => t.Value.IsFree);
            var nkCount = hive.CellRecords.Count(t => t.Value is NkCellRecord);
            var vkCount = hive.CellRecords.Count(t => t.Value is VkCellRecord);
            var skCount = hive.CellRecords.Count(t => t.Value is SkCellRecord);
            var lkCount = hive.CellRecords.Count(t => t.Value is LkCellRecord);

            Console.WriteLine($"hbin records: {hive.HBinRecordCount:N0}, total hbin bytes: 0x{hive.HBinRecordTotalSize:X}");
            Console.WriteLine($"cell records: {hive.CellRecords.Count:N0} (nk={nkCount:N0}, vk={vkCount:N0}, sk={skCount:N0}, lk={lkCount:N0}, free={freeCells:N0})");
            Console.WriteLine($"list records: {hive.ListRecords.Count:N0} (free={freeLists:N0})");
            Console.WriteLine($"hard parsing errors: {hive.HardParsingErrors:N0}");
            Console.WriteLine($"soft parsing errors: {hive.SoftParsingErrors:N0}");

            if (recoverDeleted)
            {
                Console.WriteLine($"recovered deleted keys: {hive.DeletedRegistryKeys.Count:N0}");
                Console.WriteLine($"recovered unassociated values: {hive.UnassociatedRegistryValues.Count:N0}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Parse error: {ex.Message}");
            Console.WriteLine($"hard parsing errors (before failure): {hive.HardParsingErrors:N0}");
            Console.WriteLine($"soft parsing errors (before failure): {hive.SoftParsingErrors:N0}");
        }
    }

    private static void PrintBloatAnalysis(RegistryHive hive, Options options)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("Bloat / integrity analysis (--analyzeBloat)");

        try
        {
            var reachability = HiveAnalyzer.AnalyzeReachability(hive);
            Console.WriteLine(
                $"Reachability: reachableKeys={reachability.ReachableKeys:N0}, reachableValues={reachability.ReachableValues:N0}, totalInUseKeyCells={reachability.TotalInUseKeyCells:N0}, totalInUseValueCells={reachability.TotalInUseValueCells:N0}");

            var sizeBreakdown = HiveAnalyzer.GetCellSizeBreakdown(hive);
            Console.WriteLine("Byte-size breakdown (where the file's bytes actually go):");
            Console.WriteLine($"  Total hive size:      {sizeBreakdown.TotalHiveBytes:N0} bytes (0x{sizeBreakdown.TotalHiveBytes:X})");
            Console.WriteLine($"  NK (key) cells:       {sizeBreakdown.NkCellBytes:N0} bytes");
            Console.WriteLine($"  VK (value) cells:     {sizeBreakdown.VkCellBytes:N0} bytes (headers only; excludes value data)");
            Console.WriteLine($"  Value data cells:     {sizeBreakdown.ValueDataBytes:N0} bytes (non-resident value payloads, incl. 'big data' fragments)");
            Console.WriteLine($"  SK (security) cells:  {sizeBreakdown.SkCellBytes:N0} bytes");
            Console.WriteLine($"  LK cells:             {sizeBreakdown.LkCellBytes:N0} bytes");
            foreach (var kvp in sizeBreakdown.ListRecordBytesBySignature.OrderByDescending(k => k.Value))
            {
                Console.WriteLine($"  '{kvp.Key}' list records:    {kvp.Value:N0} bytes");
            }
            Console.WriteLine($"  Free/deleted cells:   {sizeBreakdown.FreeCellBytes:N0} bytes");
            Console.WriteLine($"  Free/deleted lists:   {sizeBreakdown.FreeListBytes:N0} bytes");
            Console.WriteLine($"  hbin/regf headers:    {(sizeBreakdown.HbinHeaderBytes + sizeBreakdown.RegfHeaderBytes):N0} bytes");
            Console.WriteLine($"  Unaccounted:          {sizeBreakdown.UnaccountedBytes:N0} bytes ({(sizeBreakdown.TotalHiveBytes == 0 ? 0 : (double) sizeBreakdown.UnaccountedBytes / sizeBreakdown.TotalHiveBytes):P1} of file)");
            if (sizeBreakdown.UnaccountedBytes > sizeBreakdown.TotalHiveBytes / 20)
            {
                Console.WriteLine(
                    "  Note: a large unaccounted share is most commonly free/deleted 'data' cells (never wrapped in a typed record and therefore untracked here) and/or hbin slack space -- run with -r/--recover-deleted for deeper visibility into deleted content.");
            }

            var topSubkeys = HiveAnalyzer.GetTopSubkeyCounts(hive);
            Console.WriteLine($"Top subkey-count keys (>= 50 subkeys): {topSubkeys.Count}");
            foreach (var entry in topSubkeys.OrderByDescending(k => k.SubkeyCount).Take(10))
            {
                Console.WriteLine($"  {entry.SubkeyCount:N0} subkeys, {entry.ValueCount:N0} values: {entry.KeyPath}");
            }

            var topValues = HiveAnalyzer.GetTopValueCounts(hive);
            Console.WriteLine($"Top value-count keys (>= 50 values): {topValues.Count}");
            foreach (var entry in topValues.OrderByDescending(k => k.ValueCount).Take(10))
            {
                Console.WriteLine($"  {entry.ValueCount:N0} values: {entry.KeyPath}");
            }

            var topBySize = HiveAnalyzer.GetTopKeysBySize(hive);
            Console.WriteLine($"Top keys by total subtree size (>= 1 MB): {topBySize.Count}");
            foreach (var entry in topBySize.Take(10))
            {
                Console.WriteLine(
                    $"  {entry.SubtreeBytes:N0} bytes across {entry.SubtreeKeyCount:N0} keys ({entry.SubkeyCount:N0} direct subkeys, {entry.ValueCount:N0} direct values): {entry.KeyPath}");
            }

            if (topSubkeys.Count > 0)
            {
                var biggest = topSubkeys.OrderByDescending(k => k.SubkeyCount).First();
                var pattern = HiveAnalyzer.AnalyzeSubkeyNamingPattern(hive, biggest.KeyPath);

                if (pattern != null && pattern.TemplateGroups.Count > 0)
                {
                    Console.WriteLine(
                        $"Subkey naming pattern under largest key '{pattern.KeyPath}' ({pattern.TotalSubkeys:N0} subkeys):");
                    foreach (var group in pattern.TemplateGroups.OrderByDescending(g => g.Count).Take(5))
                    {
                        Console.WriteLine(
                            $"  Template '{group.Template}': {group.Count:N0} matches, oldest={group.OldestLastWrite:O}, newest={group.NewestLastWrite:O}");
                    }

                    Console.WriteLine(
                        "  If this pattern shows a large count with an incrementing counter suffix and a wide LastWrite spread, it likely indicates an application/service bug (e.g. an unbounded cache) rather than corruption. Consider --pruneKey to remediate.");
                }
            }

            // Rank the "top offender" for a suggested --pruneKey command using only keys that are themselves
            // the *direct* holder of a large subkey count (topSubkeys, count >= 50) as the candidate pool --
            // ranking every key in the hive by subtree bytes alone (including ancestors) would always pick
            // the root/near-root keys, since a parent's subtree necessarily contains its children's bytes,
            // identifying "where the bytes are" but not an actionable, specific --pruneKey target. An ancestor
            // of another candidate is excluded from the pool for the same reason (its subtree bytes trivially
            // include the descendant candidate's, without itself being the runaway-growth point). Within the
            // remaining, structurally-independent candidates, rank by total subtree byte size (falling back to
            // subkey count as a tiebreak) so the selection reflects both count *and* total cell size consumed,
            // per user request, rather than count alone.
            var candidatePaths = topSubkeys
                .Select(s => new
                {
                    s.KeyPath,
                    s.SubkeyCount,
                    SubtreeBytes = topBySize.FirstOrDefault(b => b.KeyPath == s.KeyPath)?.SubtreeBytes ?? 0
                })
                .ToList();

            var offenderCandidates = candidatePaths
                .Where(c => !candidatePaths.Any(other =>
                    !string.Equals(other.KeyPath, c.KeyPath, StringComparison.OrdinalIgnoreCase) &&
                    other.KeyPath.StartsWith(c.KeyPath + "\\", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c => c.SubtreeBytes)
                .ThenByDescending(c => c.SubkeyCount)
                .FirstOrDefault();

            if (offenderCandidates != null)
            {
                var suggestedOutput = string.IsNullOrWhiteSpace(options.OutputPath)
                    ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.HivePath)) ?? ".",
                        Path.GetFileNameWithoutExtension(options.HivePath) + "_pruned" + Path.GetExtension(options.HivePath))
                    : options.OutputPath;

                Console.WriteLine(
                    $"Top offender (ranked by subkey count AND total subtree bytes): '{offenderCandidates.KeyPath}' -- {offenderCandidates.SubkeyCount:N0} subkeys, ~{offenderCandidates.SubtreeBytes:N0} bytes.");
                Console.WriteLine("Suggested remediation command:");
                Console.WriteLine(
                    $"  RegistryToolkit -f \"{options.HivePath}\" --pruneKey \"{offenderCandidates.KeyPath}\" --keepRecent 500 -o \"{suggestedOutput}\"");
            }

            var duplicates = HiveAnalyzer.FindDuplicateCellGroups(hive, 16);
            var significantDuplicates = duplicates.Where(d => d.Count >= 1000).OrderByDescending(d => d.Count).Take(5).ToList();
            if (significantDuplicates.Count > 0)
            {
                Console.WriteLine("Large duplicate-signature cell groups (first 16 bytes of payload):");
                foreach (var dup in significantDuplicates)
                {
                    Console.WriteLine($"  {dup.Count:N0} cells share signature {dup.Signature}, ~{dup.EstimatedWastedBytes:N0} bytes");
                }

                Console.WriteLine(
                    "  Note: large duplicate-signature groups are commonly caused by many sibling keys sharing the same small set of value names/sizes (templated data), not literal record duplication/corruption. Cross-reference with the subkey naming pattern report above.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bloat analysis failed: {ex.Message}");
        }
    }

    private static void RunPruneKey(RegistryHive hive, Options options)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine($"Pruning key: {options.PruneKeyPath} (keeping {options.KeepRecent:N0} most-recently-written subkeys)");

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            Console.WriteLine("ERROR: --pruneKey requires -o/--output to specify where the pruned hive should be written.");
            return;
        }

        try
        {
            var plan = HivePruner.PlanPrune(hive, options.PruneKeyPath, options.KeepRecent);
            Console.WriteLine(
                $"Plan: totalSubkeys={plan.TotalSubkeyCount:N0}, retaining={plan.RetainCount:N0}, pruning={plan.PruneCount:N0}");

            if (plan.PruneCount == 0)
            {
                Console.WriteLine("Nothing to prune (subkey count is already at or below --keepRecent).");
                return;
            }

            var outputDir = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            HivePruner.PruneKeyToFile(hive, options.PruneKeyPath, options.KeepRecent, options.OutputPath);
            Console.WriteLine($"Pruned hive written to: {options.OutputPath}");

            var prunedFileInfo = new FileInfo(options.OutputPath);
            var originalFileInfo = new FileInfo(hive.HivePath);
            Console.WriteLine(
                $"Size before: {originalFileInfo.Length:N0} bytes, after: {prunedFileInfo.Length:N0} bytes ({(originalFileInfo.Length - prunedFileInfo.Length):N0} bytes removed)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Prune failed: {ex.Message}");
        }
    }

    private static void RunCompact(RegistryHive hive, Options options)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("Compacting hive (--compact)");

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            Console.WriteLine("ERROR: --compact requires -o/--output to specify where the compacted hive should be written.");
            return;
        }

        try
        {
            var outputDir = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var skeleton = new RegistrySkeleton(hive);
            var rootEntry = new SkeletonKeyRoot(hive.Root.KeyName, true, true);
            skeleton.AddEntry(rootEntry);
            skeleton.Write(options.OutputPath);

            Console.WriteLine($"Compacted hive written to: {options.OutputPath}");

            var compactedFileInfo = new FileInfo(options.OutputPath);
            var originalFileInfo = new FileInfo(hive.HivePath);
            Console.WriteLine(
                $"Size before: {originalFileInfo.Length:N0} bytes, after: {compactedFileInfo.Length:N0} bytes ({(originalFileInfo.Length - compactedFileInfo.Length):N0} bytes removed). This drops free/deleted cell slack but keeps all live keys/values; use --pruneKey to actually remove bloated key data.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Compaction failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Forwards every write to two underlying TextWriters. Used so that, when --integrityLog is
/// specified, the full console transcript (stdout/stderr) is mirrored into the integrity log file
/// alongside the structured corruption entries recorded via RegistryParseSettings.RecordCorruption.
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override System.Text.Encoding Encoding => _first.Encoding;

    public override void Write(char value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void Write(string value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void WriteLine(string value)
    {
        _first.WriteLine(value);
        _second.WriteLine(value);
    }

    public override void Flush()
    {
        _first.Flush();
        _second.Flush();
    }
}
