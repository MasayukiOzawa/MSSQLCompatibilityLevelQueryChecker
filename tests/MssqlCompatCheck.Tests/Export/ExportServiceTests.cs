using System.Text.Json;
using MssqlCompatCheck.Export;
using Xunit;

namespace MssqlCompatCheck.Tests.Export;

public sealed class ExportServiceTests
{
    private const string ConnectionString =
        "Server=fake-server;User ID=test-user;Password=do-not-persist-this-secret;Encrypt=true";

    [Fact]
    public async Task ExportAsync_WritesModulesAndCacheToSeparateDirectoriesWithManifests()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var collector = new FakeDatabaseExportCollector(CreateSnapshot(
            modules:
            [
                new DatabaseModule(
                    123,
                    "dbo",
                    "Process/Order",
                    DatabaseModuleKind.StoredProcedure,
                    "SELECT 1;",
                    UsesQuotedIdentifier: true),
            ],
            cachedQueries:
            [
                new CachedQuery(
                    "SELECT 2;",
                    UsesQuotedIdentifier: false,
                    OccurrenceCount: 2,
                    ExecutionCount: 8,
                    TotalWorkerTime: 50,
                    LastExecutionTime: new DateTime(2026, 8, 16, 1, 2, 3),
                    QueryHashes: ["0011223344556677"]),
            ]));
        var service = new ExportService(collector);

        var result = await service.ExportAsync(CreateOptions(
            temporaryDirectory.Path,
            includeModules: true,
            includeQueryCache: true), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.ModulesExported);
        Assert.Equal(1, result.CacheQueriesExported);
        Assert.True(File.Exists(Path.Combine(temporaryDirectory.Path, ".mssql-compat-output")));
        Assert.True(File.Exists(Path.Combine(temporaryDirectory.Path, "export-manifest.json")));

        var moduleDirectory = Path.Combine(temporaryDirectory.Path, "modules");
        var cacheDirectory = Path.Combine(temporaryDirectory.Path, "cache");
        var moduleFile = Assert.Single(Directory.GetFiles(moduleDirectory, "*.sql", SearchOption.AllDirectories));
        var cacheFile = Assert.Single(Directory.GetFiles(cacheDirectory, "*.sql"));
        Assert.Equal("P", Path.GetFileName(Path.GetDirectoryName(moduleFile)));
        Assert.Equal(
            "SELECT 1;",
            await File.ReadAllTextAsync(moduleFile, TestContext.Current.CancellationToken));
        Assert.Equal(
            "SELECT 2;",
            await File.ReadAllTextAsync(cacheFile, TestContext.Current.CancellationToken));

