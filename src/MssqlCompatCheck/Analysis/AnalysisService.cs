namespace MssqlCompatCheck.Analysis;

public sealed class AnalysisService
{
    public Task<AnalysisRunResult> AnalyzeAsync(
        AnalysisOptions options,
        CancellationToken cancellationToken = default) =>
        AnalyzeAsync(options, progress: null, cancellationToken);

    public async Task<AnalysisRunResult> AnalyzeAsync(
        AnalysisOptions options,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        progress?.Report(new(
            AnalysisProgressStage.DiscoveringFiles,
            ProcessedFiles: 0,
            TotalFiles: 0,
            TotalLevels: 0));
        var discovery = SqlFileDiscovery.Discover(options.SqlDirectories, cancellationToken);
        var diagnostics = discovery.Diagnostics.ToList();
        var results = new List<AnalysisItemResult>(discovery.Files.Count);
        var manifestProvider = new ManifestMetadataProvider();
        var analyzedLevels = options.LevelScope switch
        {
            CompatibilityLevelScope.Range => SqlCompatibilityLevels.GetSupportedLevels()
                .Where(level => level > options.CurrentLevel && level <= options.TargetLevel)
                .ToArray(),
            CompatibilityLevelScope.TargetOnly => [options.TargetLevel],
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LevelScope,
                "サポートされていない解析スコープです。"),
        };
        progress?.Report(new(
            AnalysisProgressStage.AnalyzingFiles,
            ProcessedFiles: 0,
            TotalFiles: discovery.Files.Count,
            TotalLevels: analyzedLevels.Length));

        var processedFiles = 0;
        foreach (var file in discovery.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var source = await manifestProvider.GetMetadataAsync(file.FullPath, cancellationToken)
                    .ConfigureAwait(false);
                var sql = await SqlTextReader.ReadAsync(file.FullPath, options.EncodingName, cancellationToken)
                    .ConfigureAwait(false);
                var quotedIdentifier = source?.QuotedIdentifier ?? options.DefaultQuotedIdentifier;
                results.Add(SqlScriptAnalyzer.Analyze(
                    file,
                    sql,
                    options.CurrentLevel,
                    analyzedLevels,
                    quotedIdentifier,
                    options.IncludeFullSql,
                    options.IgnoreUnexpectedEof,
                    source));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.Text.DecoderFallbackException or ArgumentException)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "ANALYSIS_FILE_READ_FAILED",
                    exception.Message,
                    file.FullPath));
            }
            finally
            {
                processedFiles++;
                progress?.Report(new(
                    AnalysisProgressStage.AnalyzingFiles,
                    processedFiles,
                    discovery.Files.Count,
                    analyzedLevels.Length,
                    file.FullPath));
            }
        }

        diagnostics.AddRange(manifestProvider.Diagnostics);
        if (discovery.Files.Count == 0)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "ANALYSIS_NO_SQL_FILES",
                "指定されたディレクトリに解析対象の .sql ファイルがありません。"));
        }

        var orderedResults = results.OrderBy(result => result.FilePath, GetPathComparer()).ToArray();
        var levelSummaries = analyzedLevels
            .Select(level => CompatibilityLevelSummary.From(level, orderedResults))
            .ToArray();
        progress?.Report(new(
            AnalysisProgressStage.Completed,
            processedFiles,
            discovery.Files.Count,
            analyzedLevels.Length));
        return new(
            "1.0",
            SqlCompatibilityLevels.ScriptDomVersion,
            DateTimeOffset.UtcNow,
            options.CurrentLevel,
            options.TargetLevel,
            options.LevelScope,
            options.IgnoreUnexpectedEof,
            analyzedLevels,
            levelSummaries,
            options.SqlDirectories.Select(Path.GetFullPath).ToArray(),
            AnalysisSummary.From(orderedResults),
            orderedResults,
            diagnostics);
    }

    public static void ValidateOptions(AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SqlDirectories);

        if (options.SqlDirectories.Count == 0)
        {
            throw new ArgumentException("SQL ディレクトリを1つ以上指定してください。", nameof(options));
        }

        foreach (var sqlDirectory in options.SqlDirectories)
        {
            if (string.IsNullOrWhiteSpace(sqlDirectory))
            {
                throw new ArgumentException("SQL ディレクトリに空の値は指定できません。", nameof(options));
            }

            try
            {
                _ = Path.GetFullPath(sqlDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException(
                    $"SQL ディレクトリのパスが不正です: {sqlDirectory}",
                    nameof(options),
                    exception);
            }
        }

        if (!SqlCompatibilityLevels.IsSupported(options.CurrentLevel))
        {
            throw new ArgumentException(
                $"現在の互換性レベル {options.CurrentLevel} はサポートされていません。" +
                $" 指定可能な値: {string.Join(", ", SqlCompatibilityLevels.GetSupportedLevels())}",
                nameof(options));
        }

        if (!SqlCompatibilityLevels.IsSupported(options.TargetLevel))
        {
            throw new ArgumentException(
                $"変更先の互換性レベル {options.TargetLevel} はサポートされていません。" +
                $" 指定可能な値: {string.Join(", ", SqlCompatibilityLevels.GetSupportedLevels())}",
                nameof(options));
        }

        if (options.TargetLevel <= options.CurrentLevel)
        {
            throw new ArgumentException(
                "変更先の互換性レベルは現在の互換性レベルより大きい値を指定してください。",
                nameof(options));
        }

        if (!Enum.IsDefined(options.LevelScope))
        {
            throw new ArgumentException("解析スコープが不正です。", nameof(options));
        }

        if (!string.IsNullOrWhiteSpace(options.EncodingName))
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                _ = System.Text.Encoding.GetEncoding(options.EncodingName);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    $"指定されたエンコーディング '{options.EncodingName}' は利用できません。",
                    nameof(options),
                    exception);
            }
        }
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
