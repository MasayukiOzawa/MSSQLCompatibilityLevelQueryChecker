namespace MssqlCompatCheck.Analysis;

internal sealed record SqlFileDiscoveryResult(
    IReadOnlyList<DiscoveredSqlFile> Files,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics);

internal sealed record DiscoveredSqlFile(string FullPath, string RootPath, string RelativePath);

internal static class SqlFileDiscovery
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static SqlFileDiscoveryResult Discover(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<string, DiscoveredSqlFile>(PathComparer);
        var diagnostics = new List<AnalysisDiagnostic>();
        var visitedDirectories = new HashSet<string>(PathComparer);

        foreach (var suppliedRoot in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(suppliedRoot))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "ANALYSIS_DIRECTORY_EMPTY",
                    "SQL ディレクトリに空の値は指定できません。"));
                continue;
            }

            string root;
            try
            {
                root = NormalizePath(suppliedRoot);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "ANALYSIS_DIRECTORY_INVALID",
                    exception.Message,
                    suppliedRoot));
                continue;
            }

            if (!Directory.Exists(root))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "ANALYSIS_DIRECTORY_NOT_FOUND",
                    "指定された SQL ディレクトリが存在しません。",
                    root));
                continue;
            }

            if (IsReparsePoint(root, diagnostics))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Warning,
                    "ANALYSIS_REPARSE_POINT_SKIPPED",
                    "リパースポイントのディレクトリは探索しません。",
                    root));
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedDirectory = NormalizePath(directory);
                if (!visitedDirectories.Add(normalizedDirectory))
                {
                    continue;
                }

                IEnumerable<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
                }
                catch (Exception exception) when (IsFileSystemException(exception))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "ANALYSIS_DIRECTORY_READ_FAILED",
                        exception.Message,
                        directory));
                    continue;
                }

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception exception) when (IsFileSystemException(exception))
                    {
                        diagnostics.Add(new(
                            DiagnosticSeverity.Error,
                            "ANALYSIS_ENTRY_INSPECTION_FAILED",
                            exception.Message,
                            entry));
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(new(
                            DiagnosticSeverity.Warning,
                            "ANALYSIS_REPARSE_POINT_SKIPPED",
                            "リパースポイントは探索または解析しません。",
                            entry));
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (!string.Equals(Path.GetExtension(entry), ".sql", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fullPath = NormalizePath(entry);
                    files.TryAdd(fullPath, new(fullPath, root, Path.GetRelativePath(root, fullPath)));
                }
            }
        }

        return new(
            files.Values.OrderBy(file => file.FullPath, PathComparer).ToArray(),
            diagnostics);
    }

    private static bool IsReparsePoint(string path, ICollection<AnalysisDiagnostic> diagnostics)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "ANALYSIS_DIRECTORY_INSPECTION_FAILED",
                exception.Message,
                path));
            return true;
        }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
