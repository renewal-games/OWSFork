using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSPublicAPI.Requests.Users;
using OWSPublicAPI.Services;
using OWSShared.Interfaces;
using Xunit;

namespace OWSTests.PublicAPI
{
    /// <summary>
    /// The password and Steam login handlers: a login that LoginGate refuses (ban, maintenance) ends the session the
    /// login SQL already created, and a database error answers "Login service unavailable" instead of throwing.
    /// </summary>
    public class LoginHandlerTests
    {
        private static readonly Guid SessionGUID = Guid.NewGuid();
        private static readonly Guid UserGUID = Guid.NewGuid();

        [Fact]
        public async Task PasswordLogin_Banned_LogsOutTheNewSession()
        {
            var env = new Env(banned: true);

            PlayerLoginAndCreateSession result = await env.Password();

            Assert.False(result.Authenticated);
            Assert.Equal(Guid.Empty, result.UserSessionGuid);
            Assert.Equal(new[] { SessionGUID }, env.LoggedOutSessions());
        }

        [Fact]
        public async Task PasswordLogin_Maintenance_LogsOutTheNewSession()
        {
            var env = new Env(banned: false, loginsOpen: false);

            PlayerLoginAndCreateSession result = await env.Password();

            Assert.False(result.Authenticated);
            Assert.Equal(LoginGate.MaintenanceMessage, result.ErrorMessage);
            Assert.Equal(new[] { SessionGUID }, env.LoggedOutSessions());
        }

        [Fact]
        public async Task SteamLogin_Banned_LogsOutTheNewSession()
        {
            var env = new Env(banned: true);

            PlayerLoginAndCreateSession result = await env.Steam();

            Assert.False(result.Authenticated);
            Assert.Equal(Guid.Empty, result.UserSessionGuid);
            Assert.Equal(new[] { SessionGUID }, env.LoggedOutSessions());
        }

        [Fact]
        public async Task AllowedLogins_KeepTheSession()
        {
            var env = new Env(banned: false);

            Assert.Equal(SessionGUID, (await env.Password()).UserSessionGuid);
            Assert.Equal(SessionGUID, (await env.Steam()).UserSessionGuid);
            Assert.Empty(env.LoggedOutSessions());
        }

        [Fact]
        public async Task PasswordLogin_DatabaseError_AnswersUnavailable()
        {
            var env = new Env(banned: false, loginThrows: true);

            PlayerLoginAndCreateSession result = await env.Password();

            Assert.False(result.Authenticated);
            Assert.Equal("Login service unavailable", result.ErrorMessage);
        }

        [Fact]
        public async Task SteamLogin_DatabaseError_AnswersUnavailable()
        {
            var env = new Env(banned: false, loginThrows: true);

            PlayerLoginAndCreateSession result = await env.Steam();

            Assert.False(result.Authenticated);
            Assert.Equal(Guid.Empty, result.UserSessionGuid);
            Assert.Equal("Login service unavailable", result.ErrorMessage);
        }

        private sealed class Env
        {
            private readonly Fake<IUsersRepository> _users;
            private readonly IUsersRepository _usersProxy;
            private readonly IAuditRepository _audit;
            private readonly LoginGate _gate;
            private readonly ISteamAuthService _steam;
            private readonly IHeaderCustomerGUID _customer = new CustomerHeader { CustomerGUID = Guid.NewGuid() };

            public Env(bool banned, bool loginsOpen = true, bool loginThrows = false)
            {
                (_usersProxy, _users) = Fake<IUsersRepository>.Create();
                Func<object[], object> login = loginThrows
                    ? _ => Task.FromException<PlayerLoginAndCreateSession>(new InvalidOperationException("23505: duplicate key value violates unique constraint"))
                    : _ => Task.FromResult(new PlayerLoginAndCreateSession { Authenticated = true, UserSessionGuid = SessionGUID, ErrorMessage = "" });
                _users.Handlers["LoginAndCreateSession"] = login;
                _users.Handlers["SteamLoginAndCreateSession"] = login;
                _users.Handlers["GetUserSession"] = _ => Task.FromResult(new GetUserSession { UserGuid = UserGUID, UserSessionGUID = SessionGUID, Role = "Player" });
                _users.Handlers["Logout"] = _ => Task.FromResult(new SuccessAndErrorMessage { Success = true });

                (ISanctionsRepository sanctions, Fake<ISanctionsRepository> sanctionsFake) = Fake<ISanctionsRepository>.Create();
                sanctionsFake.Handlers["GetActive"] = _ => Task.FromResult(new ActiveSanctions
                {
                    Ban = banned ? new Sanction { Kind = SanctionKinds.Ban, Active = true, Reason = "test" } : null
                });

                (IConsoleSettingsRepository settings, Fake<IConsoleSettingsRepository> settingsFake) = Fake<IConsoleSettingsRepository>.Create();
                settingsFake.Handlers["GetAll"] = _ => Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string> { [ConsoleSettingKeys.LoginsOpen] = loginsOpen ? "true" : "false" });

                (_audit, Fake<IAuditRepository> auditFake) = Fake<IAuditRepository>.Create();
                auditFake.Handlers["Add"] = _ => Task.CompletedTask;

                (_steam, Fake<ISteamAuthService> steamFake) = Fake<ISteamAuthService>.Create();
                steamFake.Handlers["ValidateAuthTicket"] = _ => Task.FromResult(new SteamTicketValidationResult { IsValid = true, SteamId = "76561198000000001" });
                steamFake.Handlers["GetPersonaName"] = _ => Task.FromResult("Persona");

                _gate = new LoginGate(sanctions, settings, _usersProxy);
            }

            public async Task<PlayerLoginAndCreateSession> Password()
            {
                var request = new LoginAndCreateSessionRequest { Email = "player@example.com", Password = "pw" };
                request.SetData(_usersProxy, _customer, _audit, _gate);
                return Body(await request.Handle());
            }

            public async Task<PlayerLoginAndCreateSession> Steam()
            {
                var request = new SteamLoginAndCreateSessionRequest { SteamAuthTicket = "00" };
                request.SetData(_usersProxy, _steam, _customer, _audit, _gate);
                return Body(await request.Handle());
            }

            public Guid[] LoggedOutSessions() =>
                _users.Calls.Where(c => c.Method == "Logout").Select(c => (Guid)c.Args[1]).ToArray();

            private static PlayerLoginAndCreateSession Body(IActionResult result) =>
                Assert.IsType<PlayerLoginAndCreateSession>(Assert.IsType<OkObjectResult>(result).Value);
        }

        private sealed class CustomerHeader : IHeaderCustomerGUID
        {
            public Guid CustomerGUID { get; set; }
        }
    }

    /// <summary>An interface fake: each method answers from <see cref="Handlers"/> by name, and every call is recorded.</summary>
    public class Fake<T> : DispatchProxy where T : class
    {
        public Dictionary<string, Func<object[], object>> Handlers { get; } = new();
        public List<(string Method, object[] Args)> Calls { get; } = new();

        public static (T Proxy, Fake<T> Fake) Create()
        {
            T proxy = Create<T, Fake<T>>();
            return (proxy, (Fake<T>)(object)proxy);
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Calls.Add((targetMethod.Name, args));
            return Handlers.TryGetValue(targetMethod.Name, out var handler)
                ? handler(args)
                : throw new NotImplementedException($"{typeof(T).Name}.{targetMethod.Name} is not faked.");
        }
    }
}
