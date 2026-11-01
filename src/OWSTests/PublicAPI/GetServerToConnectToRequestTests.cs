using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSPublicAPI.Requests.Users;
using OWSPublicAPI.Services;
using OWSShared.Interfaces;
using OWSShared.Options;
using Xunit;

namespace OWSTests.PublicAPI
{
    public class GetServerToConnectToRequestTests
    {
        private static readonly Guid UserGuid = Guid.NewGuid();
        private static readonly Guid TicketGuid = Guid.NewGuid();

        [Theory]
        [InlineData("ZoneB", "Player")]
        [InlineData("ZoneB", "GameMaster")]
        [InlineData("", "Player")]
        [InlineData("GETLASTZONENAME", "Player")]
        public async Task ClientJoinsStoredZoneWithStoredPosition(string requestedZone, string role)
        {
            var (request, calls) = CreateRequest(requestedZone, callerIsZoneServer: false, role: role);

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.True(result.Success);
            Assert.Equal("ZoneA", result.ZoneName);
            Assert.Equal(TicketGuid, result.JoinTicket);
            Assert.Equal("ZoneA", calls.JoinedZone);
            Assert.Equal(JoinTicketStartMode.StoredPosition, calls.TicketStart.Mode);
        }

        [Theory]
        [InlineData("Player")]
        [InlineData("GameMaster")]
        public async Task ZoneServerCanJoinRequestedZoneWithExplicitStart(string role)
        {
            var (request, calls) = CreateRequest("ZoneB", callerIsZoneServer: true, role: role);
            request.StartMode = JoinTicketStartMode.Explicit;
            request.StartX = 123;

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.True(result.Success);
            Assert.Equal("ZoneB", result.ZoneName);
            Assert.Equal(TicketGuid, result.JoinTicket);
            Assert.Equal("ZoneB", calls.JoinedZone);
            Assert.Equal(JoinTicketStartMode.Explicit, calls.TicketStart.Mode);
            Assert.Equal(123, calls.TicketStart.X);
        }

        private static (GetServerToConnectToRequest Request, Calls Calls) CreateRequest(string zoneName, bool callerIsZoneServer, string role)
        {
            var calls = new Calls();
            var customerGuid = Guid.NewGuid();
            var sessionGuid = Guid.NewGuid();
            IUsersRepository users = Stub<IUsersRepository>.Create(new()
            {
                [nameof(IUsersRepository.GetUserSession)] = _ => Task.FromResult(new GetUserSession { UserGuid = UserGuid, Role = role })
            });
            ICharactersRepository characters = Stub<ICharactersRepository>.Create(new()
            {
                [nameof(ICharactersRepository.GetCharByCharName)] = _ => Task.FromResult(new GetCharByCharName
                {
                    UserGuid = UserGuid, CharacterId = 7, CharName = "Hero", MapName = "ZoneA"
                }),
                [nameof(ICharactersRepository.JoinMapByCharName)] = args =>
                {
                    calls.JoinedZone = (string)args[2];
                    return Task.FromResult(new JoinMapByCharName
                    {
                        Success = true, ZoneName = calls.JoinedZone, WorldServerID = 1,
                        MapInstanceID = 42, MapInstanceStatus = 2
                    });
                },
                [nameof(ICharactersRepository.AddCharacterToMapInstanceByCharName)] = _ => Task.CompletedTask
            });
            ISanctionsRepository sanctions = Stub<ISanctionsRepository>.Create(new()
            {
                [nameof(ISanctionsRepository.GetActive)] = _ => Task.FromResult(new ActiveSanctions())
            });
            IConsoleSettingsRepository settings = Stub<IConsoleSettingsRepository>.Create(new()
            {
                [nameof(IConsoleSettingsRepository.GetAll)] = _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>())
            });
            IJoinTicketsRepository tickets = Stub<IJoinTicketsRepository>.Create(new()
            {
                [nameof(IJoinTicketsRepository.IssueTicket)] = args =>
                {
                    calls.TicketStart = (JoinTicketStart)args[6];
                    return Task.FromResult(TicketGuid);
                }
            });
            IAuditRepository audit = Stub<IAuditRepository>.Create(new()
            {
                [nameof(IAuditRepository.Add)] = _ => Task.CompletedTask
            });
            IHeaderCustomerGUID customer = Stub<IHeaderCustomerGUID>.Create(new()
            {
                ["get_CustomerGUID"] = _ => customerGuid
            });
            ILoginQueueRepository queue = Stub<ILoginQueueRepository>.Create(new());

            var request = new GetServerToConnectToRequest
            {
                UserSessionGUID = sessionGuid, CharacterName = "Hero", ZoneName = zoneName
            };
            request.SetData(Microsoft.Extensions.Options.Options.Create(new PublicAPIOptions()), users, characters, customer, null, audit,
                tickets, callerIsZoneServer, new LoginGate(sanctions, settings, users), queue);
            return (request, calls);
        }

        private class Calls
        {
            public string JoinedZone;
            public JoinTicketStart TicketStart;
        }

        public class Stub<T> : DispatchProxy
        {
            private Dictionary<string, Func<object[], object>> members;

            public static T Create(Dictionary<string, Func<object[], object>> members)
            {
                T proxy = Create<T, Stub<T>>();
                ((Stub<T>)(object)proxy).members = members;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args) =>
                members.TryGetValue(targetMethod.Name, out var member)
                    ? member(args)
                    : throw new NotSupportedException($"{typeof(T).Name}.{targetMethod.Name} is not stubbed.");
        }
    }
}
