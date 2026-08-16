using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MssqlCompatCheck.Export;

/// <summary>Exports database SQL to an offline, manifest-backed directory.</summary>
public sealed class ExportService
{
    private const string MarkerFileName = ".mssql-compat-output";
    private const string MarkerContent = "mssql-compat-check-export-v1";
    private const string ManifestSchemaVersion = "1.0";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IDatabaseExportCollector _collector;

    public ExportService()
        : this(new SqlServerExportCollector())
    {
    }

    public ExportService(IDatabaseExportCollector collector)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    public async Task<ExportResult> ExportAsync(
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        PrepareOutputDirectory(outputDirectory, options.Overwrite);

        await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, MarkerFileName),
                MarkerContent,
                Utf8WithoutBom,
                cancellationToken)
            .ConfigureAwait(false);

        DatabaseExportSnapshot snapshot;
        try
        {
            snapshot = await _collector.CollectAsync(
                    new DatabaseExportRequest(
                        options.ConnectionString,
                        options.Database,
                        options.IncludeModules,
                        options.IncludeQueryCache),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatabaseCollectionException exception)
        {
            var diagnostic = new ExportDiagnostic(
                ExportDiagnosticSeverity.Error,
                "database",
                "DatabaseCollectionFailed",
                exception.Message);
            await WriteFailureManifestAsync(outputDirectory, options, diagnostic, cancellationToken)
                .ConfigureAwait(false);
            return new ExportResult(outputDirectory, 0, 0, 0, [diagnostic]);
        }
        catch (Exception exception)
        {
            var diagnostic = new ExportDiagnostic(
                ExportDiagnosticSeverity.Error,
                "database",
                "DatabaseCollectionFailed",
                $"Database collection failed before it could be completed ({exception.GetType().Name}).");
            await WriteFailureManifestAsync(outputDirectory, options, diagnostic, cancellationToken)
                .ConfigureAwait(false);
            return new ExportResult(outputDirectory, 0, 0, 0, [diagnostic]);
        }

        var diagnostics = new List<ExportDiagnostic>(snapshot.Diagnostics);
        if (snapshot.ServerMajorVersion < 11)
        {
            var diagnostic = new ExportDiagnostic(
                ExportDiagnosticSeverity.Error,
                "database",
                "UnsupportedServerVersion",
                $"SQL Server {snapshot.ServerVersion} is not supported. SQL Server 2012 or later is required.");
            diagnostics.Add(diagnostic);
            await WriteFailureManifestAsync(outputDirectory, options, diagnostic, cancellationToken)
                .ConfigureAwait(false);
            return new ExportResult(outputDirectory, 0, 0, 0, diagnostics);
        }

        var exportedAtUtc = DateTimeOffset.UtcNow;
        var modulesExported = 0;
        var cacheQueriesExported = 0;
        var skippedCount = diagnostics.Count(static diagnostic =>
            diagnostic.Severity == ExportDiagnosticSeverity.Warning);

        if (options.IncludeModules)
        {
            var outcome = await WriteModulesAsync(
                    outputDirectory,
                    snapshot.Modules,
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
            modulesExported = outcome.ExportedCount;
            skippedCount += outcome.SkippedCount;
        }

        if (options.IncludeQueryCache)
        {
            cacheQueriesExported = await WriteCacheAsync(
                    outputDirectory,
                    snapshot.CachedQueries,
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var rootManifest = new RootManifest(
            ManifestSchemaVersion,
            GetToolVersion(),
            options.Database,
            snapshot.ServerVersion,
            snapshot.ServerMajorVersion,
            exportedAtUtc,
            GetSelectedSources(options),
            new ExportCounts(modulesExported, cacheQueriesExported, skippedCount),
            diagnostics);

        await WriteJsonAsync(
                Path.Combine(outputDirectory, "export-manifest.json"),
                rootManifest,
                cancellationToken)
            .ConfigureAwait(false);

        return new ExportResult(
            outputDirectory,
            modulesExported,
            cacheQueriesExported,
            skippedCount,
            diagnostics);
    }

    private static void ValidateOptions(ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Database))
        {
            throw new ArgumentException("A database name is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new ArgumentException("An output directory is required.", nameof(options));
        }

        if (!options.IncludeModules && !options.IncludeQueryCache)
        {
            throw new ArgumentException(
                "At least one of module or query-cache export must be selected.",
                nameof(options));
        }

    }

    private static void PrepareOutputDirectory(string outputDirectory, bool overwrite)
    {
        EnsureNoReparsePointInExistingAncestors(outputDirectory);

        if (File.Exists(outputDirectory))
        {
            throw new IOException("The export output path is an existing file.");
        }

        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            return;
        }

        if (!overwrite)
        {
            throw new IOException("The export output directory already exists. Use overwrite explicitly to replace it.");
        }

        var markerPath = Path.Combine(outputDirectory, MarkerFileName);
        if (!File.Exists(markerPath) ||
            !string.Equals(File.ReadAllText(markerPath), MarkerContent, StringComparison.Ordinal))
        {
            throw new IOException("The existing output is not marked as an mssql-compat-check export and will not be overwritten.");
        }

        EnsureTreeContainsNoReparsePoints(outputDirectory);
        Directory.Delete(outputDirectory, recursive: true);
        Directory.CreateDirectory(outputDirectory);
    }

    private static void EnsureNoReparsePointInExistingAncestors(string path)
    {
        DirectoryInfo? directory = new(path);
        while (directory is not null)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"The export path traverses a reparse point: {directory.FullName}");
            }

            directory = directory.Parent;
        }
    }

    private static void EnsureTreeContainsNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"The existing export contains a reparse point and will not be overwritten: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static async Task<ModuleWriteOutcome> WriteModulesAsync(
        string outputDirectory,
        IReadOnlyList<DatabaseModule> modules,
        List<ExportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var moduleDirectory = Path.Combine(outputDirectory, "modules");
        Directory.CreateDirectory(moduleDirectory);
        var entries = new List<ModuleManifestEntry>(modules.Count);
        var objectTypes = new HashSet<string>(StringComparer.Ordinal);
        var exportedCount = 0;
        var skippedCount = 0;

        foreach (var module in modules.OrderBy(static module => module.ObjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceType = ToSourceType(module.Kind);
            var objectType = ToObjectType(module.Kind);
            objectTypes.Add(objectType);
            var objectTypeDirectory = Path.Combine(moduleDirectory, objectType);
            Directory.CreateDirectory(objectTypeDirectory);

            if (module.Definition is null)
            {
                skippedCount++;
                var diagnostic = new ExportDiagnostic(
                    ExportDiagnosticSeverity.Warning,
                    "modules",
                    "EncryptedOrUnavailableDefinition",
                    $"Module object ID {module.ObjectId} was skipped because its definition is encrypted or unavailable.");
                diagnostics.Add(diagnostic);
                entries.Add(new ModuleManifestEntry(
                    null,
                    sourceType,
                    objectType,
                    module.ObjectId,
                    module.SchemaName,
                    module.ObjectName,
                    module.UsesQuotedIdentifier,
                    null,
                    "skipped",
                    diagnostic.Code));
                continue;
            }

            var fileName = BuildModuleFileName(module);
            var relativePath = Path.Combine(objectType, fileName).Replace(Path.DirectorySeparatorChar, '/');
            var fullPath = Path.Combine(objectTypeDirectory, fileName);
            var hash = ComputeSha256(module.Definition);

            try
            {
                await File.WriteAllTextAsync(fullPath, module.Definition, Utf8WithoutBom, cancellationToken)
                    .ConfigureAwait(false);
                exportedCount++;
                entries.Add(new ModuleManifestEntry(
                    relativePath,
                    sourceType,
                    objectType,
                    module.ObjectId,
                    module.SchemaName,
                    module.ObjectName,
                    module.UsesQuotedIdentifier,
                    hash,
                    "exported",
                    null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                diagnostics.Add(new ExportDiagnostic(
                    ExportDiagnosticSeverity.Error,
                    "modules",
                    "ModuleWriteFailed",
                    $"Module object ID {module.ObjectId} could not be written."));
                entries.Add(new ModuleManifestEntry(
                    relativePath,
                    sourceType,
                    objectType,
                    module.ObjectId,
                    module.SchemaName,
                    module.ObjectName,
                    module.UsesQuotedIdentifier,
                    hash,
                    "failed",
                    "ModuleWriteFailed"));
            }
        }

        foreach (var objectType in objectTypes.Order(StringComparer.Ordinal))
        {
            var typeEntries = entries
                .Where(entry => string.Equals(entry.ObjectType, objectType, StringComparison.Ordinal))
                .Select(entry => entry with
                {
                    RelativePath = entry.RelativePath is null ? null : Path.GetFileName(entry.RelativePath),
                })
                .ToArray();
            await WriteJsonAsync(
                    Path.Combine(moduleDirectory, objectType, "manifest.json"),
                    new SourceManifest<ModuleManifestEntry>(
                        ManifestSchemaVersion,
                        $"modules/{objectType}",
                        typeEntries),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteJsonAsync(
                Path.Combine(moduleDirectory, "manifest.json"),
                new SourceManifest<ModuleManifestEntry>(ManifestSchemaVersion, "modules", entries),
                cancellationToken)
            .ConfigureAwait(false);

        return new ModuleWriteOutcome(exportedCount, skippedCount);
    }

    private static async Task<int> WriteCacheAsync(
        string outputDirectory,
        IReadOnlyList<CachedQuery> queries,
        List<ExportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(outputDirectory, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var entries = new List<CacheManifestEntry>(queries.Count);
        var exportedCount = 0;
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries
                     .OrderBy(static query => query.Text, StringComparer.Ordinal)
                     .ThenBy(static query => query.UsesQuotedIdentifier))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = ComputeSha256(query.Text);
            var fileName = $"{hash}.sql";
            if (!usedFileNames.Add(fileName))
            {
                fileName = $"{hash}-qi{(query.UsesQuotedIdentifier ? 1 : 0)}.sql";
                usedFileNames.Add(fileName);
            }

            var relativePath = fileName.Replace(Path.DirectorySeparatorChar, '/');
            try
            {
                await File.WriteAllTextAsync(
                        Path.Combine(cacheDirectory, fileName),
                        query.Text,
                        Utf8WithoutBom,
                        cancellationToken)
                    .ConfigureAwait(false);
                exportedCount++;
                entries.Add(new CacheManifestEntry(
                    relativePath,
                    "queryCache",
                    query.UsesQuotedIdentifier,
                    hash,
                    query.OccurrenceCount,
                    query.ExecutionCount,
                    query.TotalWorkerTime,
                    query.LastExecutionTime,
                    query.QueryHashes,
                    "exported"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                diagnostics.Add(new ExportDiagnostic(
                    ExportDiagnosticSeverity.Error,
                    "cache",
                    "CacheWriteFailed",
                    $"A query-cache entry with SHA-256 {hash} could not be written."));
                entries.Add(new CacheManifestEntry(
                    relativePath,
                    "queryCache",
                    query.UsesQuotedIdentifier,
                    hash,
                    query.OccurrenceCount,
                    query.ExecutionCount,
                    query.TotalWorkerTime,
                    query.LastExecutionTime,
                    query.QueryHashes,
                    "failed"));
            }
        }

        await WriteJsonAsync(
                Path.Combine(cacheDirectory, "manifest.json"),
                new SourceManifest<CacheManifestEntry>(ManifestSchemaVersion, "cache", entries),
                cancellationToken)
            .ConfigureAwait(false);

        return exportedCount;
    }

    private static async Task WriteFailureManifestAsync(
        string outputDirectory,
        ExportOptions options,
        ExportDiagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var manifest = new RootManifest(
            ManifestSchemaVersion,
            GetToolVersion(),
            options.Database,
            null,
            null,
            DateTimeOffset.UtcNow,
            GetSelectedSources(options),
            new ExportCounts(0, 0, 0),
            [diagnostic]);

        await WriteJsonAsync(
                Path.Combine(outputDirectory, "export-manifest.json"),
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static string BuildModuleFileName(DatabaseModule module)
    {
        var prefix = module.Kind switch
        {
            DatabaseModuleKind.StoredProcedure => "procedure",
            DatabaseModuleKind.SqlScalarFunction => "scalar-function",
            DatabaseModuleKind.SqlInlineTableValuedFunction => "inline-table-function",
            DatabaseModuleKind.SqlTrigger => "trigger",
            DatabaseModuleKind.SqlView => "view",
            _ => "module",
        };
        var schema = SanitizeFileNamePart(module.SchemaName ?? "database");
        var name = SanitizeFileNamePart(module.ObjectName);
        return $"{prefix}-{schema}-{name}-{module.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.sql";
    }

    private static string SanitizeFileNamePart(string value)
    {
        const int maximumLength = 60;
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value)
        {
            if (builder.Length >= maximumLength)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        var sanitized = builder.ToString().TrimEnd('.', ' ');
        return sanitized.Length == 0 ? "unnamed" : sanitized;
    }

    private static string ComputeSha256(string text)
    {
        var bytes = Utf8WithoutBom.GetBytes(text);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string ToSourceType(DatabaseModuleKind kind) => kind switch
    {
        DatabaseModuleKind.StoredProcedure => "storedProcedure",
        DatabaseModuleKind.SqlScalarFunction => "sqlScalarFunction",
        DatabaseModuleKind.SqlInlineTableValuedFunction => "sqlInlineTableValuedFunction",
        DatabaseModuleKind.SqlTrigger => "sqlTrigger",
        DatabaseModuleKind.SqlView => "sqlView",
        _ => "module",
    };

    private static string ToObjectType(DatabaseModuleKind kind) => kind switch
    {
        DatabaseModuleKind.StoredProcedure => "P",
        DatabaseModuleKind.SqlScalarFunction => "FN",
        DatabaseModuleKind.SqlInlineTableValuedFunction => "IF",
        DatabaseModuleKind.SqlTrigger => "TR",
        DatabaseModuleKind.SqlView => "V",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported SQL module kind."),
    };

    private static string[] GetSelectedSources(ExportOptions options) =>
        (options.IncludeModules, options.IncludeQueryCache) switch
        {
            (true, true) => ["modules", "cache"],
            (true, false) => ["modules"],
            (false, true) => ["cache"],
            _ => [],
        };

    private static string GetToolVersion() =>
        typeof(ExportService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;

    private sealed record RootManifest(
        string SchemaVersion,
        string ToolVersion,
        string Database,
        string? ServerVersion,
        int? ServerMajorVersion,
        DateTimeOffset ExportedAtUtc,
        IReadOnlyList<string> SelectedSources,
        ExportCounts Counts,
        IReadOnlyList<ExportDiagnostic> Diagnostics);

    private sealed record ExportCounts(int Modules, int CacheQueries, int Skipped);

    private sealed record SourceManifest<T>(
        string SchemaVersion,
        string Source,
        IReadOnlyList<T> Entries);

    private sealed record ModuleManifestEntry(
        string? RelativePath,
        string SourceType,
        string ObjectType,
        int ObjectId,
        string? SchemaName,
        string ObjectName,
        bool UsesQuotedIdentifier,
        string? Sha256,
        string Status,
        string? DiagnosticCode);

    private sealed record CacheManifestEntry(
        string RelativePath,
        string SourceType,
        bool UsesQuotedIdentifier,
        string Sha256,
        long OccurrenceCount,
        long ExecutionCount,
        long TotalWorkerTime,
        DateTime? LastExecutionTime,
        IReadOnlyList<string> QueryHashes,
        string Status);

    private readonly record struct ModuleWriteOutcome(int ExportedCount, int SkippedCount);
}
