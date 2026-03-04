using Xunit;

namespace GiantIsopod.Plugin.Process.Tests;

public class CliExecutableResolverTests
{
    [Fact]
    public void ResolveFromRepoLocalCandidates_ReturnsNull_WhenOutsideGitCheckoutAndNoRepoLocalSidecarExists()
    {
        // Arrange
        var candidates = new[] { "nonexistent", "path/to/executable" };

        // Act
        var result = CliExecutableResolver.ResolveFromRepoLocalCandidates(candidates);

        // Assert
        Assert.Null(result);
    }
}
