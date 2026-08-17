namespace STS2PGN.Validation;

public sealed class Sts2PgnValidationException : InvalidOperationException
{
    public Sts2PgnValidationResult ValidationResult { get; }

    public Sts2PgnValidationException(Sts2PgnValidationResult validationResult)
        : base(BuildMessage(validationResult))
    {
        ValidationResult = validationResult;
    }

    private static string BuildMessage(Sts2PgnValidationResult validationResult) =>
        "The STS2PGN record is invalid: "
        + string.Join("; ", validationResult.Errors.Select(issue => $"{issue.Code}: {issue.Message}"));
}
