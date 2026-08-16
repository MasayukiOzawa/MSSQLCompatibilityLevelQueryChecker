using MssqlCompatCheck.Analysis;
using Xunit;

namespace MssqlCompatCheck.Tests.Analysis;

public sealed class AnalysisServiceTests
{
    [Fact]
    public void ValidateOptions_RequiresAtLeastOneDirectory()
    {
        var options = new AnalysisOptions(110, 170, []);

        var exception = Assert.Throws<ArgumentException>(() => AnalysisService.ValidateOptions(options));

        Assert.Contains("1つ以上", exception.Message);
    }

    [Theory]
    [InlineData(75, 170)]
    [InlineData(110, 175)]
    public void ValidateOptions_RejectsUnsupportedLevels(int currentLevel, int targetLevel)
    {
        var options = new AnalysisOptions(currentLevel, targetLevel, ["sql"]);

        Assert.Throws<ArgumentException>(() => AnalysisService.ValidateOptions(options));
    }

    [Theory]
    [InlineData(110, 110)]
    [InlineData(170, 160)]
    public void ValidateOptions_RequiresTargetGreaterThanCurrent(int currentLevel, int targetLevel)
    {
        var options = new AnalysisOptions(currentLevel, targetLevel, ["sql"]);

        Assert.Throws<ArgumentException>(() => AnalysisService.ValidateOptions(options));
    }

