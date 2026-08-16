using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MssqlCompatCheck.Analysis;

namespace MssqlCompatCheck.Reporting;

public sealed record ReportWriteResult(string JsonPath, string HtmlPath);

public sealed class ReportWriter
{
    public const string JsonFileName = "analysis-report.json";
    public const string HtmlFileName = "analysis-report.html";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ReportWriteResult> WriteAsync(
        AnalysisRunResult result,
        string outputDirectory,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        var jsonPath = Path.Combine(fullOutputDirectory, JsonFileName);
        var htmlPath = Path.Combine(fullOutputDirectory, HtmlFileName);

        if (!overwrite)
        {
            var existingFiles = new[] { jsonPath, htmlPath }.Where(File.Exists).ToArray();
            if (existingFiles.Length > 0)
            {
                throw new IOException(
                    "既存のレポートを上書きするには上書きオプションを指定してください: " +
                    string.Join(", ", existingFiles));
            }
        }

        Directory.CreateDirectory(fullOutputDirectory);

        await using (var jsonStream = new FileStream(
                         jsonPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 16 * 1024,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(jsonStream, result, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await jsonStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var html = CreateHtml(result);
        await File.WriteAllTextAsync(
                htmlPath,
                html,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);

        return new(jsonPath, htmlPath);
    }

    internal static string CreateHtml(AnalysisRunResult result)
    {
        var selectedLevelCompatible = CountFilesCompatibleAtAllSelectedLevels(result.Items);
        var selectedLevelFailures = result.Summary.Total - selectedLevelCompatible;
        var html = new StringBuilder(16 * 1024);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"ja\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>SQL Server 互換性レベル解析レポート</title>");
        html.AppendLine("<style>");
        html.AppendLine(":root{color-scheme:light;--navy:#102a43;--blue:#1769aa;--surface:#fff;--canvas:#f3f6fa;--line:#d8e1eb;--muted:#5f6f7f;--ok:#137a48;--ok-soft:#eaf7f0;--fail:#b42318;--fail-soft:#fff0ee;--warning:#946200;--shadow:0 12px 32px rgba(16,42,67,.08)}");
        html.AppendLine("*{box-sizing:border-box}html{scroll-behavior:smooth}body{font-family:Inter,system-ui,-apple-system,'Segoe UI',sans-serif;margin:0;color:#1e2936;background:var(--canvas);line-height:1.55}.report-shell{width:min(1480px,calc(100% - 2rem));margin:0 auto;padding:1.25rem 0 3rem}");
        html.AppendLine(".hero{position:relative;overflow:hidden;color:#fff;background:linear-gradient(135deg,#102a43 0%,#164e78 62%,#1769aa 100%);border-radius:1rem;padding:1.65rem 1.8rem;box-shadow:0 18px 42px rgba(16,42,67,.2)}.hero:after{content:'';position:absolute;width:20rem;height:20rem;border-radius:50%;right:-8rem;top:-11rem;background:rgba(255,255,255,.09)}.hero-top{position:relative;z-index:1;display:flex;justify-content:space-between;gap:1rem;align-items:flex-start}.eyebrow{margin:0 0 .25rem;text-transform:uppercase;letter-spacing:.12em;font-size:.72rem;font-weight:700;color:#b9ddf4}.hero h1{margin:0;font-size:clamp(1.45rem,3vw,2.15rem);line-height:1.25}.status-pill{flex:0 0 auto;border:1px solid rgba(255,255,255,.3);border-radius:999px;padding:.45rem .8rem;background:rgba(255,255,255,.13);font-weight:700}.status-pill.is-ok{background:rgba(19,122,72,.45)}.status-pill.is-fail{background:rgba(180,35,24,.48)}");
        html.AppendLine(".meta-grid{position:relative;z-index:1;display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:.65rem;margin-top:1.25rem}.meta-item{min-width:0;border:1px solid rgba(255,255,255,.18);border-radius:.65rem;padding:.65rem .8rem;background:rgba(255,255,255,.09)}.meta-label{display:block;color:#cfe8f8;font-size:.75rem;margin-bottom:.1rem}.meta-item strong,.meta-item time{font-size:.94rem;color:#fff;overflow-wrap:anywhere}");
        html.AppendLine(".panel{background:var(--surface);border:1px solid #e1e8f0;border-radius:.85rem;margin-top:1rem;padding:1.15rem 1.25rem;box-shadow:var(--shadow)}h2{display:flex;align-items:center;gap:.65rem;margin:0 0 .8rem;color:var(--navy);font-size:1.25rem}h2:before{content:'';width:.3rem;height:1.25rem;border-radius:999px;background:var(--blue)}h3{color:var(--navy);margin:1.25rem 0 .65rem}");
        html.AppendLine(".ok{color:var(--ok)}.fail{color:var(--fail)}");
        html.AppendLine("table{width:100%;border-collapse:separate;border-spacing:0;margin:.75rem 0 0;border:1px solid var(--line);border-radius:.65rem;overflow:hidden;background:#fff}th,td{border:0;border-bottom:1px solid var(--line);padding:.55rem .7rem;text-align:left;vertical-align:top}thead th{background:#edf3f8;color:#334e68;font-size:.78rem;letter-spacing:.02em;white-space:nowrap}tbody tr:last-child>th,tbody tr:last-child>td{border-bottom:0}tbody tr:hover>th,tbody tr:hover>td{background:#f7fbff}.table-scroll{max-width:100%;overflow-x:auto;border-radius:.65rem}.summary-table{width:auto;min-width:34rem}.summary-table th,.summary-table td{padding:.45rem .75rem;white-space:nowrap;text-align:right}.summary-table th:first-child{text-align:left}.summary-table tbody th{background:#f4f7fa;color:var(--navy)}");
        html.AppendLine(".error-summary-table{width:auto;max-width:100%;min-width:54rem}.error-summary-table th,.error-summary-table td{padding:.45rem .65rem}.error-summary-table .level-cell{vertical-align:middle;text-align:center;white-space:nowrap;background:#edf3f8;color:var(--navy)}.error-summary-table .number-cell,.error-summary-table .count-cell{text-align:right;white-space:nowrap}.error-summary-table .message-cell{min-width:20rem;max-width:42rem}");
        html.AppendLine("code,pre{font-family:ui-monospace,SFMono-Regular,Consolas,monospace}code{font-size:.88em}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#101c2b;color:#d9e7f3;padding:.85rem;border-radius:.55rem;border:1px solid #253c55}a{color:#0f65a5;text-decoration-thickness:.08em;text-underline-offset:.15em}a:hover{color:#084b7a}.muted{color:var(--muted)}");
        html.AppendLine("details{margin:.45rem 0;border:1px solid var(--line);border-radius:.55rem;background:#fbfcfe}details>summary{cursor:pointer;padding:.5rem .65rem;color:#334e68;font-weight:650}details>ul,details>pre{margin:.2rem .65rem .65rem}.input-panel>summary{font-size:1rem}.input-panel ul{margin:.35rem .65rem .65rem;padding-left:1.25rem}.diagnostic-list{margin:0;padding-left:1.35rem}.diagnostic-error{color:var(--fail)}.diagnostic-warning{color:var(--warning)}");
        html.AppendLine(".level-tabs{display:flex;flex-wrap:wrap;gap:.4rem;align-items:flex-end;margin-top:.25rem}.tab-input{position:absolute;opacity:0;pointer-events:none}.tab-label{cursor:pointer;border:1px solid #b8c7d6;border-radius:999px;padding:.48rem .9rem;background:#edf3f8;color:var(--navy);font-weight:700;transition:background .15s,color .15s,transform .15s}.tab-label:hover{transform:translateY(-1px);background:#dfeaf4}.tab-input:focus-visible+.tab-label{outline:3px solid #75b5e3;outline-offset:2px}.tab-input:checked+.tab-label{background:var(--navy);border-color:var(--navy);color:#fff;box-shadow:0 5px 14px rgba(16,42,67,.18)}.tab-panels{flex-basis:100%;border:1px solid var(--line);border-radius:.75rem;padding:1rem;margin-top:.25rem;background:#fbfcfe}.tab-panel{display:none}.tab-panel>h3{margin-top:0}.level-summary{display:flex;gap:.65rem;flex-wrap:wrap}.level-summary p{margin:0;border-radius:999px;padding:.35rem .7rem;background:#fff;border:1px solid var(--line)}");
        html.AppendLine(".error-groups{display:grid;gap:.8rem;margin-top:.85rem}.error-group{overflow:hidden;border:1px solid var(--line);border-radius:.75rem;background:#fff;scroll-margin-top:1rem}.error-group-head{display:flex;justify-content:space-between;align-items:flex-start;gap:1rem;padding:.8rem .9rem;background:linear-gradient(180deg,#fff7f6,#fff)}.error-identity{display:flex;align-items:flex-start;gap:.7rem;min-width:0}.error-number{flex:0 0 auto;border-radius:999px;padding:.2rem .55rem;background:var(--fail-soft);border:1px solid #efc1bb;color:var(--fail);font:700 .78rem ui-monospace,SFMono-Regular,Consolas,monospace}.error-message{margin:0;color:#4a2521;font-size:1rem;overflow-wrap:anywhere}.error-stats{display:flex;gap:.4rem;flex:0 0 auto}.error-stat{border:1px solid #efc1bb;border-radius:.5rem;padding:.3rem .55rem;background:#fff;color:var(--muted);font-size:.72rem;text-align:center}.error-stat strong{display:block;color:var(--fail);font-size:1rem;line-height:1.1}.affected-files{display:grid}.affected-file{padding:.75rem .9rem;border-top:1px solid var(--line)}.affected-file-head{display:grid;grid-template-columns:minmax(0,1fr) minmax(13rem,22rem);gap:1rem}.finding-label{display:block;margin-bottom:.2rem;color:var(--muted);font-size:.72rem;font-weight:700;text-transform:uppercase;letter-spacing:.05em}.finding-path,.finding-source{min-width:0}.finding-path a code{font-size:.82rem;overflow-wrap:anywhere;word-break:break-word}.finding-path .muted{display:block;margin-top:.25rem;font-size:.78rem}.finding-source{overflow-wrap:anywhere;word-break:break-word;font-size:.88rem}.occurrence-list{display:flex;flex-wrap:wrap;gap:.35rem;margin:.55rem 0 0;padding:0;list-style:none}.position-pill{display:inline-block;border:1px solid var(--line);border-radius:999px;padding:.18rem .5rem;background:#f7fafc;color:#486581;font-size:.76rem}");
        html.AppendLine("@media(max-width:760px){.report-shell{width:min(100% - 1rem,1480px);padding-top:.5rem}.hero{border-radius:.75rem;padding:1.2rem}.hero-top,.error-group-head{display:block}.status-pill{display:inline-block;margin-top:.8rem}.meta-grid{grid-template-columns:1fr 1fr}.panel{padding:.9rem}.summary-table{min-width:30rem}.tab-label{padding:.42rem .7rem}.tab-panels{padding:.65rem}.affected-file-head{grid-template-columns:1fr}.error-stats{margin-top:.65rem}th,td{padding:.45rem .55rem}}@media(max-width:480px){.meta-grid{grid-template-columns:1fr}.hero h1{font-size:1.35rem}}@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}.tab-label{transition:none}}@media print{body{background:#fff}.report-shell{width:100%;padding:0}.hero,.panel{box-shadow:none}.hero{background:#fff;color:#000;border:1px solid #999}.hero *{color:#000!important}.tab-panel{display:block!important;break-inside:avoid}.tab-input,.tab-label{display:none}.panel,.error-group{break-inside:avoid}}");
        foreach (var level in result.AnalyzedLevels)
        {
            html.Append("#compatibility-level-").Append(level)
                .Append(":checked~.tab-panels #panel-level-").Append(level)
                .AppendLine("{display:block}");
        }

        html.AppendLine("</style></head><body><main class=\"report-shell\">");
        html.AppendLine("<header class=\"hero\">");
        html.AppendLine("<div class=\"hero-top\"><div><p class=\"eyebrow\">MSSQL COMPATIBILITY CHECK</p><h1>SQL Server 互換性レベル解析レポート</h1></div>");
        html.Append("<div class=\"status-pill ").Append(selectedLevelFailures == 0 ? "is-ok" : "is-fail")
            .Append("\">").Append(selectedLevelFailures == 0 ? "Parse成功" : $"要確認 {FormatCount(selectedLevelFailures)} 件")
            .AppendLine("</div></div>");
        html.AppendLine("<div class=\"meta-grid\">");
        html.Append("<div class=\"meta-item\"><span class=\"meta-label\">現在の互換性レベル → 変更先の互換性レベル</span><strong>").Append(result.CurrentLevel)
            .Append(" → ").Append(result.TargetLevel).AppendLine("</strong></div>");
        html.Append("<div class=\"meta-item\"><span class=\"meta-label\">解析方式</span><strong>")
            .Append(result.LevelScope == CompatibilityLevelScope.TargetOnly
                ? "変更先レベルのみ"
                : "範囲内の全レベル")
            .AppendLine("</strong></div>");
        html.Append("<div class=\"meta-item\"><span class=\"meta-label\">ScriptDOM</span><strong>").Append(E(result.ScriptDomVersion))
            .AppendLine("</strong></div>");
        html.Append("<div class=\"meta-item\"><span class=\"meta-label\">Unexpected EOF (46029)</span><strong>")
            .Append(result.IgnoreUnexpectedEof ? "除外する" : "除外しない")
            .AppendLine("</strong></div>");
        html.Append("<div class=\"meta-item\"><span class=\"meta-label\">生成日時 (UTC)</span><time>")
            .Append(E(result.GeneratedAtUtc.ToString("O"))).AppendLine("</time></div>");
        html.AppendLine("</div></header>");

        html.AppendLine("<section class=\"panel summary-panel\">");
        AppendLevelSummaryTable(html, result.LevelSummaries);

        html.AppendLine("</section>");

        html.AppendLine("<details class=\"panel input-panel\"><summary>入力ディレクトリ</summary><ul>");
        foreach (var directory in result.InputDirectories)
        {
            html.Append("<li><code>").Append(E(directory)).AppendLine("</code></li>");
        }

        html.AppendLine("</ul></details>");
        AppendDiagnostics(html, result.Diagnostics);
        AppendLevelResults(html, result.AnalyzedLevels, result.LevelSummaries, result.Items);
        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private static void AppendLevelSummaryTable(
        StringBuilder html,
        IReadOnlyList<CompatibilityLevelSummary> levelSummaries)
    {
        html.AppendLine("<h2>互換性レベル別サマリー</h2>");
        if (levelSummaries.Count == 0)
        {
            html.AppendLine("<p class=\"muted\">集計結果はありません。</p>");
            return;
        }

        html.AppendLine("<div class=\"table-scroll\"><table class=\"summary-table\"><thead><tr><th>互換性レベル</th><th>対象件数</th><th>Parse 成功</th><th>Parse 失敗</th></tr></thead><tbody>");
        foreach (var summary in levelSummaries.OrderBy(static summary => summary.Level))
        {
            html.Append("<tr><th scope=\"row\">").Append(summary.Level)
                .Append("</th><td>").Append(FormatCount(summary.Total))
                .Append("</td><td class=\"ok\"><strong>").Append(FormatCount(summary.Compatible))
                .Append("</strong></td><td class=\"")
                .Append(summary.ParseFailures == 0 ? "ok" : "fail")
                .Append("\"><strong>").Append(FormatCount(summary.ParseFailures))
                .AppendLine("</strong></td></tr>");
        }

        html.AppendLine("</tbody></table></div>");

        var errorSummaries = levelSummaries
            .SelectMany(summary => summary.ErrorSummaries.Select(error => (summary.Level, Error: error)))
            .ToArray();
        html.AppendLine("<h3>Parseエラー内容別集計</h3>");
        if (errorSummaries.Length == 0)
        {
            html.AppendLine("<p class=\"muted\">Parseエラーはありません。</p>");
            return;
        }

        html.AppendLine("<div class=\"table-scroll\"><table class=\"error-summary-table\"><thead><tr><th>互換性レベル</th><th>エラー番号</th><th>エラー内容</th><th>発生件数</th><th>該当ファイル数</th></tr></thead><tbody>");
        foreach (var levelGroup in errorSummaries
                     .GroupBy(static summary => summary.Level)
                     .OrderBy(static group => group.Key))
        {
            var rows = levelGroup.ToArray();
            for (var index = 0; index < rows.Length; index++)
            {
                var summary = rows[index];
                html.Append("<tr>");
                if (index == 0)
                {
                    html.Append("<th scope=\"rowgroup\" rowspan=\"").Append(rows.Length)
                        .Append("\" class=\"level-cell\">").Append(summary.Level).AppendLine("</th>");
                }

                html.Append("<td class=\"number-cell\">").Append(summary.Error.Number)
                    .Append("</td><td class=\"message-cell\">").Append(E(summary.Error.Message))
                    .Append("</td><td class=\"fail count-cell\"><strong>").Append(FormatCount(summary.Error.OccurrenceCount))
                    .Append("</strong></td><td class=\"fail count-cell\"><strong>").Append(FormatCount(summary.Error.AffectedFiles))
                    .AppendLine("</strong></td></tr>");
            }
        }

        html.AppendLine("</tbody></table></div>");
    }

    private static void AppendDiagnostics(StringBuilder html, IReadOnlyList<AnalysisDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        html.AppendLine("<section class=\"panel diagnostics-panel\"><h2>処理上の問題</h2>");
        html.AppendLine("<ul class=\"diagnostic-list\">");
        foreach (var diagnostic in diagnostics)
        {
            var cssClass = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => "diagnostic-error",
                DiagnosticSeverity.Warning => "diagnostic-warning",
                _ => "",
            };
            html.Append("<li class=\"").Append(cssClass).Append("\"><strong>")
                .Append(E(diagnostic.Code)).Append("</strong>: ").Append(E(diagnostic.Message));
            if (diagnostic.Path is not null)
            {
                html.Append(" <code>").Append(E(diagnostic.Path)).Append("</code>");
            }

            html.AppendLine("</li>");
        }

        html.AppendLine("</ul></section>");
    }

    private static void AppendLevelResults(
        StringBuilder html,
        IReadOnlyList<int> analyzedLevels,
        IReadOnlyList<CompatibilityLevelSummary> levelSummaries,
        IReadOnlyList<AnalysisItemResult> items)
    {
        html.AppendLine("<section class=\"panel results-panel\"><h2>互換性レベル別の解析結果</h2>");
        if (analyzedLevels.Count == 0)
        {
            html.AppendLine("<p class=\"muted\">解析対象の互換性レベルはありません。</p>");
            html.AppendLine("</section>");
            return;
        }

        html.AppendLine("<div class=\"level-tabs\" role=\"tablist\" aria-label=\"互換性レベル\">");
        for (var index = 0; index < analyzedLevels.Count; index++)
        {
            var level = analyzedLevels[index];
            var parseFailures = levelSummaries.Single(summary => summary.Level == level).ParseFailures;
            html.Append("<input class=\"tab-input\" type=\"radio\" name=\"compatibility-level\" id=\"compatibility-level-")
                .Append(level).Append('"');
            if (index == 0)
            {
                html.Append(" checked");
            }

            html.Append("><label class=\"tab-label\" role=\"tab\" for=\"compatibility-level-")
                .Append(level).Append("\" aria-controls=\"panel-level-").Append(level).Append("\">")
                .Append(level).Append(" (").Append(FormatCount(parseFailures)).AppendLine(")</label>");
        }

        html.AppendLine("<div class=\"tab-panels\">");
        foreach (var level in analyzedLevels)
        {
            var summary = levelSummaries.Single(summary => summary.Level == level);
            html.Append("<section class=\"tab-panel\" id=\"panel-level-").Append(level)
                .Append("\" role=\"tabpanel\"><h3>互換性レベル ").Append(level).AppendLine("</h3>");
            html.Append("<div class=\"level-summary\"><p class=\"ok\"><strong>Parse 成功: ")
                .Append(FormatCount(summary.Compatible)).Append("</strong></p><p class=\"fail\"><strong>Parse 失敗: ")
                .Append(FormatCount(summary.ParseFailures)).AppendLine("</strong></p></div>");
            AppendLevelFindings(html, level, summary, items);
            html.AppendLine("</section>");
        }

        html.AppendLine("</div></div></section>");
    }

    private static void AppendLevelFindings(
        StringBuilder html,
        int level,
        CompatibilityLevelSummary summary,
        IReadOnlyList<AnalysisItemResult> items)
    {
        if (summary.Compatible > 0)
        {
            html.Append("<p class=\"muted\">このレベルでParseに成功した ").Append(FormatCount(summary.Compatible))
                .AppendLine(" 件は詳細表示を省略しています。</p>");
        }

        var errorGroups = items
            .SelectMany(item =>
            {
                var result = item.LevelResults.Single(levelResult => levelResult.Level == level);
                return result.Errors.Select(error => (Item: item, Result: result, Error: error));
            })
            .GroupBy(entry => (entry.Error.Number, entry.Error.Message))
            .Select(group => new
            {
                group.Key.Number,
                group.Key.Message,
                Entries = group.ToArray(),
                OccurrenceCount = group.Count(),
                AffectedFiles = group.Select(entry => entry.Item.FilePath).Distinct(GetPathComparer()).Count(),
            })
            .OrderByDescending(static group => group.OccurrenceCount)
            .ThenBy(static group => group.Number)
            .ThenBy(static group => group.Message, StringComparer.Ordinal)
            .ToArray();
        if (errorGroups.Length == 0)
        {
            html.AppendLine("<p class=\"muted\">このレベルで要確認のファイルはありません。</p>");
            return;
        }

        html.AppendLine("<div class=\"error-groups\">");
        for (var groupIndex = 0; groupIndex < errorGroups.Length; groupIndex++)
        {
            var errorGroup = errorGroups[groupIndex];
            html.Append("<article class=\"error-group\" id=\"")
                .Append(BuildErrorGroupId(level, groupIndex)).AppendLine("\">");
            html.Append("<div class=\"error-group-head\"><div class=\"error-identity\"><span class=\"error-number\">")
                .Append(errorGroup.Number).Append("</span><h4 class=\"error-message\">")
                .Append(E(errorGroup.Message)).AppendLine("</h4></div>");
            html.Append("<div class=\"error-stats\"><span class=\"error-stat\">発生件数<strong>")
                .Append(FormatCount(errorGroup.OccurrenceCount))
                .Append("</strong></span><span class=\"error-stat\">該当ファイル<strong>")
                .Append(FormatCount(errorGroup.AffectedFiles)).AppendLine("</strong></span></div></div>");
            html.AppendLine("<div class=\"affected-files\">");

            foreach (var fileGroup in errorGroup.Entries
                         .GroupBy(entry => entry.Item.FilePath, GetPathComparer())
                         .OrderBy(static group => group.Key, GetPathComparer()))
            {
                var firstEntry = fileGroup.First();
                var item = firstEntry.Item;
                html.AppendLine("<div class=\"affected-file\"><div class=\"affected-file-head\">");
                html.Append("<div class=\"finding-path\"><span class=\"finding-label\">該当ファイル</span><a href=\"")
                    .Append(E(ToFileUri(item.FilePath)))
                    .Append("\" target=\"_blank\" rel=\"noopener noreferrer\"><code>");
                AppendBreakablePath(html, item.FilePath);
                html.Append("</code></a><span class=\"muted\">QUOTED_IDENTIFIER: ")
                    .Append(item.QuotedIdentifier ? "ON" : "OFF").AppendLine("</span></div>");
                html.Append("<div class=\"finding-source\"><span class=\"finding-label\">ソース</span>");
                AppendSource(html, item.Source);
                html.AppendLine("</div></div>");
                html.AppendLine("<ul class=\"occurrence-list\">");
                foreach (var occurrence in fileGroup.OrderBy(static entry => entry.Error.Offset))
                {
                    html.Append("<li><span class=\"position-pill\">行 ").Append(FormatCount(occurrence.Error.Line))
                        .Append(" / 列 ").Append(FormatCount(occurrence.Error.Column))
                        .Append(" / offset ").Append(FormatCount(occurrence.Error.Offset)).AppendLine("</span></li>");
                }

                html.AppendLine("</ul>");

                var contextIssue = firstEntry.Result.Errors
                    .Where(error => error.Line > 0)
                    .OrderBy(error => error.Line)
                    .ThenBy(error => error.Column)
                    .FirstOrDefault();
                if (firstEntry.Result.ErrorContext is not null &&
                    contextIssue is not null &&
                    fileGroup.Any(entry => entry.Error == contextIssue))
                {
                    html.Append("<details><summary>エラー周辺</summary><pre>")
                        .Append(E(firstEntry.Result.ErrorContext)).AppendLine("</pre></details>");
                }

                if (item.Sql is not null)
                {
                    html.Append("<details><summary>SQL 全文</summary><pre>")
                        .Append(E(item.Sql)).AppendLine("</pre></details>");
                }

                html.AppendLine("</div>");
            }

            html.AppendLine("</div></article>");
        }

        html.AppendLine("</div>");
    }

    private static void AppendSource(StringBuilder html, SqlSourceMetadata? source)
    {
        if (source is null)
        {
            html.Append("通常ファイル");
            return;
        }

        html.Append(E(source.SourceType ?? "エクスポート"));
        if (!string.IsNullOrWhiteSpace(source.ObjectName))
        {
            html.Append("<br>");
            if (!string.IsNullOrWhiteSpace(source.SchemaName))
            {
                html.Append(E(source.SchemaName)).Append('.');
            }

            html.Append(E(source.ObjectName));
        }

        var queryHashes = source.QueryHashes is { Count: > 0 }
            ? source.QueryHashes
            : string.IsNullOrWhiteSpace(source.QueryHash) ? [] : [source.QueryHash];
        foreach (var queryHash in queryHashes)
        {
            html.Append("<br><code>").Append(E(queryHash)).Append("</code>");
        }
    }

    internal static string FormatCount(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

    private static int CountFilesCompatibleAtAllSelectedLevels(IReadOnlyList<AnalysisItemResult> items) =>
        items.Count(item => item.LevelResults.All(level => level.ParseSucceeded));

    private static string BuildErrorGroupId(int level, int groupIndex) =>
        $"error-group-{level}-{groupIndex + 1}";

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static void AppendBreakablePath(StringBuilder html, string path)
    {
        const int maximumRunLength = 32;
        var runLength = 0;
        foreach (var character in path)
        {
            html.Append(E(character.ToString()));
            runLength++;
            if (character is '\\' or '/' or '_' or '-' || runLength >= maximumRunLength)
            {
                html.Append("<wbr>");
                runLength = 0;
            }
        }
    }

    private static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static string E(string value) => HtmlEncoder.Default.Encode(value);
}
