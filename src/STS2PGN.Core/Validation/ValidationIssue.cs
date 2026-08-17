namespace STS2PGN.Validation;

public sealed record ValidationIssue(
    string Code,
    string Message,
    ValidationSeverity Severity,
    string? Location = null);
