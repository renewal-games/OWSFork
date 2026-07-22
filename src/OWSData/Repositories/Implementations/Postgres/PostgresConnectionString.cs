using System;
using System.Collections.Concurrent;
using Npgsql;

namespace OWSData.Repositories.Implementations.Postgres
{
    /// <summary>
    /// Applies safe connection-pool bounds to the Postgres connection string.
    /// </summary>
    /// <remarks>
    /// Npgsql's default Maximum Pool Size is 100 per process. Several OWS services share
    /// a single Postgres whose max_connections is modest on the reference deployment, so
    /// unbounded per-process pools can exhaust the server ("too many clients"). This caps
    /// each process's pool unless the connection string already specifies pool sizing.
    /// Ops can tune the cap per host with OWS_DB_MAX_POOL_SIZE / OWS_DB_MIN_POOL_SIZE.
    /// Results are memoized so the builder only parses each distinct raw string once.
    /// </remarks>
    public static class PostgresConnectionString
    {
        private const int DefaultMaxPoolSize = 20;
        private const int DefaultMinPoolSize = 1;

        private static readonly ConcurrentDictionary<string, string> Normalized = new();

        public static string WithPoolDefaults(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return connectionString;
            }

            return Normalized.GetOrAdd(connectionString, Build);
        }

        private static string Build(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);

            bool explicitPoolSizing =
                connectionString.Contains("Pool Size", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("MaxPoolSize", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("MinPoolSize", StringComparison.OrdinalIgnoreCase);

            if (!explicitPoolSizing)
            {
                builder.Pooling = true;
                builder.MaxPoolSize = ReadEnvInt("OWS_DB_MAX_POOL_SIZE", DefaultMaxPoolSize);
                builder.MinPoolSize = ReadEnvInt("OWS_DB_MIN_POOL_SIZE", DefaultMinPoolSize);
            }

            return builder.ConnectionString;
        }

        private static int ReadEnvInt(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
        }
    }
}
