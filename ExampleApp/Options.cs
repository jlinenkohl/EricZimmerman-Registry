using System.Text;
using CommandLine;

internal class Options
{
    [Option('f', "file", Required = true,
        HelpText = "Path to the registry hive to analyze")]
    public string HivePath { get; set; }

    [Option("log1", Required = false,
        HelpText = "Path to LOG1 transaction log")]
    public string Log1Path { get; set; }

    [Option("log2", Required = false,
        HelpText = "Path to LOG2 transaction log")]
    public string Log2Path { get; set; }

    [Option("no-auto-logs", Default = false, Required = false,
        HelpText = "Disable automatic discovery of adjacent .LOG1/.LOG2 files")]
    public bool DisableAutoLogs { get; set; }

    [Option('r', "recover-deleted", Default = false, Required = false,
        HelpText = "Recover and process deleted Registry keys/values")]
    public bool RecoverDeleted { get; set; }

    [Option('o', "output", Required = false,
        HelpText = "Path to write corrected hive bytes after transaction log replay")]
    public string OutputPath { get; set; }

    [Option("integrity", Default = false, Required = false,
        HelpText = "Enable best-effort integrity parsing and continue on supported corruption conditions")]
    public bool Integrity { get; set; }

    [Option("integrityLog", Required = false,
        HelpText = "Path to write corruption details when --integrity is enabled")]
    public string IntegrityLogPath { get; set; }

    [Option("exceeds2GbRecovery", Default = false, Required = false,
        HelpText = "Enable oversized/truncated hive recovery mode")]
    public bool Exceeds2GbRecovery { get; set; }

    [Option('v', "verbose", Default = 0, Required = false,
        HelpText = "Verbosity level. 0 = Information, 1 = Debug, 2 = Verbose")]
    public int VerboseLevel { get; set; }

    public static string GetUsage()
    {
        var usage = new StringBuilder();
        usage.AppendLine("Registry integrity/recovery analyzer");
        usage.AppendLine("-f, --file <path>         Registry hive to analyze");
        usage.AppendLine("--log1 <path>             Optional LOG1 transaction log path");
        usage.AppendLine("--log2 <path>             Optional LOG2 transaction log path");
        usage.AppendLine("--no-auto-logs            Disable auto-discovery of <file>.LOG1/.LOG2");
        usage.AppendLine("-r, --recover-deleted     Recover deleted keys/values during parsing");
        usage.AppendLine("-o, --output <path>       Write corrected hive bytes after log replay");
        usage.AppendLine("--integrity               Continue on supported corruption conditions");
        usage.AppendLine("--integrityLog <path>     Write corruption details to file");
        usage.AppendLine("--exceeds2GbRecovery      Enable oversized/truncated hive recovery mode");
        usage.AppendLine("-v, --verbose <0|1|2>     Logging verbosity");

        return usage.ToString();
    }
}
