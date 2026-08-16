using MssqlCompatCheck.Analysis;
using MssqlCompatCheck.Export;
using Xunit;

namespace MssqlCompatCheck.Tests.Integration;

public sealed class ExportAnalyzeRoundTripTests
{
    [Fact]
    public async Task ExportedModulesAndCacheCanBeAnalyzedOfflineWithManifestMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "MssqlCompatCheck.Tests", Guid.NewGuid().ToString("N"));
        var exportDirectory = Path.Combine(temporaryRoot, "export");

        try
        {
            var collector = new StubCollector(new DatabaseExportSnapshot(
                "16.0.1000.0",
                16,
                [new DatabaseModule(
                    42,
                    "dbo",
                    "ExportedProcedure",
                    DatabaseModuleKind.StoredProcedure,
                    "CREATE PROCEDURE dbo.ExportedProcedure AS SELECT 1;",
                    UsesQuotedIdentifier: false)],
                [new CachedQuery(
                    "SELECT 2;",
                    UsesQuotedIdentifier: true,
                    OccurrenceCount: 3,
                    ExecutionCount: 7,
                    TotalWorkerTime: 99,
                    LastExecutionTime: null,
                    QueryHashes: ["ABCDEF"])],
                []));

            var exported = await new ExportService(collector).ExportAsync(
                new ExportOptions(
                    "Server=fake;Integrated Security=true",
                    "TestDb",
                    exportDirectory,
                    IncludeModules: true,
                    IncludeQueryCache: true),
                cancellationToken);

            Assert.Equal(0, exported.ExitCode);

            var analyzed = await new AnalysisService().AnalyzeAsync(
                new AnalysisOptions(110, 170, [exportDirectory]),
                cancellationToken);

            Assert.False(analyzed.HasOperationalErrors);
            Assert.False(analyzed.HasParseFailures);
            Assert.Equal(2, analyzed.Summary.Total);

            var module = Assert.Single(analyzed.Items, item => item.Source?.SourceType == "storedProcedure");
            Assert.False(module.QuotedIdentifier);
            Assert.Equal("ExportedProcedure", module.Source?.ObjectName);

            var cache = Assert.Single(analyzed.Items, item => item.Source?.SourceType == "queryCache");
            Assert.True(cache.QuotedIdentifier);
            Assert.Equal(3, cache.Source?.OccurrenceCount);
            Assert.Equal(7, cache.Source?.ExecutionCount);
            Assert.Equal(99, cache.Source?.TotalWorkerTime);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private sealed class StubCollector(DatabaseExportSnapshot snapshot) : IDatabaseExportCollector
    {
        public Task<DatabaseExportSnapshot> CollectAsync(
            DatabaseExportRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }
    }
}
