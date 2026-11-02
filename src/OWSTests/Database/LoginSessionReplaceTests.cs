using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSData.Repositories.Interfaces;
using OWSPublicAPI.Requests.Users;
using OWSPublicAPI.Services;
using OWSShared.Interfaces;
using OWSShared.Options;
using OWSTests.PublicAPI;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// A login refused by LoginGate (here a ban) keeps the account's live session, and an admitted login replaces it.
    /// The login functions used to delete the earlier sessions before the gate ran. Runs the real handlers, LoginGate and
    /// UsersRepository against both the fresh-install schema and the migrated one. The ban is a stand-in: setup.sql has
    /// no AccountSanctions table.
    /// </summary>
    public class LoginSessionReplaceTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public LoginSessionReplaceTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task PasswordLogin_RefusedByBan_KeepsTheLiveSession(SchemaSource source)
        {
            var env = new Env(_databases.ConnectionString(source));
            Assert.True((await env.Users.RegisterUser(env.CustomerGUID, "player@example.com", "pw", "P", "L")).Success);

            Guid live = Admitted(await env.PasswordLogin("player@example.com"));
            Guid userGUID = await env.UserOf(live);
            env.Banned = true;

            PlayerLoginAndCreateSession refused = await env.PasswordLogin("player@example.com");

            Assert.False(refused.Authenticated);
            Assert.Equal(new[] { live }, await env.SessionsOf(userGUID));

            env.Banned = false;
            Guid replacement = Admitted(await env.PasswordLogin("player@example.com"));

            Assert.Equal(new[] { replacement }, await env.SessionsOf(userGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task SteamLogin_RefusedByBan_KeepsTheLiveSession(SchemaSource source)
        {
            var env = new Env(_databases.ConnectionString(source));

            Guid live = Admitted(await env.SteamLogin("76561198000000077"));
            Guid userGUID = await env.UserOf(live);
            env.Banned = true;

            PlayerLoginAndCreateSession refused = await env.SteamLogin("76561198000000077");

            Assert.False(refused.Authenticated);
            Assert.Equal(new[] { live }, await env.SessionsOf(userGUID));

            env.Banned = false;
            Guid replacement = Admitted(await env.SteamLogin("76561198000000077"));

            Assert.Equal(new[] { replacement }, await env.SessionsOf(userGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.SetupSql)]
        [InlineData(SchemaSource.Migrated)]
        public async Task EndEarlierSessions_EndsOnlyOlderSessionsOfTheSameAccount(SchemaSource source)
        {
            var env = new Env(_databases.ConnectionString(source));
            Assert.True((await env.Users.RegisterUser(env.CustomerGUID, "player@example.com", "pw", "P", "L")).Success);
            Assert.True((await env.Users.RegisterUser(env.CustomerGUID, "other@example.com", "pw", "O", "L")).Success);

            // The login functions alone leave earlier sessions in place.
            Guid first = await env.RawLogin("player@example.com");
            Guid second = await env.RawLogin("player@example.com");
            Guid third = await env.RawLogin("player@example.com");
            Guid other = await env.RawLogin("other@example.com");
            Guid userGUID = await env.UserOf(first);
            Assert.Equal(new[] { first, second, third }, await env.SessionsOf(userGUID));

            // A slower request for the second login must not end the newer third session.
            Assert.Equal(new[] { first }, await env.Users.EndEarlierSessions(env.CustomerGUID, second));
            Assert.Equal(new[] { second }, await env.Users.EndEarlierSessions(env.CustomerGUID, third));

            Assert.Equal(new[] { third }, await env.SessionsOf(userGUID));
            Assert.Equal(new[] { other }, await env.SessionsOf(await env.UserOf(other)));
            Assert.Empty(await env.Users.EndEarlierSessions(env.CustomerGUID, Guid.NewGuid()));
        }

        private static Guid Admitted(PlayerLoginAndCreateSession login)
        {
            Assert.True(login.Authenticated, login.ErrorMessage);
            Assert.True(login.UserSessionGuid.HasValue && login.UserSessionGuid.Value != Guid.Empty);
            return login.UserSessionGuid.Value;
        }

        private sealed class Env
        {
            private readonly string _connectionString;
            private readonly IAuditRepository _audit;
            private readonly LoginGate _gate;
            private readonly IHeaderCustomerGUID _customer;
            private readonly Fake<ISteamAuthService> _steamFake;
            private readonly ISteamAuthService _steam;

            public Env(string connectionString)
            {
                _connectionString = connectionString;
                var storage = Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = connectionString });
                Users = new UsersRepository(storage);
                _customer = new CustomerHeader { CustomerGUID = Guid.NewGuid() };

                (ISanctionsRepository sanctions, Fake<ISanctionsRepository> sanctionsFake) = Fake<ISanctionsRepository>.Create();
                sanctionsFake.Handlers["GetActive"] = _ => Task.FromResult(new ActiveSanctions
                {
                    Ban = Banned ? new Sanction { Kind = SanctionKinds.Ban, Active = true, Reason = "test" } : null
                });

                // ConsoleSettings is one table for the whole database behind a static cache, so maintenance stays a fake.
                (IConsoleSettingsRepository settings, Fake<IConsoleSettingsRepository> settingsFake) = Fake<IConsoleSettingsRepository>.Create();
                settingsFake.Handlers["GetAll"] = _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

                (_audit, Fake<IAuditRepository> auditFake) = Fake<IAuditRepository>.Create();
                auditFake.Handlers["Add"] = _ => Task.CompletedTask;

                (_steam, _steamFake) = Fake<ISteamAuthService>.Create();
                _steamFake.Handlers["GetPersonaName"] = _ => Task.FromResult("Persona");

                _gate = new LoginGate(sanctions, settings, Users);
            }

            public UsersRepository Users { get; }
            public bool Banned { get; set; }
            public Guid CustomerGUID => _customer.CustomerGUID;

            public async Task<PlayerLoginAndCreateSession> PasswordLogin(string email)
            {
                var request = new LoginAndCreateSessionRequest { Email = email, Password = "pw" };
                request.SetData(Users, _customer, _audit, _gate);
                return Body(await request.Handle());
            }

            public async Task<PlayerLoginAndCreateSession> SteamLogin(string steamId)
            {
                _steamFake.Handlers["ValidateAuthTicket"] = _ => Task.FromResult(new SteamTicketValidationResult { IsValid = true, SteamId = steamId });
                var request = new SteamLoginAndCreateSessionRequest { SteamAuthTicket = "00" };
                request.SetData(Users, _steam, _customer, _audit, _gate);
                return Body(await request.Handle());
            }

            /// <summary>The login function alone, without LoginGate or EndEarlierSessions.</summary>
            public async Task<Guid> RawLogin(string email) => Admitted(await Users.LoginAndCreateSession(CustomerGUID, email, "pw"));

            public async Task<Guid> UserOf(Guid userSessionGUID)
            {
                GetUserSession session = await Users.GetUserSession(CustomerGUID, userSessionGUID);
                Assert.NotNull(session);
                return session.UserGuid.Value;
            }

            /// <summary>The account's sessions, oldest first.</summary>
            public async Task<Guid[]> SessionsOf(Guid userGUID)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return (await connection.QueryAsync<Guid>(
                    "SELECT UserSessionGUID FROM UserSessions WHERE UserGUID = @UserGUID ORDER BY LoginDate, UserSessionGUID",
                    new { UserGUID = userGUID })).ToArray();
            }

            private static PlayerLoginAndCreateSession Body(IActionResult result) =>
                Assert.IsType<PlayerLoginAndCreateSession>(Assert.IsType<OkObjectResult>(result).Value);
        }

        private sealed class CustomerHeader : IHeaderCustomerGUID
        {
            public Guid CustomerGUID { get; set; }
        }
    }
}
