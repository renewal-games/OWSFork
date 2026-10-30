using System;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OWSCharacterPersistence.Services;
using OWSData.Repositories.Interfaces;
using OWSShared;
using Xunit;

namespace OWSTests.CharacterPersistence
{
    /// <summary>
    /// Tests that set ASPNETCORE_ENVIRONMENT and OWS_SAMSARA_SERVICE_KEY. Both are process-wide, so these run one at a
    /// time and after the parallel tests.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class ServiceKeyEnvironmentCollection
    {
        public const string Name = "Service key environment";
    }

    /// <summary>
    /// The CharacterPersistence service in memory under a chosen environment and service key, with stub repositories in
    /// place of Postgres.
    /// </summary>
    public sealed class CharacterPersistenceApp : WebApplicationFactory<OWSCharacterPersistence.Program>
    {
        private const string EnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

        private readonly string _environment;
        private readonly string _previousEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        private readonly string _previousServiceKey = Environment.GetEnvironmentVariable(SamsaraServiceKeyCheck.EnvironmentVariable);

        public RepositoryStub<ICharactersRepository> Characters { get; } = RepositoryStub<ICharactersRepository>.Create();
        public RepositoryStub<ICharacterOwnersRepository> Owners { get; } = RepositoryStub<ICharacterOwnersRepository>.Create();

        /// <param name="serviceKey">The configured OWS_SAMSARA_SERVICE_KEY; null leaves it unset.</param>
        public CharacterPersistenceApp(string environment, string serviceKey)
        {
            _environment = environment;
            SetEnvironment(environment, serviceKey);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environment);
            builder.ConfigureTestServices(services =>
            {
                // Startup reads appsettings.json from the test output, which has no OWSStorageConfig, so it registers no
                // Postgres repositories and Simple Injector cross-wires these from the service collection instead.
                services.AddSingleton(Characters.Repository);
                services.AddSingleton(Owners.Repository);
                services.AddSingleton(RepositoryStub<IJoinTicketsRepository>.Create().Repository);
                services.AddSingleton(RepositoryStub<IAuditRepository>.Create().Repository);
                services.AddSingleton(RepositoryStub<IPlayerShopsRepository>.Create().Repository);
                services.AddSingleton(RepositoryStub<IPlayerReportsRepository>.Create().Repository);
                services.AddSingleton(RepositoryStub<ISanctionsRepository>.Create().Repository);
                services.AddSingleton(RepositoryStub<IZoneOpsRepository>.Create().Repository);
                services.AddSingleton(RepositoryStub<IConsoleSettingsRepository>.Create().Repository);

                // It reads Postgres directly once a Discord webhook is configured.
                services.Remove(services.Single(d => d.ImplementationType == typeof(StaffAlertsWorker)));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SetEnvironment(_previousEnvironment, _previousServiceKey);
        }

        private static void SetEnvironment(string environment, string serviceKey)
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, environment);
            Environment.SetEnvironmentVariable(SamsaraServiceKeyCheck.EnvironmentVariable, serviceKey);
            // The check reads both once per process, so a new host alone would keep the previous test's key.
            SamsaraServiceKeyCheck.ReadEnvironment();
        }
    }
}
