using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MssqlCompatCheck.Export;

/// <summary>Collects modules and cached statements from SQL Server 2012 or later.</summary>
public sealed class SqlServerExportCollector : IDatabaseExportCollector
{
    private const int MinimumSupportedMajorVersion = 11;

    private const string ModuleSql = """
        SELECT
            o.object_id,
            s.name AS schema_name,
            o.name AS object_name,
            RTRIM(o.type) AS object_type,
            sm.definition,
            sm.uses_quoted_identifier
        FROM sys.sql_modules AS sm
        INNER JOIN sys.objects AS o ON o.object_id = sm.object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('P', 'FN', 'IF', 'TR', 'V')
        ORDER BY o.object_id;
        """;

    private const string QueryCacheSql = """
        SELECT
            txt.text AS batch_text,
            qs.statement_start_offset,
            qs.statement_end_offset,
            qs.query_hash,
            qs.execution_count,
            qs.total_worker_time,
            qs.last_execution_time,
            CAST(CASE WHEN (plan_options.set_options & 64) = 64 THEN 1 ELSE 0 END AS bit) AS uses_quoted_identifier
        FROM sys.dm_exec_query_stats AS qs
        CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS txt
        OUTER APPLY
        (
            SELECT TOP (1) CONVERT(int, attribute.value) AS plan_database_id
            FROM sys.dm_exec_plan_attributes(qs.plan_handle) AS attribute
            WHERE attribute.attribute = 'dbid'
        ) AS plan_database
        OUTER APPLY
        (
            SELECT TOP (1) CONVERT(bigint, attribute.value) AS set_options
            FROM sys.dm_exec_plan_attributes(qs.plan_handle) AS attribute
            WHERE attribute.attribute = 'set_options'
        ) AS plan_options
        WHERE txt.dbid = DB_ID()
           OR plan_database.plan_database_id = DB_ID();
        """;

