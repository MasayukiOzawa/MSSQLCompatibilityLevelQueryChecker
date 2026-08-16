namespace MssqlCompatCheck.Export;

/// <summary>Options for exporting SQL text from a SQL Server database.</summary>
public sealed record ExportOptions(
    string ConnectionString,
    string Database,
    string OutputDirectory,
    bool IncludeModules,
    bool IncludeQueryCache,
    bool Overwrite = false);

/// <summary>The request supplied to a database export collector.</summary>
public sealed record DatabaseExportRequest(
    string ConnectionString,
    string Database,
    bool IncludeModules,
    bool IncludeQueryCache);

/// <summary>Abstraction over database access used by <see cref="ExportService"/>.</summary>
public interface IDatabaseExportCollector
{
    Task<DatabaseExportSnapshot> CollectAsync(
        DatabaseExportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A database snapshot ready to be written to an offline export.</summary>
public sealed record DatabaseExportSnapshot(
    string ServerVersion,
    int ServerMajorVersion,
    IReadOnlyList<DatabaseModule> Modules,
    IReadOnlyList<CachedQuery> CachedQueries,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

/// <summary>The supported module categories.</summary>
public enum DatabaseModuleKind
{
    StoredProcedure,
    SqlScalarFunction,
    SqlInlineTableValuedFunction,
    SqlTrigger,
}

/// <summary>A T-SQL module returned by a database collector.</summary>
public sealed record DatabaseModule(
    int ObjectId,
    string? SchemaName,
    string ObjectName,
    DatabaseModuleKind Kind,
    string? Definition,
    bool UsesQuotedIdentifier);

/// <summary>An aggregated query-cache entry returned by a database collector.</summary>
public sealed record CachedQuery(
    string Text,
    bool UsesQuotedIdentifier,
    long OccurrenceCount,
    long ExecutionCount,
    long TotalWorkerTime,
    DateTime? LastExecutionTime,
    IReadOnlyList<string> QueryHashes);

/// <summary>The severity of an export diagnostic.</summary>
public enum ExportDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>A safe-to-persist diagnostic that never contains a connection string or SQL text.</summary>
public sealed record ExportDiagnostic(
    ExportDiagnosticSeverity Severity,
    string Source,
    string Code,
    string Message);

/// <summary>The outcome of an export operation.</summary>
public sealed record ExportResult(
    string OutputDirectory,
    int ModulesExported,
    int CacheQueriesExported,
    int SkippedCount,
    IReadOnlyList<ExportDiagnostic> Diagnostics)
{
    public int ErrorCount => Diagnostics.Count(static diagnostic =>
        diagnostic.Severity == ExportDiagnosticSeverity.Error);

    public int ExitCode => ErrorCount > 0
        ? 2
        : SkippedCount > 0 || Diagnostics.Any(static diagnostic =>
            diagnostic.Severity == ExportDiagnosticSeverity.Warning)
            ? 1
            : 0;
}
