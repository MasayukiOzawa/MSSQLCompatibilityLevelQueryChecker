using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlCompatCheck.Analysis;

internal static class SqlScriptAnalyzer
{
    private const int UnexpectedEofErrorNumber = 46029;

    public static AnalysisItemResult Analyze(
        DiscoveredSqlFile file,
        string sql,
        int currentLevel,
        IReadOnlyList<int> levels,
        bool quotedIdentifier,
        bool includeFullSql,
        bool ignoreUnexpectedEof,
        SqlSourceMetadata? source)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0)
        {
            throw new ArgumentException("解析レベルを1つ以上指定してください。", nameof(levels));
        }

        var currentResult = Parse(sql, currentLevel, quotedIdentifier, ignoreUnexpectedEof);
        var levelResults = levels
            .Select(level => Parse(sql, level, quotedIdentifier, ignoreUnexpectedEof))
            .Select(result => new CompatibilityLevelAnalysisResult(
                result.Level,
                result.Errors.Count == 0,
                result.Errors,
                result.Errors
                    .Where(error => error.Line > 0)
                    .OrderBy(error => error.Line)
                    .ThenBy(error => error.Column)
                    .Select(error => CreateErrorContext(sql, error.Line))
                    .FirstOrDefault()))
            .ToArray();
        IReadOnlyList<ParseIssue> currentErrors = currentResult.Errors;
        var targetErrors = levelResults[^1].Errors;
        var status = Classify(currentErrors, levelResults);
        var firstError = currentErrors.Concat(levelResults.SelectMany(result => result.Errors))
            .Where(error => error.Line > 0)
            .OrderBy(error => error.Line)
            .ThenBy(error => error.Column)
            .FirstOrDefault();

        return new(
            file.FullPath,
            file.RelativePath,
            status,
            quotedIdentifier,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant(),
            currentErrors,
            targetErrors,
            levelResults,
            firstError is null ? null : CreateErrorContext(sql, firstError.Line),
            includeFullSql ? sql : null,
            source);
    }

    private static (int Level, IReadOnlyList<ParseIssue> Errors) Parse(
        string sql,
        int level,
        bool quotedIdentifier,
        bool ignoreUnexpectedEof)
    {
        var parser = SqlCompatibilityLevels.CreateParser(level, quotedIdentifier);
        using var reader = new StringReader(sql);
        _ = parser.Parse(reader, out IList<ParseError> errors);
        return (level, errors
            .Where(error => !ignoreUnexpectedEof || error.Number != UnexpectedEofErrorNumber)
            .Select(error => new ParseIssue(
                error.Number,
                error.Message,
                error.Line,
                error.Column,
                error.Offset))
            .ToArray());
    }

    private static AnalysisStatus Classify(
        IReadOnlyList<ParseIssue> currentErrors,
        IReadOnlyList<CompatibilityLevelAnalysisResult> levelResults)
    {
        if (currentErrors.Count == 0 && levelResults.All(static result => result.ParseSucceeded))
        {
            return AnalysisStatus.Compatible;
        }

        if (currentErrors.Count == 0)
        {
            return AnalysisStatus.TargetIncompatible;
        }

        return levelResults.Any(static result => result.ParseSucceeded)
            ? AnalysisStatus.CurrentInvalidTargetValid
            : AnalysisStatus.Unparseable;
    }

    private static string CreateErrorContext(string sql, int errorLine)
    {
        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var index = Math.Clamp(errorLine - 1, 0, lines.Length - 1);
        var start = Math.Max(0, index - 1);
        var end = Math.Min(lines.Length - 1, index + 1);
        var context = string.Join("\n", lines[start..(end + 1)]);
        return context.Length <= 500 ? context : context[..500];
    }
}
