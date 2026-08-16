using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using MssqlCompatCheck.Cli;
using Xunit;

namespace MssqlCompatCheck.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task RunAsync_WithoutMode_ReturnsUsageError()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            [],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("--mode に analyze または export を指定してください。", error.ToString());
    }

    [Fact]
    public async Task RunAsync_WithUnknownMode_ReturnsUsageError()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            ["--mode", "unexpected"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("不明なモードです: unexpected", error.ToString());
    }

    [Theory]
    [InlineData("--database", "ApplicationDb")]
    [InlineData("--include-modules", null)]
    [InlineData("--include-query-cache", null)]
    [InlineData("--connection-string-env", "TEST_CONNECTION_STRING")]
    public async Task RunAsync_AnalyzeWithDatabaseOption_ReturnsUsageError(
        string option,
        string? value)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "--mode", "analyze",
            "--current-level", "120",
            "--target-level", "130",
            "--sql-directory", "unused-input",
            "--output", "unused-output",
            option,
        };
        if (value is not null)
        {
            arguments.Add(value);
        }

        var exitCode = await CliApplication.RunAsync(
            arguments.ToArray(),
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("analyze モードではデータベース、接続、モジュール、クエリキャッシュのオプションを使用できません。", error.ToString());
    }

    [Theory]
    [InlineData("--current-level", "120")]
    [InlineData("--target-level", "130")]
    [InlineData("--level-scope", "target")]
    [InlineData("--sql-directory", "unused-input")]
    [InlineData("--encoding", "utf-8")]
    [InlineData("--quoted-identifiers", "false")]
    [InlineData("--include-full-sql", null)]
    [InlineData("--ignore-unexpected-eof", null)]
    public async Task RunAsync_ExportWithAnalysisOption_ReturnsUsageError(
        string option,
        string? value)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "--mode", "export",
            "--database", "ApplicationDb",
            "--include-modules",
            "--output", "unused-output",
            option,
        };
        if (value is not null)
        {
            arguments.Add(value);
        }

        var exitCode = await CliApplication.RunAsync(
            arguments.ToArray(),
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("export モードでは解析レベル、SQL ディレクトリ、文字コード、解析レポートのオプションを使用できません。", error.ToString());
    }

    [Fact]
    public async Task RunAsync_ExportWithoutConnectionStringEnvironment_ReturnsUsageError()
    {
        var environmentName = $"MSSQL_COMPAT_TEST_MISSING_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(environmentName, null);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            [
                "--mode", "export",
                "--database", "ApplicationDb",
                "--include-modules",
                "--connection-string-env", environmentName,
                "--output", "unused-output",
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains(environmentName, error.ToString());
        Assert.Contains("設定されていません", error.ToString());
    }

    [Fact]
    public async Task RunAsync_AnalyzeNestedSql_WritesReportsAndReturnsSuccess()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputDirectory = Path.Combine(temporaryDirectory.Path, "input");
        var nestedDirectory = Path.Combine(inputDirectory, "level-one", "level-two");
        var outputDirectory = Path.Combine(temporaryDirectory.Path, "report");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(nestedDirectory, "nested.sql"),
            "SELECT 1;",
            TestContext.Current.CancellationToken);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            [
                "--mode", "analyze",
                "--current-level", "120",
                "--target-level", "130",
                "--sql-directory", inputDirectory,
                "--output", outputDirectory,
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        var consoleOutput = output.ToString();
        Assert.Contains("SQLファイルを探索しています...", consoleOutput);
        Assert.Contains("解析対象: 1 ファイル、1 互換性レベル", consoleOutput);
        Assert.Contains("解析中: 1/1 ファイル (100.0%)", consoleOutput);
        Assert.Contains("SQL解析が完了しました。", consoleOutput);
        Assert.Contains("レポートを生成しています...", consoleOutput);
        Assert.Contains("解析完了: 合計 1, 変更先範囲成功 1, 要確認 0", consoleOutput);
        Assert.True(
            consoleOutput.IndexOf("SQLファイルを探索しています...", StringComparison.Ordinal) <
            consoleOutput.IndexOf("レポートを生成しています...", StringComparison.Ordinal));

        var jsonPath = Path.Combine(outputDirectory, "analysis-report.json");
        var htmlPath = Path.Combine(outputDirectory, "analysis-report.html");
        Assert.True(File.Exists(jsonPath));
        Assert.True(File.Exists(htmlPath));

        await using var jsonStream = File.OpenRead(jsonPath);
        using var report = await JsonDocument.ParseAsync(
            jsonStream,
            cancellationToken: TestContext.Current.CancellationToken);
        var root = report.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("Range", root.GetProperty("levelScope").GetString());
        Assert.Equal(
            [130],
            root.GetProperty("analyzedLevels").EnumerateArray().Select(level => level.GetInt32()));
        Assert.DoesNotContain(
            root.GetProperty("items")[0].GetProperty("levelResults").EnumerateArray(),
            level => level.GetProperty("level").GetInt32() == 120);
        Assert.Equal(1, root.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("compatible").GetInt32());
        Assert.EndsWith(
            "nested.sql",
            root.GetProperty("items")[0].GetProperty("filePath").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ShortAnalyzeOptions_WriteReportsAndReturnSuccess()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputDirectory = Path.Combine(temporaryDirectory.Path, "input");
        var outputDirectory = Path.Combine(temporaryDirectory.Path, "report");
        Directory.CreateDirectory(inputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(inputDirectory, "short-options.sql"),
            "SELECT 1;",
            TestContext.Current.CancellationToken);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            [
                "-m", "analyze",
                "-c", "120",
                "-t", "130",
                "-s", inputDirectory,
                "-o", outputDirectory,
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.True(File.Exists(Path.Combine(outputDirectory, "analysis-report.json")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "analysis-report.html")));
    }

    [Fact]
    public async Task RunAsync_ShortExportOptions_AreAccepted()
    {
        var environmentName = $"MSSQL_COMPAT_TEST_MISSING_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(environmentName, null);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            [
                "-m", "export",
                "-d", "ApplicationDb",
                "-M",
                "-e", environmentName,
                "-o", "unused-output",
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains(environmentName, error.ToString());
        Assert.Contains("設定されていません", error.ToString());
    }

    [Theory]
    [InlineData("--query-cache-limit")]
    [InlineData("-l")]
    public async Task RunAsync_RemovedQueryCacheLimitOption_ReturnsUsageError(string option)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            [
                "-m", "export",
                "-d", "ApplicationDb",
                "-Q",
                option, "10",
                "-o", "unused-output",
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains(option, error.ToString());
    }

    [Fact]
    public async Task RunAsync_IgnoreUnexpectedEofShortOption_ReturnsSuccessForEofOnlyFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputDirectory = Path.Combine(temporaryDirectory.Path, "input");
        var outputDirectory = Path.Combine(temporaryDirectory.Path, "report");
        Directory.CreateDirectory(inputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(inputDirectory, "unexpected-eof.sql"),
            "IF NOT EXISTS(SELECT 1)",
            TestContext.Current.CancellationToken);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            [
                "-m", "analyze",
                "-c", "110",
                "-t", "130",
                "-s", inputDirectory,
                "-o", outputDirectory,
                "-u",
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Contains("Unexpected EOF (46029) を解析結果から除外します。", output.ToString());
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(outputDirectory, "analysis-report.json"),
            TestContext.Current.CancellationToken));
        Assert.True(report.RootElement.GetProperty("ignoreUnexpectedEof").GetBoolean());
        Assert.Equal(1, report.RootElement.GetProperty("summary").GetProperty("compatible").GetInt32());
        var html = await File.ReadAllTextAsync(
            Path.Combine(outputDirectory, "analysis-report.html"),
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "<span class=\"meta-label\">Unexpected EOF (46029)</span><strong>除外する</strong>",
            html);
    }

    [Fact]
    public async Task RunAsync_TargetLevelScopeShortOption_AnalyzesOnlyTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var inputDirectory = Path.Combine(temporaryDirectory.Path, "input");
        var outputDirectory = Path.Combine(temporaryDirectory.Path, "report");
        Directory.CreateDirectory(inputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(inputDirectory, "target-valid.sql"),
            "DROP TABLE IF EXISTS dbo.t;",
            TestContext.Current.CancellationToken);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            [
                "-m", "analyze",
                "-c", "110",
                "-t", "130",
                "-L", "target",
                "-s", inputDirectory,
                "-o", outputDirectory,
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Contains("解析対象: 1 ファイル、1 互換性レベル", output.ToString());
        Assert.Contains("解析完了: 合計 1, 指定レベル成功 1, 要確認 0", output.ToString());
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(outputDirectory, "analysis-report.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal("TargetOnly", report.RootElement.GetProperty("levelScope").GetString());
        Assert.Equal(
            [130],
            report.RootElement.GetProperty("analyzedLevels")
                .EnumerateArray()
                .Select(level => level.GetInt32()));
        var html = await File.ReadAllTextAsync(
            Path.Combine(outputDirectory, "analysis-report.html"),
            TestContext.Current.CancellationToken);
        Assert.Contains("<span class=\"meta-label\">解析方式</span><strong>変更先レベルのみ</strong>", html);
        Assert.DoesNotContain("class=\"summary\"", html);
        Assert.DoesNotContain($"<span>{HtmlEncoder.Default.Encode("指定レベルで成功")}</span>", html);
    }

    [Fact]
    public async Task RunAsync_InvalidLevelScope_ReturnsUsageError()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            ["-m", "analyze", "-L", "unknown"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("--level-scope は range または target", error.ToString());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MssqlCompatCheck.Tests",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            GC.SuppressFinalize(this);
        }
    }
}
