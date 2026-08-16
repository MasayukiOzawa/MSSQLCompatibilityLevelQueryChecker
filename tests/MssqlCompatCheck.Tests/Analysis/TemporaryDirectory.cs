namespace MssqlCompatCheck.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private static readonly string TestRoot = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "mssql-compat-check-tests");

    public TemporaryDirectory()
    {
        Directory.CreateDirectory(TestRoot);
        Path = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(params string[] segments)
    {
        var path = segments.Aggregate(Path, System.IO.Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<string> WriteSqlAsync(string relativePath, string sql)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, sql);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        var relative = System.IO.Path.GetRelativePath(TestRoot, Path);
        if (System.IO.Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a path outside the test root.");
        }

        Directory.Delete(Path, recursive: true);
    }
}
