using System.IO;
using System.Linq;
using OWSData.Health;
using OWSTests.Database;
using Xunit;

namespace OWSTests.Health
{
    /// <summary>
    /// /health must go red when the journal is behind this build, and the build's idea of "current" must follow
    /// migration-manifest.txt, so a new migration cannot ship without the health check expecting it.
    /// </summary>
    public class SchemaVersionCheckTests
    {
        [Fact]
        public void IsCurrent_False_WhenExpectedScriptMissing()
        {
            Assert.False(SchemaVersionCheck.IsCurrent(new[] { "A.sql", "B.sql" }, "C.sql"));
        }

        [Fact]
        public void IsCurrent_False_WhenJournalEmptyOrNull()
        {
            Assert.False(SchemaVersionCheck.IsCurrent(new string[0], "C.sql"));
            Assert.False(SchemaVersionCheck.IsCurrent(null, "C.sql"));
        }

        [Fact]
        public void IsCurrent_True_WhenExpectedScriptPresent()
        {
            Assert.True(SchemaVersionCheck.IsCurrent(new[] { "A.sql", "B.sql", "C.sql" }, "C.sql"));
        }

        [Fact]
        public void IsCurrent_IsCaseSensitive_LikeTheJournal()
        {
            Assert.False(SchemaVersionCheck.IsCurrent(new[] { "c.sql" }, "C.sql"));
        }

        [Fact]
        public void ExpectedLastScript_IsLastManifestEntry()
        {
            string manifest = Path.Combine(PostgresTestDatabases.FindRepoRoot(), "Databases", "Postgres", "migration-manifest.txt");
            string last = File.ReadAllLines(manifest)
                .Select(line => line.Trim())
                .Last(line => line.Length > 0 && !line.StartsWith("#"));

            Assert.Equal(last, SchemaVersionCheck.ExpectedLastScript);
        }
    }
}
