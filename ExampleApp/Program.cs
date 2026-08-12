using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommandLine;
using Registry;
using Registry.Cells;
using Serilog;
using Serilog.Events;

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
        Console.WriteLine(Options.GetUsage());

        if (string.IsNullOrWhiteSpace(options.HivePath) || !File.Exists(options.HivePath))
        {
            Console.WriteLine($"Hive file not found: {options.HivePath}");
            return 1;
        }

        ConfigureLogging(options.VerboseLevel);

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

        byte[] correctedBytes = null;

        var hiveIsDirty = originalHive.Header.PrimarySequenceNumber != originalHive.Header.SecondarySequenceNumber;

        if (hiveIsDirty)
        {
            if (discoveredLogs.Count == 0)
            {
                Console.WriteLine("Hive is dirty but no transaction logs were supplied or discovered.");
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
}