    public async Task<DatabaseExportSnapshot> CollectAsync(
        DatabaseExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Database))
        {
            throw new ArgumentException("A database name is required.", nameof(request));
        }

        if (!request.IncludeModules && !request.IncludeQueryCache)
        {
            throw new ArgumentException("At least one export source must be selected.", nameof(request));
        }

        var diagnostics = new List<ExportDiagnostic>();
        await using var connection = new SqlConnection(request.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException exception)
        {
            throw new DatabaseCollectionException(
                $"SQL Server connection failed (error {exception.Number}).",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new DatabaseCollectionException("SQL Server connection configuration is invalid.", exception);
        }

        var serverVersion = connection.ServerVersion;
        if (!TryGetServerMajorVersion(serverVersion, out var serverMajorVersion))
        {
            throw new DatabaseCollectionException("The SQL Server version could not be determined.");
        }

        if (serverMajorVersion < MinimumSupportedMajorVersion)
        {
            throw new DatabaseCollectionException(
                $"SQL Server {serverVersion} is not supported. SQL Server 2012 or later is required.");
        }

        try
        {
            connection.ChangeDatabase(request.Database);
        }
        catch (SqlException exception)
        {
            throw new DatabaseCollectionException(
                $"The requested database could not be accessed (SQL Server error {exception.Number}).",
                exception);
        }

        IReadOnlyList<DatabaseModule> modules = Array.Empty<DatabaseModule>();
        IReadOnlyList<CachedQuery> cachedQueries = Array.Empty<CachedQuery>();

        if (request.IncludeModules)
        {
            modules = await CollectModulesAsync(connection, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        if (request.IncludeQueryCache)
        {
            cachedQueries = await CollectQueryCacheAsync(
                    connection,
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new DatabaseExportSnapshot(
            serverVersion,
            serverMajorVersion,
            modules,
            cachedQueries,
            diagnostics);
    }

    private static async Task<IReadOnlyList<DatabaseModule>> CollectModulesAsync(
        SqlConnection connection,
        List<ExportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var modules = new List<DatabaseModule>();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ModuleSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                modules.Add(new DatabaseModule(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ToModuleKind(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    !reader.IsDBNull(5) && reader.GetBoolean(5)));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException exception)
        {
            diagnostics.Add(new ExportDiagnostic(
                ExportDiagnosticSeverity.Error,
                "modules",
                "ModuleCollectionFailed",
                $"Module collection failed (SQL Server error {exception.Number}). Verify metadata permissions."));
        }

        return modules;
    }

    private static DatabaseModuleKind ToModuleKind(string objectType) => objectType.Trim() switch
    {
        "P" => DatabaseModuleKind.StoredProcedure,
        "FN" => DatabaseModuleKind.SqlScalarFunction,
        "IF" => DatabaseModuleKind.SqlInlineTableValuedFunction,
        "TR" => DatabaseModuleKind.SqlTrigger,
        "V" => DatabaseModuleKind.SqlView,
        _ => throw new InvalidOperationException($"Unsupported SQL module object type: {objectType}"),
    };

    private static async Task<IReadOnlyList<CachedQuery>> CollectQueryCacheAsync(
        SqlConnection connection,
        List<ExportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var queries = new Dictionary<CacheKey, CacheAccumulator>();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = QueryCacheSql;
            await using var reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess,
                    cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0))
                {
                    diagnostics.Add(new ExportDiagnostic(
                        ExportDiagnosticSeverity.Warning,
                        "cache",
                        "MissingBatchText",
                        "A cache entry was skipped because its SQL text was unavailable."));
                    continue;
                }

                var batchText = reader.GetString(0);
                var startOffset = reader.GetInt32(1);
                var endOffset = reader.GetInt32(2);
                var queryHash = reader.IsDBNull(3)
                    ? null
                    : Convert.ToHexString((byte[])reader.GetValue(3));
                var executionCount = reader.GetInt64(4);
                var totalWorkerTime = reader.GetInt64(5);
                var lastExecutionTime = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);
                var usesQuotedIdentifier = !reader.IsDBNull(7) && reader.GetBoolean(7);

                if (!TryExtractStatement(batchText, startOffset, endOffset, out var statement))
                {
                    diagnostics.Add(new ExportDiagnostic(
                        ExportDiagnosticSeverity.Warning,
                        "cache",
                        "InvalidStatementOffsets",
                        "A cache entry was skipped because its statement offsets were invalid."));
                    continue;
                }

                var key = new CacheKey(statement, usesQuotedIdentifier);
                if (!queries.TryGetValue(key, out var accumulator))
                {
                    accumulator = new CacheAccumulator(statement, usesQuotedIdentifier);
                    queries.Add(key, accumulator);
                }

                accumulator.OccurrenceCount = SaturatingAdd(accumulator.OccurrenceCount, 1);
                accumulator.ExecutionCount = SaturatingAdd(accumulator.ExecutionCount, executionCount);
                accumulator.TotalWorkerTime = SaturatingAdd(accumulator.TotalWorkerTime, totalWorkerTime);

                if (lastExecutionTime is not null)
                {
                    if (accumulator.LastExecutionTime is null || lastExecutionTime > accumulator.LastExecutionTime)
                    {
                        accumulator.LastExecutionTime = lastExecutionTime;
                    }
                }

                if (queryHash is not null)
                {
                    accumulator.QueryHashes.Add(queryHash);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException exception)
        {
            diagnostics.Add(new ExportDiagnostic(
                ExportDiagnosticSeverity.Error,
                "cache",
                "QueryCacheCollectionFailed",
                $"Query-cache collection failed (SQL Server error {exception.Number}). Verify VIEW SERVER STATE or VIEW SERVER PERFORMANCE STATE permission."));
        }

        return queries.Values
            .OrderByDescending(static query => query.TotalWorkerTime)
            .ThenBy(static query => query.Text, StringComparer.Ordinal)
            .ThenBy(static query => query.UsesQuotedIdentifier)
            .Select(static query => new CachedQuery(
                query.Text,
                query.UsesQuotedIdentifier,
                query.OccurrenceCount,
                query.ExecutionCount,
                query.TotalWorkerTime,
                query.LastExecutionTime,
                query.QueryHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    private static bool TryGetServerMajorVersion(string serverVersion, out int majorVersion)
    {
        var separator = serverVersion.IndexOf('.', StringComparison.Ordinal);
        var major = separator < 0 ? serverVersion : serverVersion[..separator];
        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out majorVersion);
    }

    private static bool TryExtractStatement(
        string batchText,
        int statementStartOffset,
        int statementEndOffset,
        out string statement)
    {
        statement = string.Empty;
        if (statementStartOffset < 0 || (statementStartOffset & 1) != 0)
        {
            return false;
        }

        var start = statementStartOffset / 2;
        if (start > batchText.Length)
        {
            return false;
        }

        if (statementEndOffset == -1)
        {
            statement = batchText[start..];
            return true;
        }

        if (statementEndOffset < statementStartOffset || (statementEndOffset & 1) != 0)
        {
            return false;
        }

        var requestedLength = ((statementEndOffset - statementStartOffset) / 2) + 1;
        var length = Math.Min(requestedLength, batchText.Length - start);
        statement = batchText.Substring(start, length);
        return true;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    private readonly record struct CacheKey(string Text, bool UsesQuotedIdentifier);

    private sealed class CacheAccumulator(string text, bool usesQuotedIdentifier)
    {
        public string Text { get; } = text;

        public bool UsesQuotedIdentifier { get; } = usesQuotedIdentifier;

        public long OccurrenceCount { get; set; }

        public long ExecutionCount { get; set; }

        public long TotalWorkerTime { get; set; }

        public DateTime? LastExecutionTime { get; set; }

        public HashSet<string> QueryHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class DatabaseCollectionException : Exception
{
    public DatabaseCollectionException(string safeMessage)
        : base(safeMessage)
    {
    }

    public DatabaseCollectionException(string safeMessage, Exception innerException)
        : base(safeMessage, innerException)
    {
    }
}