        using var moduleManifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(moduleDirectory, "manifest.json"),
                TestContext.Current.CancellationToken));
        var moduleEntry = Assert.Single(
            moduleManifest.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("storedProcedure", moduleEntry.GetProperty("sourceType").GetString());
        Assert.Equal("P", moduleEntry.GetProperty("objectType").GetString());
        Assert.StartsWith("P/", moduleEntry.GetProperty("relativePath").GetString());
        Assert.Equal(123, moduleEntry.GetProperty("objectId").GetInt32());
        Assert.True(moduleEntry.GetProperty("usesQuotedIdentifier").GetBoolean());
        Assert.Equal("exported", moduleEntry.GetProperty("status").GetString());

        using var typeManifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(moduleDirectory, "P", "manifest.json"),
                TestContext.Current.CancellationToken));
        var typeEntry = Assert.Single(typeManifest.RootElement.GetProperty("entries").EnumerateArray());
        Assert.DoesNotContain("/", typeEntry.GetProperty("relativePath").GetString());

        using var cacheManifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(cacheDirectory, "manifest.json"),
                TestContext.Current.CancellationToken));
        var cacheEntry = Assert.Single(
            cacheManifest.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("queryCache", cacheEntry.GetProperty("sourceType").GetString());
        Assert.Equal(2, cacheEntry.GetProperty("occurrenceCount").GetInt64());
        Assert.Equal(8, cacheEntry.GetProperty("executionCount").GetInt64());
        Assert.Equal(50, cacheEntry.GetProperty("totalWorkerTime").GetInt64());

        using var rootManifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(temporaryDirectory.Path, "export-manifest.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal("1.0", rootManifest.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("TestDb", rootManifest.RootElement.GetProperty("database").GetString());
        Assert.Equal(2, rootManifest.RootElement.GetProperty("selectedSources").GetArrayLength());
    }

    [Fact]
    public async Task ExportAsync_EncryptedModuleIsSkippedAndReturnsExitCodeOne()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var collector = new FakeDatabaseExportCollector(CreateSnapshot(
            modules:
            [
                new DatabaseModule(
                    10,
                    "dbo",
                    "Visible",
                    DatabaseModuleKind.StoredProcedure,
                    "SELECT 1;",
                    UsesQuotedIdentifier: true),
                new DatabaseModule(
                    11,
                    "dbo",
                    "Encrypted",
                    DatabaseModuleKind.StoredProcedure,
                    Definition: null,
                    UsesQuotedIdentifier: false),
            ]));
        var service = new ExportService(collector);

        var result = await service.ExportAsync(CreateOptions(
            temporaryDirectory.Path,
            includeModules: true,
            includeQueryCache: false), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.ModulesExported);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(Directory.GetFiles(
            Path.Combine(temporaryDirectory.Path, "modules"),
            "*.sql",
            SearchOption.AllDirectories));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "EncryptedOrUnavailableDefinition" &&
                          diagnostic.Severity == ExportDiagnosticSeverity.Warning);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(temporaryDirectory.Path, "modules", "manifest.json"),
            TestContext.Current.CancellationToken));
        var entries = manifest.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        var skipped = Assert.Single(entries, entry => entry.GetProperty("status").GetString() == "skipped");
        Assert.Equal(11, skipped.GetProperty("objectId").GetInt32());
        Assert.False(skipped.TryGetProperty("relativePath", out _));
        Assert.Equal(
            "EncryptedOrUnavailableDefinition",
            skipped.GetProperty("diagnosticCode").GetString());
    }

    [Fact]
    public async Task ExportAsync_DoesNotPersistConnectionString()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var collector = new FakeDatabaseExportCollector(CreateSnapshot(
            modules:
            [
                new DatabaseModule(
                    1,
                    "dbo",
                    "SafeModule",
                    DatabaseModuleKind.StoredProcedure,
                    "SELECT 1;",
                    UsesQuotedIdentifier: true),
            ]));
        var service = new ExportService(collector);

        await service.ExportAsync(CreateOptions(
            temporaryDirectory.Path,
            includeModules: true,
            includeQueryCache: false), TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionString, collector.LastRequest?.ConnectionString);
        foreach (var path in Directory.EnumerateFiles(
                     temporaryDirectory.Path,
                     "*",
                     SearchOption.AllDirectories))
        {
            var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(ConnectionString, content, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-persist-this-secret", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExportAsync_OverwriteRejectsUnmarkedDirectoryBeforeCollecting()
    {
        using var temporaryDirectory = new TemporaryDirectory(createDirectory: true);
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory.Path, "user-file.txt"),
            "keep me",
            TestContext.Current.CancellationToken);
        var collector = new FakeDatabaseExportCollector(CreateSnapshot());
        var service = new ExportService(collector);
        var options = CreateOptions(
            temporaryDirectory.Path,
            includeModules: true,
            includeQueryCache: false,
            overwrite: true);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            service.ExportAsync(options, TestContext.Current.CancellationToken));

        Assert.Contains("not marked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, collector.CallCount);
        Assert.True(File.Exists(Path.Combine(temporaryDirectory.Path, "user-file.txt")));
    }

    [Fact]
    public async Task ExportAsync_OverwriteReplacesOnlyPreviouslyMarkedExport()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var collector = new FakeDatabaseExportCollector(CreateSnapshot());
        var service = new ExportService(collector);
        var initialOptions = CreateOptions(
            temporaryDirectory.Path,
            includeModules: true,
            includeQueryCache: false);
        await service.ExportAsync(initialOptions, TestContext.Current.CancellationToken);
        var staleFile = Path.Combine(temporaryDirectory.Path, "stale.txt");
        await File.WriteAllTextAsync(staleFile, "stale", TestContext.Current.CancellationToken);

        await service.ExportAsync(
            initialOptions with { Overwrite = true },
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(staleFile));
        Assert.Equal(2, collector.CallCount);
        Assert.True(File.Exists(Path.Combine(temporaryDirectory.Path, ".mssql-compat-output")));
    }

    [Fact]
    public async Task ExportAsync_CreatesOnlySelectedSourceDirectories()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var modulesOutput = Path.Combine(temporaryDirectory.Path, "modules-only");
        var cacheOutput = Path.Combine(temporaryDirectory.Path, "cache-only");
        var snapshot = CreateSnapshot(
            modules:
            [
                new DatabaseModule(
                    1,
                    "dbo",
                    "Module",
                    DatabaseModuleKind.StoredProcedure,
                    "SELECT 1;",
                    UsesQuotedIdentifier: true),
            ],
            cachedQueries:
            [
                new CachedQuery("SELECT 2;", false, 1, 1, 1, null, []),
            ]);

        var modulesCollector = new FakeDatabaseExportCollector(snapshot);
        await new ExportService(modulesCollector).ExportAsync(CreateOptions(
            modulesOutput,
            includeModules: true,
            includeQueryCache: false), TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(Path.Combine(modulesOutput, "modules")));
        Assert.True(Directory.Exists(Path.Combine(modulesOutput, "modules", "P")));
        Assert.False(Directory.Exists(Path.Combine(modulesOutput, "cache")));
        Assert.True(modulesCollector.LastRequest?.IncludeModules);
        Assert.False(modulesCollector.LastRequest?.IncludeQueryCache);

        var cacheCollector = new FakeDatabaseExportCollector(snapshot);
        await new ExportService(cacheCollector).ExportAsync(CreateOptions(
            cacheOutput,
            includeModules: false,
            includeQueryCache: true), TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(Path.Combine(cacheOutput, "modules")));
        Assert.True(Directory.Exists(Path.Combine(cacheOutput, "cache")));
        Assert.False(cacheCollector.LastRequest?.IncludeModules);
        Assert.True(cacheCollector.LastRequest?.IncludeQueryCache);
    }

    [Fact]
    public async Task ExportAsync_ForwardsCancellationToCollector()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var collector = new CancelableDatabaseExportCollector();
        var service = new ExportService(collector);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var exportTask = service.ExportAsync(
            CreateOptions(
                temporaryDirectory.Path,
                includeModules: true,
                includeQueryCache: false),
            cancellation.Token);
        await collector.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exportTask);
    }

    [Fact]
    public async Task ExportAsync_UnexpectedCollectorFailureReportsSafeExceptionType()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var service = new ExportService(new ThrowingDatabaseExportCollector());

        var result = await service.ExportAsync(
            CreateOptions(temporaryDirectory.Path, includeModules: true, includeQueryCache: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DatabaseCollectionFailed", diagnostic.Code);
        Assert.Contains("InvalidOperationException", diagnostic.Message);
        Assert.DoesNotContain("sensitive-detail", diagnostic.Message);
    }

    [Fact]
    public async Task ExportAsync_WritesModulesIntoObjectTypeDirectories()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var modules = new[]
        {
            new DatabaseModule(1, "dbo", "Procedure", DatabaseModuleKind.StoredProcedure, "SELECT 1;", true),
            new DatabaseModule(2, "dbo", "ScalarFunction", DatabaseModuleKind.SqlScalarFunction, "RETURN 1;", true),
            new DatabaseModule(3, "dbo", "InlineFunction", DatabaseModuleKind.SqlInlineTableValuedFunction, "RETURN SELECT 1 AS value;", true),
            new DatabaseModule(4, "dbo", "Trigger", DatabaseModuleKind.SqlTrigger, "SELECT 1;", false),
            new DatabaseModule(5, "dbo", "OrderView", DatabaseModuleKind.SqlView, "CREATE VIEW dbo.OrderView AS SELECT 1 AS value;", true),
        };
        var service = new ExportService(new FakeDatabaseExportCollector(CreateSnapshot(modules: modules)));

        var result = await service.ExportAsync(
            CreateOptions(temporaryDirectory.Path, includeModules: true, includeQueryCache: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(5, result.ModulesExported);
        var moduleDirectory = Path.Combine(temporaryDirectory.Path, "modules");
        foreach (var objectType in new[] { "P", "FN", "IF", "TR", "V" })
        {
            var typeDirectory = Path.Combine(moduleDirectory, objectType);
            Assert.True(Directory.Exists(typeDirectory));
            Assert.Single(Directory.GetFiles(typeDirectory, "*.sql"));
            Assert.True(File.Exists(Path.Combine(typeDirectory, "manifest.json")));
        }

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(moduleDirectory, "manifest.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            ["FN", "IF", "P", "TR", "V"],
            manifest.RootElement.GetProperty("entries")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("objectType").GetString())
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ModuleCollectionQuery_UsesSqlModulesAndSupportedObjectTypes()
    {
        var field = typeof(SqlServerExportCollector).GetField(
            "ModuleSql",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var sql = Assert.IsType<string>(field?.GetRawConstantValue());

        Assert.Contains("FROM sys.sql_modules AS sm", sql);
        Assert.Contains("INNER JOIN sys.objects AS o", sql);
        Assert.Contains("RTRIM(o.type) AS object_type", sql);
        Assert.Contains("o.type IN ('P', 'FN', 'IF', 'TR', 'V')", sql);
        Assert.DoesNotContain("sys.procedures", sql);
        Assert.DoesNotContain("sys.triggers", sql);
    }

    [Theory]
    [InlineData("P ", DatabaseModuleKind.StoredProcedure)]
    [InlineData("FN", DatabaseModuleKind.SqlScalarFunction)]
    [InlineData("IF", DatabaseModuleKind.SqlInlineTableValuedFunction)]
    [InlineData("TR", DatabaseModuleKind.SqlTrigger)]
    [InlineData("V ", DatabaseModuleKind.SqlView)]
    public void ModuleTypeMapping_AcceptsSqlObjectsTypeValues(
        string objectType,
        DatabaseModuleKind expected)
    {
        var method = typeof(SqlServerExportCollector).GetMethod(
            "ToModuleKind",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var actual = method?.Invoke(null, [objectType]);

        Assert.Equal(expected, Assert.IsType<DatabaseModuleKind>(actual));
    }

    private static ExportOptions CreateOptions(
        string outputDirectory,
        bool includeModules,
        bool includeQueryCache,
        bool overwrite = false) =>
        new(
            ConnectionString,
            "TestDb",
            outputDirectory,
            includeModules,
            includeQueryCache,
            overwrite);

    private static DatabaseExportSnapshot CreateSnapshot(
        IReadOnlyList<DatabaseModule>? modules = null,
        IReadOnlyList<CachedQuery>? cachedQueries = null,
        IReadOnlyList<ExportDiagnostic>? diagnostics = null) =>
        new(
            "16.0.1000.0",
            16,
            modules ?? [],
            cachedQueries ?? [],
            diagnostics ?? []);

    private sealed class FakeDatabaseExportCollector(DatabaseExportSnapshot snapshot)
        : IDatabaseExportCollector
    {
        public int CallCount { get; private set; }

        public DatabaseExportRequest? LastRequest { get; private set; }

        public Task<DatabaseExportSnapshot> CollectAsync(
            DatabaseExportRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class CancelableDatabaseExportCollector : IDatabaseExportCollector
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DatabaseExportSnapshot> CollectAsync(
            DatabaseExportRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-aware wait unexpectedly completed.");
        }
    }

    private sealed class ThrowingDatabaseExportCollector : IDatabaseExportCollector
    {
        public Task<DatabaseExportSnapshot> CollectAsync(
            DatabaseExportRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("sensitive-detail");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(bool createDirectory = false)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mssql-compat-export-tests-{Guid.NewGuid():N}");
            if (createDirectory)
            {
                Directory.CreateDirectory(Path);
            }
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
