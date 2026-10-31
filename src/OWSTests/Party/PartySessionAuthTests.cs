using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSParty.Service;
using OWSShared.Grpc;
using OWSTests.Grpc;
using OWSShared.Implementations;
using PartyServiceApp.Protos;
using Xunit;

namespace OWSTests.Party
{
    /// <summary>
    /// With OWS_GRPC_REQUIRE_SESSION_AUTH on, SendPartyMsg only acts for the character the caller's session
    /// has selected; the names in the request body are not trusted. With it off, behaviour is unchanged.
    /// </summary>
    [Collection(SessionAuthEnvironmentCollection.Name)]
    public class PartySessionAuthTests
    {
        private static readonly Guid CustomerGuid = Guid.NewGuid();
        private static readonly Guid LeaderSession = Guid.NewGuid();
        private static readonly Guid OtherSession = Guid.NewGuid();
        private const string LeaderName = "Leader";
        private const string MemberName = "Member";
        private const string OtherName = "Other";

        [Theory]
        [InlineData(PartyAction.MessageTypeKick)]
        [InlineData(PartyAction.MessageTypeDismiss)]
        public async Task SessionAuthOn_NoMetadata_IsUnauthenticated(PartyAction action)
        {
            using SessionAuthFlag flag = new SessionAuthFlag(true);
            (PartyService service, List<string> repositoryCalls) = CreateService();

            RpcException ex = await Assert.ThrowsAsync<RpcException>(() => service.SendPartyMsg(LeaderRequest(action), new TestServerCallContext()));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
            Assert.Empty(repositoryCalls);
        }

        [Theory]
        [InlineData(PartyAction.MessageTypeKick)]
        [InlineData(PartyAction.MessageTypeDismiss)]
        [InlineData(PartyAction.MessageTypeMakeLead)]
        public async Task SessionAuthOn_OtherCharactersSessionNamingLeader_IsUnauthenticated(PartyAction action)
        {
            using SessionAuthFlag flag = new SessionAuthFlag(true);
            (PartyService service, List<string> repositoryCalls) = CreateService();

            RpcException ex = await Assert.ThrowsAsync<RpcException>(() => service.SendPartyMsg(LeaderRequest(action), SessionContext(OtherSession)));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
            Assert.Empty(repositoryCalls);
        }

        [Fact]
        public async Task SessionAuthOn_AskAsSomeoneElse_IsUnauthenticated()
        {
            using SessionAuthFlag flag = new SessionAuthFlag(true);
            (PartyService service, _) = CreateService();
            PartyToSend ask = new PartyToSend { CustomerGuid = CustomerGuid.ToString(), PartyAction = PartyAction.MessageTypeAsk };
            ask.PartyMembers.Add(new PartyMemberInfo { CharName = LeaderName });
            ask.PartyMembers.Add(new PartyMemberInfo { CharName = MemberName });

            RpcException ex = await Assert.ThrowsAsync<RpcException>(() => service.SendPartyMsg(ask, SessionContext(OtherSession)));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        }

        [Fact]
        public async Task SessionAuthOn_SessionForAnotherCustomer_IsUnauthenticated()
        {
            using SessionAuthFlag flag = new SessionAuthFlag(true);
            (PartyService service, List<string> repositoryCalls) = CreateService();

            RpcException ex = await Assert.ThrowsAsync<RpcException>(
                () => service.SendPartyMsg(LeaderRequest(PartyAction.MessageTypeKick), SessionContext(LeaderSession, Guid.NewGuid())));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
            Assert.Empty(repositoryCalls);
        }

        [Theory]
        [InlineData(PartyAction.MessageTypeKick, "LeaveParty")]
        [InlineData(PartyAction.MessageTypeDismiss, "LeaveParty")]
        [InlineData(PartyAction.MessageTypeMakeLead, "ChangePartyLeader")]
        public async Task SessionAuthOn_LeadersOwnSession_Succeeds(PartyAction action, string expectedCall)
        {
            using SessionAuthFlag flag = new SessionAuthFlag(true);
            (PartyService service, List<string> repositoryCalls) = CreateService();

            await service.SendPartyMsg(LeaderRequest(action), SessionContext(LeaderSession));

            Assert.Equal(new[] { expectedCall }, repositoryCalls);
        }

