using System.CommandLine;
using MssqlCompatCheck.Analysis;
using MssqlCompatCheck.Export;
using MssqlCompatCheck.Reporting;

namespace MssqlCompatCheck.Cli;

public static class CliApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var modeOption = new Option<string?>("--mode", "-m")
        {
            Description = "実行モード: analyze または export",
        };
        var currentLevelOption = new Option<int?>("--current-level", "-c")
        {
            Description = "現在の互換性レベル",
        };
        var targetLevelOption = new Option<int?>("--target-level", "-t")
        {
            Description = "変更先の互換性レベル",
        };
        var levelScopeOption = new Option<string?>("--level-scope", "-L")
        {
            Description = "解析する互換性レベル: range（範囲内すべて、既定）または target（変更先のみ）",
        };
        var sqlDirectoryOption = new Option<string[]>("--sql-directory", "-s")
        {
            Description = "再帰的に解析する SQL ディレクトリ。複数指定できます。",
            AllowMultipleArgumentsPerToken = true,
        };
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "レポートまたはエクスポートの出力ディレクトリ",
        };
        var databaseOption = new Option<string?>("--database", "-d")
        {
            Description = "エクスポート元データベース名",
        };
        var includeModulesOption = new Option<bool>("--include-modules", "-M")
        {
            Description = "P / FN / IF / TR の SQL モジュールをエクスポートします。",
        };
        var includeQueryCacheOption = new Option<bool>("--include-query-cache", "-Q")
        {
            Description = "クエリキャッシュをエクスポートします。",
        };
        var connectionStringEnvOption = new Option<string?>("--connection-string-env", "-e")
        {
            Description = "接続文字列を格納した環境変数名。既定: MSSQL_COMPAT_CONNECTION_STRING",
        };
        var encodingOption = new Option<string?>("--encoding", "-E")
        {
            Description = "BOM なし SQL ファイルの文字エンコーディング。既定: UTF-8",
        };
        var quotedIdentifiersOption = new Option<string?>("--quoted-identifiers", "-i")
        {
            Description = "マニフェストがない SQL ファイルの QUOTED_IDENTIFIER: true または false。既定: true",
        };
        var includeFullSqlOption = new Option<bool>("--include-full-sql", "-f")
        {
            Description = "解析レポートへ SQL 全文を含めます。",
        };
        var ignoreUnexpectedEofOption = new Option<bool>("--ignore-unexpected-eof", "-u")
        {
            Description = "ScriptDOM の Unexpected EOF エラー (46029) を解析結果から除外します。",
        };
        var overwriteOption = new Option<bool>("--overwrite", "-w")
        {
            Description = "本ツールが生成した既存出力の上書きを許可します。",
        };

        var root = new RootCommand("SQL Server 互換性レベルクエリチェッカー");
        var versionOption = root.Options.OfType<VersionOption>().Single();
        versionOption.Aliases.Add("-v");
        root.Options.Add(modeOption);
        root.Options.Add(currentLevelOption);
        root.Options.Add(targetLevelOption);
        root.Options.Add(levelScopeOption);
        root.Options.Add(sqlDirectoryOption);
        root.Options.Add(outputOption);
        root.Options.Add(databaseOption);
        root.Options.Add(includeModulesOption);
        root.Options.Add(includeQueryCacheOption);
        root.Options.Add(connectionStringEnvOption);
        root.Options.Add(encodingOption);
        root.Options.Add(quotedIdentifiersOption);
        root.Options.Add(includeFullSqlOption);
        root.Options.Add(ignoreUnexpectedEofOption);
        root.Options.Add(overwriteOption);

        root.SetAction(async (parseResult, token) =>
        {
            var mode = parseResult.GetValue(modeOption)?.Trim().ToLowerInvariant();
            var currentLevel = parseResult.GetValue(currentLevelOption);
            var targetLevel = parseResult.GetValue(targetLevelOption);
            var levelScopeText = parseResult.GetValue(levelScopeOption);
            CompatibilityLevelScope? levelScope = levelScopeText?.Trim().ToLowerInvariant() switch
            {
                null or "" => null,
                "range" => CompatibilityLevelScope.Range,
                "target" => CompatibilityLevelScope.TargetOnly,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(levelScopeText) && levelScope is null)
            {
                return WriteUsageError(error, "--level-scope は range または target で指定してください。");
            }

            var sqlDirectories = parseResult.GetValue(sqlDirectoryOption) ?? [];
            var outputDirectory = parseResult.GetValue(outputOption);
            var database = parseResult.GetValue(databaseOption);
            var includeModules = parseResult.GetValue(includeModulesOption);
            var includeQueryCache = parseResult.GetValue(includeQueryCacheOption);
            var connectionStringEnvironment = parseResult.GetValue(connectionStringEnvOption);
            var encoding = parseResult.GetValue(encodingOption);
            var quotedIdentifiersText = parseResult.GetValue(quotedIdentifiersOption);
            bool? quotedIdentifiers = null;
            if (quotedIdentifiersText is not null)
            {
                if (!bool.TryParse(quotedIdentifiersText, out var parsedQuotedIdentifiers))
                {
                    return WriteUsageError(error, "--quoted-identifiers は true または false で指定してください。");
                }

                quotedIdentifiers = parsedQuotedIdentifiers;
            }
            var includeFullSql = parseResult.GetValue(includeFullSqlOption);
            var ignoreUnexpectedEof = parseResult.GetValue(ignoreUnexpectedEofOption);
            var overwrite = parseResult.GetValue(overwriteOption);

            try
            {
                return mode switch
                {
                    "analyze" => await RunAnalyzeAsync(
                        currentLevel,
                        targetLevel,
                        levelScope,
                        sqlDirectories,
                        outputDirectory,
                        database,
                        includeModules,
                        includeQueryCache,
                        connectionStringEnvironment,
                        encoding,
                        quotedIdentifiers,
                        includeFullSql,
                        ignoreUnexpectedEof,
                        overwrite,
                        output,
                        error,
                        token).ConfigureAwait(false),
                    "export" => await RunExportAsync(
                        currentLevel,
                        targetLevel,
                        levelScope,
                        sqlDirectories,
                        outputDirectory,
                        database,
                        includeModules,
                        includeQueryCache,
                        connectionStringEnvironment,
                        encoding,
                        quotedIdentifiers,
                        includeFullSql,
                        ignoreUnexpectedEof,
                        overwrite,
                        output,
                        error,
                        token).ConfigureAwait(false),
                    null or "" => WriteUsageError(error, "--mode に analyze または export を指定してください。"),
                    _ => WriteUsageError(error, $"不明なモードです: {mode}"),
                };
            }
            catch (OperationCanceledException)
            {
                await error.WriteLineAsync("処理をキャンセルしました。").ConfigureAwait(false);
                return 130;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"エラー: {exception.Message}").ConfigureAwait(false);
                return 2;
            }
            catch (Exception exception)
            {
                await error.WriteLineAsync($"予期しないエラー: {exception.Message}").ConfigureAwait(false);
                return 2;
            }
        });

        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var parseError in parsed.Errors)
            {
                await error.WriteLineAsync($"引数エラー: {parseError.Message}").ConfigureAwait(false);
            }

            return 2;
        }

        return await parsed.InvokeAsync(new InvocationConfiguration(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunAnalyzeAsync(
        int? currentLevel,
        int? targetLevel,
        CompatibilityLevelScope? levelScope,
        IReadOnlyList<string> sqlDirectories,
        string? outputDirectory,
        string? database,
        bool includeModules,
        bool includeQueryCache,
        string? connectionStringEnvironment,
        string? encoding,
        bool? quotedIdentifiers,
        bool includeFullSql,
        bool ignoreUnexpectedEof,
        bool overwrite,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateAnalyzeOptions(
            currentLevel,
            targetLevel,
            sqlDirectories,
            outputDirectory,
            database,
            includeModules,
            includeQueryCache,
            connectionStringEnvironment);
        if (validationError is not null)
        {
            return WriteUsageError(error, validationError);
        }

        var options = new AnalysisOptions(
            currentLevel!.Value,
            targetLevel!.Value,
            sqlDirectories,
            encoding,
            includeFullSql,
            quotedIdentifiers ?? true,
            ignoreUnexpectedEof,
            levelScope ?? CompatibilityLevelScope.Range);
        if (ignoreUnexpectedEof)
        {
            await output.WriteLineAsync("Unexpected EOF (46029) を解析結果から除外します。")
                .ConfigureAwait(false);
        }

        var progress = new ConsoleAnalysisProgress(output);
        var result = await new AnalysisService()
            .AnalyzeAsync(options, progress, cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync("レポートを生成しています...").ConfigureAwait(false);
        var report = await new ReportWriter()
            .WriteAsync(result, outputDirectory!, overwrite, cancellationToken)
            .ConfigureAwait(false);

        var successLabel = result.LevelScope == CompatibilityLevelScope.TargetOnly
            ? "指定レベル成功"
            : "変更先範囲成功";
        var selectedLevelCompatible = result.Items.Count(
            item => item.LevelResults.All(level => level.ParseSucceeded));
        await output.WriteLineAsync(
            $"解析完了: 合計 {result.Summary.Total}, {successLabel} {selectedLevelCompatible}, 要確認 {result.Summary.Total - selectedLevelCompatible}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"JSON: {report.JsonPath}").ConfigureAwait(false);
        await output.WriteLineAsync($"HTML: {report.HtmlPath}").ConfigureAwait(false);

        foreach (var diagnostic in result.Diagnostics)
        {
            await error.WriteLineAsync($"{diagnostic.Severity}: {diagnostic.Message}").ConfigureAwait(false);
        }

        return result.HasOperationalErrors ? 2 : result.HasParseFailures ? 1 : 0;
    }

    private static async Task<int> RunExportAsync(
        int? currentLevel,
        int? targetLevel,
        CompatibilityLevelScope? levelScope,
        IReadOnlyList<string> sqlDirectories,
        string? outputDirectory,
        string? database,
        bool includeModules,
        bool includeQueryCache,
        string? connectionStringEnvironment,
        string? encoding,
        bool? quotedIdentifiers,
        bool includeFullSql,
        bool ignoreUnexpectedEof,
        bool overwrite,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateExportOptions(
            currentLevel,
            targetLevel,
            levelScope,
            sqlDirectories,
            outputDirectory,
            database,
            includeModules,
            includeQueryCache,
            encoding,
            quotedIdentifiers,
            includeFullSql,
            ignoreUnexpectedEof);
        if (validationError is not null)
        {
            return WriteUsageError(error, validationError);
        }

        var environmentName = string.IsNullOrWhiteSpace(connectionStringEnvironment)
            ? "MSSQL_COMPAT_CONNECTION_STRING"
            : connectionStringEnvironment;
        var connectionString = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return WriteUsageError(error, $"接続文字列の環境変数 '{environmentName}' が設定されていません。");
        }

        var result = await new ExportService().ExportAsync(
                new ExportOptions(
                    connectionString,
                    database!,
                    outputDirectory!,
                    includeModules,
                    includeQueryCache,
                    overwrite),
                cancellationToken)
            .ConfigureAwait(false);

        await output.WriteLineAsync(
            $"エクスポート完了: modules {result.ModulesExported}, cache {result.CacheQueriesExported}, skipped {result.SkippedCount}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"出力: {result.OutputDirectory}").ConfigureAwait(false);

        foreach (var diagnostic in result.Diagnostics)
        {
            await error.WriteLineAsync($"{diagnostic.Severity}: {diagnostic.Message}").ConfigureAwait(false);
        }

        return result.ExitCode;
    }

    private static string? ValidateAnalyzeOptions(
        int? currentLevel,
        int? targetLevel,
        IReadOnlyList<string> sqlDirectories,
        string? outputDirectory,
        string? database,
        bool includeModules,
        bool includeQueryCache,
        string? connectionStringEnvironment)
    {
        if (currentLevel is null || targetLevel is null)
        {
            return "analyze モードでは --current-level と --target-level が必須です。";
        }

        if (sqlDirectories.Count == 0 || sqlDirectories.Any(string.IsNullOrWhiteSpace))
        {
            return "analyze モードでは --sql-directory を1つ以上指定してください。";
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return "--output を指定してください。";
        }

        if (database is not null || includeModules || includeQueryCache ||
            connectionStringEnvironment is not null)
        {
            return "analyze モードではデータベース、接続、モジュール、クエリキャッシュのオプションを使用できません。";
        }

        return null;
    }

    private static string? ValidateExportOptions(
        int? currentLevel,
        int? targetLevel,
        CompatibilityLevelScope? levelScope,
        IReadOnlyList<string> sqlDirectories,
        string? outputDirectory,
        string? database,
        bool includeModules,
        bool includeQueryCache,
        string? encoding,
        bool? quotedIdentifiers,
        bool includeFullSql,
        bool ignoreUnexpectedEof)
    {
        if (currentLevel is not null || targetLevel is not null || levelScope is not null || sqlDirectories.Count > 0 ||
            encoding is not null || quotedIdentifiers is not null || includeFullSql || ignoreUnexpectedEof)
        {
            return "export モードでは解析レベル、SQL ディレクトリ、文字コード、解析レポートのオプションを使用できません。";
        }

        if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(outputDirectory))
        {
            return "export モードでは --database と --output が必須です。";
        }

        if (!includeModules && !includeQueryCache)
        {
            return "--include-modules または --include-query-cache の少なくとも一方を指定してください。";
        }

        return null;
    }

    private static int WriteUsageError(TextWriter error, string message)
    {
        error.WriteLine($"引数エラー: {message}");
        return 2;
    }

    private sealed class ConsoleAnalysisProgress(TextWriter output) : IProgress<AnalysisProgress>
    {
        private int _nextPercentage = 5;

        public void Report(AnalysisProgress value)
        {
            switch (value.Stage)
            {
                case AnalysisProgressStage.DiscoveringFiles:
                    output.WriteLine("SQLファイルを探索しています...");
                    break;
                case AnalysisProgressStage.AnalyzingFiles when value.ProcessedFiles == 0:
                    output.WriteLine(
                        $"解析対象: {value.TotalFiles} ファイル、{value.TotalLevels} 互換性レベル");
                    break;
                case AnalysisProgressStage.AnalyzingFiles:
                    WriteFileProgress(value);
                    break;
                case AnalysisProgressStage.Completed:
                    output.WriteLine("SQL解析が完了しました。");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(value), value.Stage, "不明な進捗ステージです。");
            }
        }

        private void WriteFileProgress(AnalysisProgress value)
        {
            if (value.TotalFiles <= 0)
            {
                return;
            }

            var percentage = value.ProcessedFiles * 100d / value.TotalFiles;
            var shouldWrite = value.ProcessedFiles == 1 ||
                              value.ProcessedFiles == value.TotalFiles ||
                              percentage >= _nextPercentage;
            if (!shouldWrite)
            {
                return;
            }

            output.WriteLine(FormattableString.Invariant(
                $"解析中: {value.ProcessedFiles}/{value.TotalFiles} ファイル ({percentage:0.0}%)"));
            while (_nextPercentage <= percentage)
            {
                _nextPercentage += 5;
            }
        }
    }
}
