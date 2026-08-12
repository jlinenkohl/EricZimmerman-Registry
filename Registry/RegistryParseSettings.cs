using System;
using System.IO;
using Serilog;

namespace Registry;

public static class RegistryParseSettings
{
    private static readonly object SyncRoot = new();

    /// <summary>
    ///     When true, known parse-failure conditions will be logged and parsing will continue where possible.
    /// </summary>
    public static bool ContinueOnCorruption { get; set; }

    /// <summary>
    ///     Destination file for verbose corruption/salvage messages.
    /// </summary>
    public static string CorruptionLogPath { get; set; } =
        Path.Combine(Path.GetTempPath(), "registry-corruption-recovery.log");

    internal static void ReportCorruption(string condition, Exception ex = null)
    {
        if (!ContinueOnCorruption)
        {
            return;
        }

        try
        {
            var line = ex == null
                ? $"{DateTimeOffset.UtcNow:o} | {condition}{Environment.NewLine}"
                : $"{DateTimeOffset.UtcNow:o} | {condition} | {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}";

            lock (SyncRoot)
            {
                File.AppendAllText(CorruptionLogPath, line);
            }
        }
        catch (Exception localEx)
        {
            Log.Debug(localEx, "Unable to append corruption details to log file {CorruptionLogPath}", CorruptionLogPath);
        }
    }
}