    [Fact]
    public async Task AnalyzeAsync_RecursesAndDeduplicatesOverlappingRoots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        var nested = temp.CreateDirectory("one", "two", "three");
        await temp.WriteSqlAsync("root.sql", "SELECT 1;");
        await temp.WriteSqlAsync(Path.Combine("one", "two", "three", "nested.SQL"), "SELECT 2;");
        await File.WriteAllTextAsync(Path.Combine(nested, "ignored.txt"), "SELECT 3;", cancellationToken);

        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 170, [temp.Path, Path.Combine(temp.Path, "one")]),
            cancellationToken);

        Assert.Equal(2, result.Summary.Total);
        Assert.Equal(2, result.Items.Select(item => item.FilePath).Distinct(GetPathComparer()).Count());
        Assert.All(result.Items, item => Assert.Equal(AnalysisStatus.Compatible, item.Status));
        Assert.Contains(result.Items, item => item.FilePath.EndsWith("nested.SQL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeAsync_ClassifiesBothParserOutcomes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        await temp.WriteSqlAsync("compatible.sql", "SELECT 1;");
        await temp.WriteSqlAsync("target-incompatible.sql", "SELECT * FROM a, b WHERE a.id *= b.id;");
        await temp.WriteSqlAsync("target-only.sql", "DROP TABLE IF EXISTS dbo.t;");
        await temp.WriteSqlAsync("unparseable.sql", "SELECT FROM;");

        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(80, 170, [temp.Path]),
            cancellationToken);
        var statuses = result.Items.ToDictionary(
            item => Path.GetFileName(item.FilePath),
            item => item.Status,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(AnalysisStatus.Compatible, statuses["compatible.sql"]);
        Assert.Equal(AnalysisStatus.TargetIncompatible, statuses["target-incompatible.sql"]);
        Assert.Equal(AnalysisStatus.CurrentInvalidTargetValid, statuses["target-only.sql"]);
        Assert.Equal(AnalysisStatus.Unparseable, statuses["unparseable.sql"]);
        Assert.NotEmpty(result.Items.Single(item => item.Status == AnalysisStatus.TargetIncompatible).TargetErrors);
        Assert.NotEmpty(result.Items.Single(item => item.Status == AnalysisStatus.Unparseable).CurrentErrors);
    }

    [Fact]
    public async Task AnalyzeAsync_ParsesEverySupportedLevelInRequestedRange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        await temp.WriteSqlAsync("level-sensitive.sql", "DROP TABLE IF EXISTS dbo.t;");

        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 150, [temp.Path]),
            cancellationToken);

        Assert.Equal([120, 130, 140, 150], result.AnalyzedLevels);
        var item = Assert.Single(result.Items);
        Assert.Equal(result.AnalyzedLevels, item.LevelResults.Select(level => level.Level));
        Assert.NotEmpty(item.CurrentErrors);
        Assert.False(item.LevelResults.Single(level => level.Level == 120).ParseSucceeded);
        Assert.True(item.LevelResults.Single(level => level.Level == 130).ParseSucceeded);
        Assert.True(item.LevelResults.Single(level => level.Level == 140).ParseSucceeded);
        Assert.True(item.LevelResults.Single(level => level.Level == 150).ParseSucceeded);
        Assert.Equal(4, result.LevelSummaries.Count);
        Assert.DoesNotContain(result.LevelSummaries, level => level.Level == 110);
        Assert.Equal(0, result.LevelSummaries.Single(level => level.Level == 150).ParseFailures);
    }

    [Fact]
    public async Task AnalyzeAsync_TargetOnly_ParsesOnlyTargetLevel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        await temp.WriteSqlAsync("target-valid.sql", "DROP TABLE IF EXISTS dbo.t;");

        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(
                110,
                150,
                [temp.Path],
                LevelScope: CompatibilityLevelScope.TargetOnly),
            cancellationToken);

        Assert.Equal(CompatibilityLevelScope.TargetOnly, result.LevelScope);
        Assert.Equal([150], result.AnalyzedLevels);
        var item = Assert.Single(result.Items);
        var levelResult = Assert.Single(item.LevelResults);
        Assert.Equal(150, levelResult.Level);
        Assert.True(levelResult.ParseSucceeded);
        Assert.Equal(AnalysisStatus.CurrentInvalidTargetValid, item.Status);
        Assert.NotEmpty(item.CurrentErrors);
        Assert.Empty(item.TargetErrors);
        Assert.False(result.HasParseFailures);
    }

    [Fact]
    public void ValidateOptions_RejectsUnknownLevelScope()
    {
        var options = new AnalysisOptions(
            110,
            150,
            ["sql"],
            LevelScope: (CompatibilityLevelScope)999);

        Assert.Throws<ArgumentException>(() => AnalysisService.ValidateOptions(options));
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsDiscoveryAndFileProgress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        await temp.WriteSqlAsync("one.sql", "SELECT 1;");
        await temp.WriteSqlAsync("two.sql", "SELECT 2;");
        var progress = new RecordingProgress();

        await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 130, [temp.Path]),
            progress,
            cancellationToken);

        Assert.Equal(AnalysisProgressStage.DiscoveringFiles, progress.Updates[0].Stage);
        var started = progress.Updates[1];
        Assert.Equal(AnalysisProgressStage.AnalyzingFiles, started.Stage);
        Assert.Equal(0, started.ProcessedFiles);
        Assert.Equal(2, started.TotalFiles);
        Assert.Equal(2, started.TotalLevels);
        Assert.Contains(
            progress.Updates,
            update => update.Stage == AnalysisProgressStage.AnalyzingFiles &&
                      update.ProcessedFiles == 1 &&
                      update.TotalFiles == 2);
        var completed = progress.Updates[^1];
        Assert.Equal(AnalysisProgressStage.Completed, completed.Stage);
        Assert.Equal(2, completed.ProcessedFiles);
        Assert.Equal(2, completed.TotalFiles);
        Assert.Equal(2, completed.TotalLevels);
    }

    [Fact]
    public async Task AnalyzeAsync_SummarizesParseErrorsByLevelAndContent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        await temp.WriteSqlAsync("invalid-one.sql", "SELECT FROM;");
        await temp.WriteSqlAsync("invalid-two.sql", "SELECT FROM;");

        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 130, [temp.Path]),
            cancellationToken);

        foreach (var levelSummary in result.LevelSummaries)
        {
            Assert.Equal(2, levelSummary.ParseFailures);
            var error = Assert.Single(levelSummary.ErrorSummaries);
            Assert.NotEqual(0, error.Number);
            Assert.NotEmpty(error.Message);
            Assert.Equal(2, error.OccurrenceCount);
            Assert.Equal(2, error.AffectedFiles);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_IgnoreUnexpectedEof_RemovesOnlyError46029()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        await temp.WriteSqlAsync("unexpected-eof.sql", "IF NOT EXISTS(SELECT 1)");
        await temp.WriteSqlAsync("other-error.sql", "SELECT FROM;");

        var defaultResult = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 130, [temp.Path]),
            cancellationToken);
        var ignoredResult = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 130, [temp.Path], IgnoreUnexpectedEof: true),
            cancellationToken);

        var defaultEof = defaultResult.Items.Single(item =>
            item.FilePath.EndsWith("unexpected-eof.sql", StringComparison.OrdinalIgnoreCase));
        Assert.All(
            defaultEof.LevelResults,
            level => Assert.Contains(level.Errors, error => error.Number == 46029));

        var ignoredEof = ignoredResult.Items.Single(item =>
            item.FilePath.EndsWith("unexpected-eof.sql", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AnalysisStatus.Compatible, ignoredEof.Status);
        Assert.All(ignoredEof.LevelResults, level => Assert.Empty(level.Errors));

        var otherError = ignoredResult.Items.Single(item =>
            item.FilePath.EndsWith("other-error.sql", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(AnalysisStatus.Compatible, otherError.Status);
        Assert.All(
            ignoredResult.LevelSummaries,
            level => Assert.DoesNotContain(level.ErrorSummaries, error => error.Number == 46029));
        Assert.True(ignoredResult.IgnoreUnexpectedEof);
    }

    [Fact]
    public async Task AnalyzeAsync_OmitsFullSqlByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TemporaryDirectory();
        await temp.WriteSqlAsync("secret.sql", "SELECT 'do-not-leak-secret';");

        var result = await new AnalysisService().AnalyzeAsync(
            new AnalysisOptions(110, 170, [temp.Path]),
            cancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Null(item.Sql);
        Assert.Equal(64, item.Sha256.Length);
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class RecordingProgress : IProgress<AnalysisProgress>
    {
        public List<AnalysisProgress> Updates { get; } = [];

        public void Report(AnalysisProgress value) => Updates.Add(value);
    }
}