        [Fact]
        public async Task SessionAuthOn_LeaveWithOwnSession_Succeeds()
        {
            using SessionAuthFlag flag = new SessionAuthFlag(true);
            (PartyService service, List<string> repositoryCalls) = CreateService();
            PartyToSend leave = new PartyToSend
            {
                CustomerGuid = CustomerGuid.ToString(),
                PartyAction = PartyAction.MessageTypeLeave,
                PartyInfo = new PartyInfo { PartyGuid = Guid.NewGuid().ToString() }
            };
            leave.PartyMembers.Add(new PartyMemberInfo { CharName = OtherName });

            await service.SendPartyMsg(leave, SessionContext(OtherSession));

            Assert.Equal(new[] { "LeaveParty" }, repositoryCalls);
        }

        [Fact]
        public async Task SessionAuthOff_NoMetadata_IsUnchanged()
        {
            using SessionAuthFlag flag = new SessionAuthFlag(false);
            (PartyService service, List<string> repositoryCalls) = CreateService();

            await service.SendPartyMsg(LeaderRequest(PartyAction.MessageTypeKick), new TestServerCallContext());

            Assert.Equal(new[] { "LeaveParty" }, repositoryCalls);
        }

        private static PartyToSend LeaderRequest(PartyAction action)
        {
            PartyToSend request = new PartyToSend
            {
                CustomerGuid = CustomerGuid.ToString(),
                PartyAction = action,
                PartyInfo = new PartyInfo { PartyGuid = Guid.NewGuid().ToString() }
            };
            request.PartyMembers.Add(new PartyMemberInfo { CharName = LeaderName, PartyLeader = true });
            if (action != PartyAction.MessageTypeDismiss)
            {
                request.PartyMembers.Add(new PartyMemberInfo { CharName = MemberName });
            }

            return request;
        }

        private static TestServerCallContext SessionContext(Guid userSessionGuid, Guid? customerGuid = null)
        {
            TestServerCallContext context = new TestServerCallContext();
            context.Headers.Add(GrpcSessionAuth.CustomerGuidHeader, (customerGuid ?? CustomerGuid).ToString());
            context.Headers.Add(GrpcSessionAuth.UserSessionGuidHeader, userSessionGuid.ToString());
            return context;
        }

        private static (PartyService, List<string>) CreateService()
        {
            List<string> repositoryCalls = new List<string>();

            ICharactersRepository characters = FakeRepository.Create<ICharactersRepository>((method, args) =>
            {
                repositoryCalls.Add(method.Name);
                return Task.FromResult(new PartyToSend());
            });

            IUsersRepository users = FakeRepository.Create<IUsersRepository>((method, args) =>
            {
                Assert.Equal(nameof(IUsersRepository.GetUserSession), method.Name);
                Guid customer = (Guid)args[0];
                Guid session = (Guid)args[1];
                string selectedCharacter = customer != CustomerGuid ? null
                    : session == LeaderSession ? LeaderName
                    : session == OtherSession ? OtherName
                    : null;

                return Task.FromResult(selectedCharacter == null
                    ? null
                    : new GetUserSession { CustomerGuid = customer, UserGuid = Guid.NewGuid(), UserSessionGUID = session, SelectedCharacterName = selectedCharacter });
            });

            PartyService service = new PartyService(characters, users, new HeaderCustomerGUID(), NullLogger<PartyService>.Instance);
            return (service, repositoryCalls);
        }

        private sealed class SessionAuthFlag : IDisposable
        {
            private const string Variable = "OWS_GRPC_REQUIRE_SESSION_AUTH";
            private readonly string _previous;

            public SessionAuthFlag(bool required)
            {
                _previous = Environment.GetEnvironmentVariable(Variable);
                Environment.SetEnvironmentVariable(Variable, required ? "true" : "false");
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Variable, _previous);
            }
        }
    }

    /// <summary>Interface stub that routes every call to a handler, so a test only fakes the methods it reaches.</summary>
    public class FakeRepository : DispatchProxy
    {
        private Func<MethodInfo, object[], object> _handler;

        public static T Create<T>(Func<MethodInfo, object[], object> handler) where T : class
        {
            T proxy = Create<T, FakeRepository>();
            ((FakeRepository)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            return _handler(targetMethod, args);
        }
    }

    public class TestServerCallContext : ServerCallContext
    {
        public Metadata Headers { get; } = new Metadata();

        protected override string MethodCore => "/party.PartyApp/SendPartyMsg";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:0";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => Headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new AuthContext(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
