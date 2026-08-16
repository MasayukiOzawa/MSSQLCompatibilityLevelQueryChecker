using System.Globalization;
using System.Text.Json;

namespace MssqlCompatCheck.Analysis;

internal sealed class ManifestMetadataProvider
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly Dictionary<string, SqlSourceMetadata> metadataByPath = new(PathComparer);
    private readonly HashSet<string> loadedManifests = new(PathComparer);
    private readonly List<AnalysisDiagnostic> diagnostics = [];

    public IReadOnlyList<AnalysisDiagnostic> Diagnostics => diagnostics;

    public async Task<SqlSourceMetadata?> GetMetadataAsync(
        string sqlFilePath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(sqlFilePath);
        if (directory is null)
        {
            return null;
        }

        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.Exists(manifestPath) && loadedManifests.Add(NormalizePath(manifestPath)))
        {
            await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        }

        metadataByPath.TryGetValue(NormalizePath(sqlFilePath), out var metadata);
        return metadata;
    }

    private async Task LoadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var entries = FindEntries(document.RootElement);
            if (entries is null)
            {
                throw new JsonException("manifest.json に entries、items、files のいずれかの配列がありません。");
            }

            var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
            foreach (var entry in entries.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("マニフェスト項目は JSON オブジェクトである必要があります。");
                }

                var relativePath = GetString(entry, "relativePath", "filePath", "path", "file");
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    var status = GetString(entry, "status");
                    if (string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    throw new JsonException("エクスポート済みのマニフェスト項目に SQL ファイルの相対パスがありません。");
                }

                var fullPath = NormalizePath(Path.Combine(manifestDirectory, relativePath));
                if (!IsWithinDirectory(fullPath, manifestDirectory))
                {
                    throw new JsonException($"マニフェスト項目がディレクトリ外を参照しています: {relativePath}");
                }

                var metadataElement = GetObject(entry, "metadata") ?? entry;
                var metadata = new SqlSourceMetadata(
                    GetString(metadataElement, "sourceType", "type", "kind"),
                    GetString(metadataElement, "objectName", "name"),
                    GetString(metadataElement, "queryHash"),
                    GetBoolean(metadataElement, "quotedIdentifier", "usesQuotedIdentifier"),
                    GetInt64(metadataElement, "occurrenceCount"),
                    GetInt64(metadataElement, "executionCount"),
                    GetInt64(metadataElement, "totalWorkerTime", "totalCpuTime"),
                    GetDateTimeOffset(metadataElement, "lastExecutionTime"),
                    GetInt32(metadataElement, "objectId"),
                    GetString(metadataElement, "schemaName"),
                    GetStringArray(metadataElement, "queryHashes"));

                if (!metadataByPath.TryAdd(fullPath, metadata))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Warning,
                        "ANALYSIS_MANIFEST_DUPLICATE_ENTRY",
                        "同じ SQL ファイルのマニフェスト項目が重複しているため、最初の項目を使用します。",
                        fullPath));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "ANALYSIS_MANIFEST_INVALID",
                exception.Message,
                manifestPath));
        }
    }

    private static JsonElement? FindEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "entries", "items", "files", "queries" })
        {
            if (TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }

    private static JsonElement? GetObject(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool? GetBoolean(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static long? GetInt64(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static int? GetInt32(JsonElement element, params string[] propertyNames)
    {
        var value = GetInt64(element, propertyNames);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static IReadOnlyList<string>? GetStringArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .OfType<string>()
                .ToArray();
        }

        return null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, params string[] propertyNames)
    {
        var text = GetString(element, propertyNames);
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var relativePath = Path.GetRelativePath(normalizedDirectory, path);
        return !Path.IsPathRooted(relativePath) &&
               relativePath != ".." &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
