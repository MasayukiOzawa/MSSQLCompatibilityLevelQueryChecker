using System.Text.Json.Serialization;

namespace MssqlCompatCheck.Analysis;

public sealed record AnalysisOptions(
    int CurrentLevel,
    int TargetLevel,
    IReadOnlyList<string> SqlDirectories,
    string? EncodingName = null,
    bool IncludeFullSql = false,
    bool DefaultQuotedIdentifier = true,
    bool IgnoreUnexpectedEof = false,
    CompatibilityLevelScope LevelScope = CompatibilityLevelScope.Range);

[JsonConverter(typeof(JsonStringEnumConverter<CompatibilityLevelScope>))]
public enum CompatibilityLevelScope
{
    Range,
    TargetOnly,
}

public enum AnalysisProgressStage
{
    DiscoveringFiles,
    AnalyzingFiles,
    Completed,
}

public sealed record AnalysisProgress(
    AnalysisProgressStage Stage,
    int ProcessedFiles,
    int TotalFiles,
    int TotalLevels,
    string? CurrentFile = null);

[JsonConverter(typeof(JsonStringEnumConverter<AnalysisStatus>))]
public enum AnalysisStatus
{
    Compatible,
    TargetIncompatible,
    CurrentInvalidTargetValid,
    Unparseable,
}

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticSeverity>))]
public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record AnalysisDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Path = null);

public sealed record ParseIssue(
    int Number,
    string Message,
    int Line,
    int Column,
    int Offset);

public sealed record CompatibilityLevelAnalysisResult(
    int Level,
    bool ParseSucceeded,
    IReadOnlyList<ParseIssue> Errors,
    string? ErrorContext);

public sealed record ParseErrorSummary(
    int Number,
    string Message,
    int OccurrenceCount,
    int AffectedFiles);

public sealed record CompatibilityLevelSummary(
    int Level,
    int Total,
    int Compatible,
    int ParseFailures,
    IReadOnlyList<ParseErrorSummary> ErrorSummaries)
{
    public static CompatibilityLevelSummary From(
        int level,
        IReadOnlyList<AnalysisItemResult> items)
    {
        var levelResults = items
            .Select(item => (
                item.FilePath,
                Result: item.LevelResults.Single(result => result.Level == level)))
            .ToArray();
        var compatible = levelResults.Count(item => item.Result.ParseSucceeded);
        var errorSummaries = levelResults
            .SelectMany(item => item.Result.Errors.Select(error => (item.FilePath, Error: error)))
            .GroupBy(item => (item.Error.Number, item.Error.Message))
            .Select(group => new ParseErrorSummary(
                group.Key.Number,
                group.Key.Message,
                group.Count(),
                group.Select(item => item.FilePath).Distinct(GetPathComparer()).Count()))
            .OrderByDescending(static summary => summary.OccurrenceCount)
            .ThenBy(static summary => summary.Number)
            .ThenBy(static summary => summary.Message, StringComparer.Ordinal)
            .ToArray();
        return new(level, items.Count, compatible, items.Count - compatible, errorSummaries);
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed record SqlSourceMetadata(
    string? SourceType = null,
    string? ObjectName = null,
    string? QueryHash = null,
    bool? QuotedIdentifier = null,
    long? OccurrenceCount = null,
    long? ExecutionCount = null,
    long? TotalWorkerTime = null,
    DateTimeOffset? LastExecutionTime = null,
    int? ObjectId = null,
    string? SchemaName = null,
    IReadOnlyList<string>? QueryHashes = null);

public sealed record AnalysisItemResult(
    string FilePath,
    string RelativePath,
    AnalysisStatus Status,
    bool QuotedIdentifier,
    string Sha256,
    IReadOnlyList<ParseIssue> CurrentErrors,
    IReadOnlyList<ParseIssue> TargetErrors,
    IReadOnlyList<CompatibilityLevelAnalysisResult> LevelResults,
    string? ErrorContext,
    string? Sql,
    SqlSourceMetadata? Source);

public sealed record AnalysisSummary(
    int Total,
    int Compatible,
    int TargetIncompatible,
    int CurrentInvalidTargetValid,
    int Unparseable)
{
    public static AnalysisSummary From(IReadOnlyList<AnalysisItemResult> items) => new(
        items.Count,
        items.Count(item => item.Status == AnalysisStatus.Compatible),
        items.Count(item => item.Status == AnalysisStatus.TargetIncompatible),
        items.Count(item => item.Status == AnalysisStatus.CurrentInvalidTargetValid),
        items.Count(item => item.Status == AnalysisStatus.Unparseable));
}

public sealed record AnalysisRunResult(
    string SchemaVersion,
    string ScriptDomVersion,
    DateTimeOffset GeneratedAtUtc,
    int CurrentLevel,
    int TargetLevel,
    CompatibilityLevelScope LevelScope,
    bool IgnoreUnexpectedEof,
    IReadOnlyList<int> AnalyzedLevels,
    IReadOnlyList<CompatibilityLevelSummary> LevelSummaries,
    IReadOnlyList<string> InputDirectories,
    AnalysisSummary Summary,
    IReadOnlyList<AnalysisItemResult> Items,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics)
{
    [JsonIgnore]
    public bool HasOperationalErrors => Diagnostics.Any(
        diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    [JsonIgnore]
    public bool HasParseFailures => Items.Any(
        item => item.LevelResults.Any(level => !level.ParseSucceeded));
}
