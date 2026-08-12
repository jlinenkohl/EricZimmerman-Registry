using System;
using System.IO;

namespace Registry;

public static class RegistryParseSettings
{
    private static readonly object _sync = new();

    public static bool ContinueOnCorruption { get; set; }
    public static bool Exceeds2GbRecovery { get; set; }
    public static string CorruptionLogPath { get; set; }

    internal static void RecordCorruption(string message)
    {
        if (string.IsNullOrWhiteSpace(CorruptionLogPath))
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
                File.AppendAllText(CorruptionLogPath, line);
            }
            catch
            {
            }
        }
    }
}
