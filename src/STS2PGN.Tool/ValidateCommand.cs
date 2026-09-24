using STS2PGN.Model;
using STS2PGN.Parsing;
using STS2PGN.Scripting;
using STS2PGN.Validation;

namespace STS2PGN.Tool;

internal static class ValidateCommand
{
    public static int Run(string[] args)
    {
        ToolArguments arguments = TraceLoader.ParseArguments(args);
        bool anyError = false;
        foreach (string path in arguments.Paths)
        {
            anyError |= !ValidateFile(path, arguments.ParseOptions, arguments.Quiet, arguments.ValidationMode);
        }

        return anyError ? 1 : 0;
    }

    private static bool ValidateFile(string path, Sts2PgnParseOptions parseOptions, bool quiet, Sts2PgnValidationMode? requestedMode)
    {
        LoadedTrace trace;
        Sts2PgnValidationMode mode;
        try
        {
            trace = TraceLoader.Load(path, parseOptions);
            mode = requestedMode ?? (ScriptProfile.IsScript(trace.Record.Metadata)
                ? Sts2PgnValidationMode.Script : Sts2PgnValidationMode.Recording);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Sts2PgnParseException)
        {
            Console.Error.WriteLine($"{path}: {exception.Message}");
            return false;
        }

        Sts2PgnValidationResult result = Sts2PgnValidator.Validate(trace.Record, mode);
        int warningCount = result.Warnings.Count();
        bool clean = result.IsValid && warningCount == 0 && !trace.Record.RecoveredTruncatedFinalLine;
        if (clean && quiet)
        {
            return true;
        }

        if (result.IsValid)
        {
            string suffix = warningCount == 0 ? "" : $", {warningCount} warning(s)";
            if (mode == Sts2PgnValidationMode.Script)
            {
                int decisions = DecisionScript.FromRecord(trace.Record).Decisions.Count;
                Console.WriteLine($"{path}: valid script profile ({trace.Record.Entries.Count} records, {decisions} decisions{suffix})");
            }
            else
            {
                Console.WriteLine($"{path}: valid ({trace.Record.Entries.Count} records, result {trace.Record.Result.ToToken()}{suffix})");
            }
        }
        else
        {
            Console.WriteLine($"{path}: INVALID ({result.Errors.Count()} error(s), {warningCount} warning(s))");
        }

        if (trace.Record.RecoveredTruncatedFinalLine)
        {
            Console.WriteLine("  note: recovered a truncated unterminated final line (crash prefix)");
        }

        foreach (ValidationIssue issue in result.Issues)
        {
            string severity = issue.Severity == ValidationSeverity.Error ? "error" : "warning";
            string location = issue.Location is null ? "" : $" @ {issue.Location}";
            Console.WriteLine($"  {severity} {issue.Code}{location}: {issue.Message}");
        }

        return result.IsValid;
    }
}
