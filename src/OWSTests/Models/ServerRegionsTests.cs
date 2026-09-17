using OWSData.Models;
using Xunit;

namespace OWSTests.Models
{
    /// <summary>
    /// Normalize decides where a player lands when no region was chosen, and where a launcher
    /// registers when its ServerRegion is unset — both must resolve to the default rather than to
    /// an empty string, which would match no host and fail closed.
    /// </summary>
    public class ServerRegionsTests
    {
        [Fact]
        public void Normalize_ReturnsDefault_WhenNull()
        {
            Assert.Equal(ServerRegions.Default, ServerRegions.Normalize(null));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void Normalize_ReturnsDefault_WhenBlank(string region)
        {
            Assert.Equal(ServerRegions.Default, ServerRegions.Normalize(region));
        }

        [Fact]
        public void Normalize_TrimsSurroundingWhitespace()
        {
            Assert.Equal("SEA", ServerRegions.Normalize("  SEA  "));
        }

        [Fact]
        public void Normalize_PreservesCasing()
        {
            // The routing queries compare with exact equality in Postgres, so Normalize must not
            // fold case: doing so would silently rewrite a region the operator configured.
            Assert.Equal("sea", ServerRegions.Normalize("sea"));
        }
    }
}
