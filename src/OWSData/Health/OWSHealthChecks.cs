using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using RabbitMQ.Client;

namespace OWSData.Health
{
    /// <summary>
    /// Dependency checks behind each service's /health. Every probe logs in fresh, so a stale password fails
    /// even while pooled connections still work; results are cached so the endpoint cannot flood the database.
    /// The Postgres check also fails when the schema is behind this build (<see cref="SchemaVersionCheck"/>).
    /// </summary>
    public static class OWSHealthChecks
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

        public static IHealthChecksBuilder AddOWSPostgres(this IHealthChecksBuilder builder, IConfiguration configuration)
        {
            string connectionString = configuration["OWSStorageConfig:OWSDBConnectionString"];
            if (string.IsNullOrEmpty(connectionString) || configuration["OWSStorageConfig:OWSDBBackend"] != "postgres")
                return builder;

            var csb = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 5, CommandTimeout = 5 };
            return builder.AddCheck("postgres", new CachedCheck(async ct =>
            {
                await using var connection = new NpgsqlConnection(csb.ConnectionString);
                await connection.OpenAsync(ct);
                return await SchemaVersionCheck.FindProblemAsync(connection, ct);
            }));
        }

        public static IHealthChecksBuilder AddOWSRabbitMQ(this IHealthChecksBuilder builder, IConfiguration configuration)
        {
            var factory = new ConnectionFactory
            {
                HostName = configuration["RabbitMQOptions:RabbitMQHostName"],
                Port = configuration.GetValue("RabbitMQOptions:RabbitMQPort", AmqpTcpEndpoint.UseDefaultPort),
                UserName = configuration["RabbitMQOptions:RabbitMQUserName"],
                Password = configuration["RabbitMQOptions:RabbitMQPassword"],
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
            };
            return builder.AddCheck("rabbitmq", new CachedCheck(ct => Task.Run(() =>
            {
                using IConnection connection = factory.CreateConnection();
                return (string)null;
            }, ct)));
        }

        private sealed class CachedCheck : IHealthCheck
        {
            // Resolves to a description of what is wrong, or null when healthy; a throw counts as unhealthy too.
            private readonly Func<CancellationToken, Task<string>> _probe;
            private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
            private HealthCheckResult _last;
            private DateTime _lastUtc = DateTime.MinValue;

            public CachedCheck(Func<CancellationToken, Task<string>> probe) => _probe = probe;

            public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (DateTime.UtcNow - _lastUtc < CacheFor)
                        return _last;

                    try
                    {
                        string problem = await _probe(cancellationToken);
                        _last = problem == null
                            ? HealthCheckResult.Healthy()
                            : new HealthCheckResult(context.Registration.FailureStatus, problem);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _last = new HealthCheckResult(context.Registration.FailureStatus, ex.Message);
                    }

                    _lastUtc = DateTime.UtcNow;
                    return _last;
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
    }
}
