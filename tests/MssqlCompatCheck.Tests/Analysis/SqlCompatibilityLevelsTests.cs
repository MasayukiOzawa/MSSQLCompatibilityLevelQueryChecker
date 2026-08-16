using MssqlCompatCheck.Analysis;
using Xunit;

namespace MssqlCompatCheck.Tests.Analysis;

public sealed class SqlCompatibilityLevelsTests
{
    [Fact]
    public void GetSupportedLevels_ReturnsRegularSqlServerLevelsThrough180()
    {
        var expected = new[] { 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180 };

        var actual = SqlCompatibilityLevels.GetSupportedLevels();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ScriptDomVersion_ReturnsCentrallyManagedPackageVersion()
    {
        Assert.Equal("180.59.2", SqlCompatibilityLevels.ScriptDomVersion);
    }
}
