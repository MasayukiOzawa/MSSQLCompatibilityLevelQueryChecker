using System.Text.Json;
using MssqlCompatCheck.Analysis;
using MssqlCompatCheck.Reporting;
using Xunit;

namespace MssqlCompatCheck.Tests.Reporting;

public sealed class ReportWriterTests
{
    [Fact]
    public async Task WriteAsync_WritesSchemaAndOmitsFullSqlByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var sqlDirectory = temp.CreateDirectory("sql");
        await File.WriteAllTextAsync(
            Path.Combine(sqlDirectory, "secret.sql"),
            "SELECT 'do-not-leak-secret';",
            cancellationToken);
        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 170, [sqlDirectory]),
            cancellationToken);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var jsonText = await File.ReadAllTextAsync(written.JsonPath, cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);
        using var document = JsonDocument.Parse(jsonText);
        var firstItem = document.RootElement.GetProperty("items")[0];

        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("180.59.2", document.RootElement.GetProperty("scriptDomVersion").GetString());
        Assert.False(document.RootElement.GetProperty("ignoreUnexpectedEof").GetBoolean());
        Assert.Contains("<span class=\"meta-label\">ScriptDOM</span><strong>180.59.2</strong>", html);
        Assert.Contains(
            "<span class=\"meta-label\">現在の互換性レベル → 変更先の互換性レベル</span>",
            html);
        Assert.DoesNotContain("比較基準", html);
        Assert.Contains("<main class=\"report-shell\">", html);
        Assert.Contains("<header class=\"hero\">", html);
        Assert.Contains("<div class=\"meta-grid\">", html);
        Assert.Contains("<section class=\"panel summary-panel\">", html);
        Assert.Contains("@media(max-width:760px)", html);
        Assert.Contains("@media print", html);
        Assert.False(firstItem.TryGetProperty("sql", out _));
        Assert.DoesNotContain("do-not-leak-secret", jsonText);
        Assert.DoesNotContain("do-not-leak-secret", html);
        Assert.DoesNotContain("<h2>処理上の問題</h2>", html);
    }

    [Fact]
    public async Task WriteAsync_EscapesSqlInJsonAndStandaloneHtml()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var sqlDirectory = temp.CreateDirectory("sql");
        const string sql = "SELECT '<script>alert(1)</script>' AS value FROM;";
        await File.WriteAllTextAsync(Path.Combine(sqlDirectory, "escape.sql"), sql, cancellationToken);
        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 170, [sqlDirectory], IncludeFullSql: true),
            cancellationToken);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var jsonText = await File.ReadAllTextAsync(written.JsonPath, cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);
        using var document = JsonDocument.Parse(jsonText);
        var errorSummary = document.RootElement
            .GetProperty("levelSummaries")[0]
            .GetProperty("errorSummaries")[0];

        Assert.Equal(sql, document.RootElement.GetProperty("items")[0].GetProperty("sql").GetString());
        Assert.Equal(1, errorSummary.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal(1, errorSummary.GetProperty("affectedFiles").GetInt32());
        Assert.DoesNotContain("<script>", jsonText);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<!doctype html>", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("<script src=", html);
    }

    [Fact]
    public async Task WriteAsync_OmitsCompatibleFilesFromLevelTabsButKeepsThemInJson()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var sqlDirectory = temp.CreateDirectory("sql");
        const string fileName = "compatible-only.sql";
        await File.WriteAllTextAsync(
            Path.Combine(sqlDirectory, fileName),
            "SELECT 1;",
            cancellationToken);
        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 170, [sqlDirectory]),
            cancellationToken);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var jsonText = await File.ReadAllTextAsync(written.JsonPath, cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);
        using var document = JsonDocument.Parse(jsonText);

        Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("compatible").GetInt32());
        Assert.Equal(fileName, Path.GetFileName(
            document.RootElement.GetProperty("items")[0].GetProperty("filePath").GetString()));
        Assert.Equal(
            [120, 130, 140, 150, 160, 170],
            document.RootElement.GetProperty("analyzedLevels")
                .EnumerateArray()
                .Select(level => level.GetInt32()));
        Assert.DoesNotContain(fileName, html);
        foreach (var level in new[] { 120, 130, 140, 150, 160, 170 })
        {
            Assert.Contains($"id=\"compatibility-level-{level}\"", html);
            Assert.Contains($"id=\"panel-level-{level}\"", html);
        }

        Assert.Contains("このレベルでParseに成功した 1 件は詳細表示を省略しています。", html);
        Assert.Contains("このレベルで要確認のファイルはありません。", html);
    }

    [Fact]
    public async Task WriteAsync_LevelTabsShowFileOnlyAtLevelsWhereParseFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var sqlDirectory = temp.CreateDirectory("sql");
        const string fileName = "level-sensitive.sql";
        await File.WriteAllTextAsync(
            Path.Combine(sqlDirectory, fileName),
            "DROP TABLE IF EXISTS dbo.t;",
            cancellationToken);
        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 140, [sqlDirectory]),
            cancellationToken);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);

        Assert.Equal(3, result.LevelSummaries.Count);
        Assert.Equal(1, result.LevelSummaries.Single(level => level.Level == 120).ParseFailures);
        Assert.Equal(0, result.LevelSummaries.Single(level => level.Level == 130).ParseFailures);
        Assert.Equal(0, result.LevelSummaries.Single(level => level.Level == 140).ParseFailures);
        var expectedHref = $"href=\"{new Uri(Path.Combine(sqlDirectory, fileName)).AbsoluteUri}\"";
        Assert.Equal(1, html.Split(expectedHref, StringSplitOptions.None).Length - 1);
        Assert.Equal(3, html.Split("class=\"tab-input\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, html.Split("class=\"tab-panel\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("id=\"compatibility-level-110\"", html);
        Assert.Contains("aria-controls=\"panel-level-120\">120 (1)</label>", html);
        Assert.Contains("aria-controls=\"panel-level-130\">130 (0)</label>", html);
        Assert.Contains("aria-controls=\"panel-level-140\">140 (0)</label>", html);
        Assert.Contains("<h2>互換性レベル別サマリー</h2>", html);
        Assert.Contains("<table class=\"summary-table\">", html);
        Assert.Contains(".summary-table{width:auto;min-width:34rem}", html);
        Assert.Contains("<th>互換性レベル</th><th>対象件数</th><th>Parse 成功</th><th>Parse 失敗</th>", html);
        Assert.Contains("<h3>Parseエラー内容別集計</h3>", html);
        Assert.Contains("<table class=\"error-summary-table\">", html);
        Assert.Contains(".error-summary-table{width:auto;max-width:100%;min-width:54rem}", html);
        Assert.Contains("<div class=\"error-groups\">", html);
        Assert.Contains("<article class=\"error-group\"", html);
        Assert.Contains("<span class=\"finding-label\">該当ファイル</span>", html);
        Assert.DoesNotContain("SHA-256", html);
        Assert.DoesNotContain(
            result.Items.Single(item => !item.LevelResults.Single(level => level.Level == 120).ParseSucceeded).Sha256,
            html);
        var decodedHtml = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("class=\"position-pill\">行 ", decodedHtml);
        Assert.DoesNotContain("<summary>互換性レベル 120", decodedHtml);
        Assert.Contains("<th>エラー番号</th><th>エラー内容</th><th>発生件数</th><th>該当ファイル数</th>", html);
        Assert.Contains("<tr><th scope=\"row\">120</th><td>1</td><td class=\"ok\"><strong>0</strong></td><td class=\"fail\"><strong>1</strong></td></tr>", html);
        Assert.Contains("<tr><th scope=\"row\">130</th><td>1</td><td class=\"ok\"><strong>1</strong></td><td class=\"ok\"><strong>0</strong></td></tr>", html);
        Assert.True(
            html.IndexOf("互換性レベル別サマリー", StringComparison.Ordinal) <
            html.IndexOf("互換性レベル別の解析結果", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteAsync_ErrorSummaryMergesCompatibilityLevelCells()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var sqlDirectory = temp.CreateDirectory("sql");
        await File.WriteAllTextAsync(
            Path.Combine(sqlDirectory, "syntax-error.sql"),
            "SELECT FROM;",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(sqlDirectory, "unexpected-eof.sql"),
            "IF NOT EXISTS(SELECT 1)",
            cancellationToken);
        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 120, [sqlDirectory]),
            cancellationToken);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);
        var level120 = result.LevelSummaries.Single(summary => summary.Level == 120);

        Assert.True(level120.ErrorSummaries.Count >= 2);
        Assert.Contains(
            $"<th scope=\"rowgroup\" rowspan=\"{level120.ErrorSummaries.Count}\" class=\"level-cell\">120</th>",
            html);
        Assert.Equal(
            1,
            html.Split("class=\"level-cell\">120</th>", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task WriteAsync_GroupsErrorsAndListsAllAffectedFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var sqlDirectory = temp.CreateDirectory("sql");
        await File.WriteAllTextAsync(Path.Combine(sqlDirectory, "first.sql"), "SELECT FROM;", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sqlDirectory, "second.sql"), "SELECT FROM;", cancellationToken);
        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 120, [sqlDirectory]),
            cancellationToken);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);
        var decodedHtml = System.Net.WebUtility.HtmlDecode(html);

        Assert.Equal(1, html.Split("<article class=\"error-group\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("id=\"error-group-120-1\"", html);
        Assert.Contains("発生件数<strong>2</strong>", decodedHtml);
        Assert.Contains("該当ファイル<strong>2</strong>", decodedHtml);
        Assert.Contains("first.sql", decodedHtml);
        Assert.Contains("second.sql", decodedHtml);
    }

    [Fact]
    public async Task WriteAsync_LinksFindingToEncodedLocalSqlFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var sqlDirectory = temp.CreateDirectory("sql");
        var sqlPath = Path.Combine(sqlDirectory, "invalid file #1.sql");
        await File.WriteAllTextAsync(sqlPath, new string(' ', 1500) + "SELECT FROM;", cancellationToken);
        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 120, [sqlDirectory]),
            cancellationToken);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);
        var expectedUri = new Uri(Path.GetFullPath(sqlPath)).AbsoluteUri;

        Assert.Contains(
            $"href=\"{expectedUri}\" target=\"_blank\" rel=\"noopener noreferrer\"",
            html);
        Assert.Contains("invalid file #1.sql</code></a>", html);
        Assert.Contains("invalid%20file%20%231.sql", html);
        Assert.Contains("<wbr>", html);
        Assert.Matches(
            @"行 1 / 列 \d{1,3},\d{3} / offset \d{1,3},\d{3}",
            System.Net.WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task WriteAsync_ShowsDiagnosticsSectionOnlyWhenDiagnosticsExist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var missingDirectory = Path.Combine(temp.Path, "missing");
        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 120, [missingDirectory]),
            cancellationToken);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);

        Assert.Contains("<h2>処理上の問題</h2>", html);
        Assert.Contains("ANALYSIS_DIRECTORY_NOT_FOUND", html);
        Assert.Contains("ANALYSIS_NO_SQL_FILES", html);
    }

    [Fact]
    public async Task WriteAsync_FormatsCountsButNotCompatibilityLevelsOrErrorNumbers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var result = new AnalysisRunResult(
            "1.0",
            "180.59.2",
            DateTimeOffset.UtcNow,
            110,
            180,
            CompatibilityLevelScope.Range,
            false,
            [110],
            [new CompatibilityLevelSummary(
                110,
                3773,
                1000,
                2773,
                [new ParseErrorSummary(46029, "予期しない EOF が見つかりました。", 2773, 1000)])],
            [temp.Path],
            new AnalysisSummary(3773, 1000, 2773, 0, 0),
            [],
            []);

        var written = await new ReportWriter().WriteAsync(
            result,
            Path.Combine(temp.Path, "report"),
            cancellationToken: cancellationToken);
        var html = await File.ReadAllTextAsync(written.HtmlPath, cancellationToken);

        Assert.Contains("<td>3,773</td>", html);
        Assert.Contains("<strong>2,773</strong>", html);
        Assert.Contains("<strong>1,000</strong>", html);
        Assert.Contains("<td class=\"number-cell\">46029</td>", html);
        Assert.DoesNotContain("46,029", html);
        Assert.Contains("<th scope=\"row\">110</th>", html);
    }
}
