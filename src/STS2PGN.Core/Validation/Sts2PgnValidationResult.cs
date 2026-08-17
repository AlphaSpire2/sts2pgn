using System.Collections.ObjectModel;

namespace STS2PGN.Validation;

public sealed class Sts2PgnValidationResult
{
    private readonly ReadOnlyCollection<ValidationIssue> _issues;

    public IReadOnlyList<ValidationIssue> Issues => _issues;
    public bool IsValid => _issues.All(issue => issue.Severity != ValidationSeverity.Error);
    public IEnumerable<ValidationIssue> Errors => _issues.Where(issue => issue.Severity == ValidationSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => _issues.Where(issue => issue.Severity == ValidationSeverity.Warning);

    internal Sts2PgnValidationResult(IEnumerable<ValidationIssue> issues)
    {
        _issues = Array.AsReadOnly(issues.ToArray());
    }
}
