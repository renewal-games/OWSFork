using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
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

        // 503: SpinUpServerInstance found no launcher queue to take the command. 4xx: refused before publishing.
        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Unauthorized)]
        public async Task SpinUpCertainlyNotSent_RemovesTheUnstartedInstance(HttpStatusCode spinUpStatus)
        {
            var (request, calls) = CreateRequest("ZoneA", callerIsZoneServer: false, role: "Player", needToStartupMap: true,
                spinUp: _ => new HttpResponseMessage(spinUpStatus));

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.False(result.Success);
            Assert.Equal(42, calls.ReleasedInstance);
            Assert.Equal(42, calls.RemovedUnstartedInstance);
        }

        // 504: the RabbitMQ confirm was lost or timed out after the publish, so the launcher may already be starting the
        // server. 500 and 502 (a proxy) say nothing about whether the publish happened.
        [Theory]
        [InlineData(HttpStatusCode.GatewayTimeout)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        public async Task SpinUpMaybeSent_KeepsTheInstanceForTheLauncher(HttpStatusCode spinUpStatus)
        {
            var (request, calls) = CreateRequest("ZoneA", callerIsZoneServer: false, role: "Player", needToStartupMap: true,
                spinUp: _ => new HttpResponseMessage(spinUpStatus));

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.False(result.Success);
            Assert.Equal(42, calls.ReleasedInstance);
            Assert.Null(calls.RemovedUnstartedInstance);
        }

        public static TheoryData<Exception> ResponseLostAfterSending => new()
        {
            // HttpClient.Timeout: InstanceManagement may have published and been slow to answer.
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            // The connection dropped after the request went out.
            new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
            new HttpRequestException("An error occurred while sending the request.")
        };

        [Theory]
        [MemberData(nameof(ResponseLostAfterSending))]
        public async Task SpinUpResponseLost_KeepsTheInstanceForTheLauncher(Exception failure)
        {
            var (request, calls) = CreateRequest("ZoneA", callerIsZoneServer: false, role: "Player", needToStartupMap: true,
                spinUp: _ => throw failure);

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.False(result.Success);
            Assert.Equal(42, calls.ReleasedInstance);
            Assert.Null(calls.RemovedUnstartedInstance);
        }

        [Theory]
        [InlineData(HttpRequestError.ConnectionError)]
        [InlineData(HttpRequestError.NameResolutionError)]
        public async Task UnreachableInstanceManagement_RemovesTheUnstartedInstance(HttpRequestError error)
        {
            var (request, calls) = CreateRequest("ZoneA", callerIsZoneServer: false, role: "Player", needToStartupMap: true,
                spinUp: _ => throw new HttpRequestException(error, "Connection refused"));

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.False(result.Success);
            Assert.Equal(42, calls.ReleasedInstance);
            Assert.Equal(42, calls.RemovedUnstartedInstance);
        }

        [Fact]
        public async Task StartingInstanceThatTimesOut_IsLeftForTheLauncher()
        {
            // Joining an instance someone else is spinning up: the command did reach a launcher, the server may just be slow.
            var (request, calls) = CreateRequest("ZoneA", callerIsZoneServer: false, role: "Player", mapInstanceStatus: 1);

            JoinMapByCharName result = Assert.IsType<JoinMapByCharName>(Assert.IsType<OkObjectResult>(await request.Handle()).Value);

            Assert.False(result.Success);
            Assert.Equal(42, calls.ReleasedInstance);
            Assert.Null(calls.RemovedUnstartedInstance);
        }

        private static (GetServerToConnectToRequest Request, Calls Calls) CreateRequest(string zoneName, bool callerIsZoneServer, string role,
            bool needToStartupMap = false, int mapInstanceStatus = 2, Func<HttpRequestMessage, HttpResponseMessage> spinUp = null)
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
                        MapInstanceID = 42, MapInstanceStatus = needToStartupMap ? 1 : mapInstanceStatus,
                        NeedToStartupMap = needToStartupMap, MapNameToStart = "ZoneA", Port = 7778
                    });
                },
                [nameof(ICharactersRepository.AddCharacterToMapInstanceByCharName)] = _ => Task.CompletedTask,
                [nameof(ICharactersRepository.CheckMapInstanceStatus)] = _ => Task.FromResult(new OWSData.Models.Tables.MapInstances { Status = 1 }),
                [nameof(ICharactersRepository.ReleaseCharacterMapReservation)] = args =>
                {
                    calls.ReleasedInstance = (int)args[2];
                    return Task.CompletedTask;
                },
                [nameof(ICharactersRepository.RemoveUnstartedMapInstance)] = args =>
                {
                    calls.RemovedUnstartedInstance = (int)args[1];
                    return Task.FromResult(true);
                }
            });
            IHttpClientFactory httpClientFactory = Stub<IHttpClientFactory>.Create(new()
            {
                [nameof(IHttpClientFactory.CreateClient)] = _ => new HttpClient(new Responder(spinUp)) { BaseAddress = new Uri("http://instancemanagement/") }
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
            // No waiting: a server that is not ready on the first poll counts as timed out.
            var options = new PublicAPIOptions { SecondsToWaitBeforeFirstPollForSpinUp = 0, SecondsToWaitForServerSpinUp = 0, SecondsToWaitInBetweenSpinUpPolling = 0 };
            request.SetData(Microsoft.Extensions.Options.Options.Create(options), users, characters, customer, httpClientFactory, audit,
                tickets, callerIsZoneServer, new LoginGate(sanctions, settings, users), queue);
            return (request, calls);
        }

        private class Calls
        {
            public string JoinedZone;
            public JoinTicketStart TicketStart;
            public int? ReleasedInstance;
            public int? RemovedUnstartedInstance;
        }

        private sealed class Responder : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

            public Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                respond == null
                    ? throw new NotSupportedException($"No HTTP call expected, got {request.RequestUri}.")
                    : Task.FromResult(respond(request));
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
