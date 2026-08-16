using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MssqlCompatCheck.Analysis;

public static class SqlCompatibilityLevels
{
    private static readonly IReadOnlyDictionary<int, SqlVersion> Versions = DiscoverVersions();

    public static string ScriptDomVersion { get; } = typeof(SqlCompatibilityLevels).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "ScriptDomPackageVersion")
        .Value!;

    public static IReadOnlyList<int> GetSupportedLevels() => Versions.Keys.Order().ToArray();

    public static bool IsSupported(int level) => Versions.ContainsKey(level);

    internal static TSqlParser CreateParser(int level, bool quotedIdentifier)
    {
        if (!Versions.TryGetValue(level, out var version))
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                $"サポートされていない互換性レベルです。指定可能な値: {string.Join(", ", GetSupportedLevels())}");
        }

        return TSqlParser.CreateParser(version, quotedIdentifier);
    }

    private static IReadOnlyDictionary<int, SqlVersion> DiscoverVersions()
    {
        var versions = new SortedDictionary<int, SqlVersion>();
        foreach (var version in Enum.GetValues<SqlVersion>())
        {
            var name = Enum.GetName(version);
            if (name is null || !name.StartsWith("Sql", StringComparison.Ordinal) ||
                !int.TryParse(name.AsSpan(3), out var level) || level is < 80 or > 180)
            {
                continue;
            }

            try
            {
                _ = TSqlParser.CreateParser(version, initialQuotedIdentifiers: true);
                versions[level] = version;
            }
            catch (ArgumentException)
            {
                // Enum members that are not regular SQL Server parsers (for example
                // specialized warehouse dialects) are intentionally omitted.
            }
        }

        return versions;
    }
}
