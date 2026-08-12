using System.Collections.Generic;

namespace Registry.Other;

public class IntegrityIssue
{
    public IntegrityIssue(string category, string message, long? absoluteOffset = null, long? relativeOffset = null)
    {
        Category = category;
        Message = message;
        AbsoluteOffset = absoluteOffset;
        RelativeOffset = relativeOffset;
    }

    public string Category { get; }
    public string Message { get; }
    public long? AbsoluteOffset { get; }
    public long? RelativeOffset { get; }
}

public class ParseIntegrityReport
{
    private readonly List<IntegrityIssue> _issues = new();

    public IReadOnlyList<IntegrityIssue> Issues => _issues.AsReadOnly();

    public int IssueCount => _issues.Count;

    public void AddIssue(string category, string message, long? absoluteOffset = null, long? relativeOffset = null)
    {
        _issues.Add(new IntegrityIssue(category, message, absoluteOffset, relativeOffset));
    }
}

public class RecoverySanitizationResult
{
    public string OutputPath { get; set; }
    public bool ParseSucceeded { get; set; }
    public bool VerifyPassed { get; set; }
    public int ParsedHbinCount { get; set; }
    public int IntegrityIssueCount { get; set; }
    public List<string> Notes { get; } = new();
}
