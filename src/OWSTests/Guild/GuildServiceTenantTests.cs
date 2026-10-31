using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using GuildServiceApp.Protos;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSGuild.Service;
using OWSShared.Grpc;
using OWSShared.Implementations;
using Xunit;

namespace OWSTests.Guild
{
    /// <summary>
    /// GuildService takes the customer from the message body. A malformed one must come back as InvalidArgument, not
    /// an unhandled FormatException, and with session auth on the body may only name the session's own customer.
    /// The tests set OWS_GRPC_REQUIRE_SESSION_AUTH, which is process-wide, so they join the Chat/Party session-auth collection.
    /// </summary>
    [Collection(OWSTests.Grpc.SessionAuthEnvironmentCollection.Name)]
    public class GuildServiceTenantTests : IDisposable
    {
        private const string SessionAuthVariable = "OWS_GRPC_REQUIRE_SESSION_AUTH";

        private static readonly Guid Customer = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid OtherCustomer = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid Session = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public void Dispose() => Environment.SetEnvironmentVariable(SessionAuthVariable, null);

        [Theory]
        [InlineData("not-a-guid")]
        [InlineData("")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public async Task SendGuildMsg_RejectsMalformedCustomerGuid_AsInvalidArgument(string customerGuid)
        {
            Environment.SetEnvironmentVariable(SessionAuthVariable, "false");

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                CreateService(null).SendGuildMsg(Message(customerGuid), new FakeCallContext()));

            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        }

        [Theory]
        [InlineData("not-a-guid")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public async Task RegisterGuild_RejectsMalformedCustomerGuid_AsInvalidArgument(string customerGuid)
        {
            Environment.SetEnvironmentVariable(SessionAuthVariable, "false");

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                CreateService(null).RegisterGuild(Register(customerGuid, "Hero"), null, new FakeCallContext()));

            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        }

        [Fact]
        public async Task SendGuildMsg_ByDefault_RejectsCallWithoutSession()
        {
            Environment.SetEnvironmentVariable(SessionAuthVariable, null);

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                CreateService(null).SendGuildMsg(Message(Customer.ToString()), new FakeCallContext()));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        }

        [Fact]
        public async Task SendGuildMsg_WithSessionAuth_RejectsCallWithoutSession()
        {
            Environment.SetEnvironmentVariable(SessionAuthVariable, "true");

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                CreateService(LiveSession("Hero")).SendGuildMsg(Message(Customer.ToString()), new FakeCallContext()));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        }

        [Fact]
        public async Task SendGuildMsg_WithSessionAuth_RejectsBodyGuidForAnotherCustomer()
        {
            Environment.SetEnvironmentVariable(SessionAuthVariable, "true");

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                CreateService(LiveSession("Hero")).SendGuildMsg(Message(OtherCustomer.ToString()), new FakeCallContext(Customer, Session)));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        }

        [Fact]
        public async Task SendGuildMsg_WithSessionAuth_RejectsUnknownSession()
        {
            Environment.SetEnvironmentVariable(SessionAuthVariable, "true");

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                CreateService(null).SendGuildMsg(Message(Customer.ToString()), new FakeCallContext(Customer, Session)));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        }

        [Fact]
        public async Task SendGuildMsg_WithSessionAuth_AcceptsSessionOfTheSameCustomer()
        {
            Environment.SetEnvironmentVariable(SessionAuthVariable, "true");

            await CreateService(LiveSession("Hero")).SendGuildMsg(Message(Customer.ToString()), new FakeCallContext(Customer, Session));
        }

        [Fact]
        public async Task RegisterGuild_WithSessionAuth_RejectsAnotherCharacter()
        {
            Environment.SetEnvironmentVariable(SessionAuthVariable, "true");

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                CreateService(LiveSession("Hero")).RegisterGuild(Register(Customer.ToString(), "Villain"), null, new FakeCallContext(Customer, Session)));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        }

        private static GuildService CreateService(GetUserSession session)
        {
            var users = DispatchProxy.Create<IUsersRepository, UsersRepositoryStub>();
            ((UsersRepositoryStub)(object)users).Session = session;
            return new GuildService(DispatchProxy.Create<ICharactersRepository, UnusedRepository>(), users, new HeaderCustomerGUID());
        }

        private static GetUserSession LiveSession(string characterName) =>
            new GetUserSession { UserGuid = Guid.NewGuid(), SelectedCharacterName = characterName };

        // MessageTypeInitial matches no case in SendGuildMsg, so an accepted call returns without touching the database.
        private static GuildToSend Message(string customerGuid) =>
            new GuildToSend { CustomerGuid = customerGuid, GuildAction = GuildAction.MessageTypeInitial };

        private static GuildRegister Register(string customerGuid, string userName) =>
            new GuildRegister { CustomerGuid = customerGuid, UserId = "1", UserName = userName };

        public class UsersRepositoryStub : DispatchProxy
        {
            public GetUserSession Session { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IUsersRepository.GetUserSession))
                {
                    return Task.FromResult(Session);
                }

                throw new NotSupportedException(targetMethod.Name);
            }
        }

        public class UnusedRepository : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args) =>
                throw new NotSupportedException(targetMethod.Name);
        }

        private sealed class FakeCallContext : ServerCallContext
        {
            private readonly Metadata _headers = new Metadata();

            public FakeCallContext()
            {
            }

            public FakeCallContext(Guid customerGuid, Guid userSessionGuid)
            {
                _headers.Add(GrpcSessionAuth.CustomerGuidHeader, customerGuid.ToString());
                _headers.Add(GrpcSessionAuth.UserSessionGuidHeader, userSessionGuid.ToString());
            }

            protected override string MethodCore => "/guild.GuildApp/Test";
            protected override string HostCore => "localhost";
            protected override string PeerCore => "ipv4:127.0.0.1:1";
            protected override DateTime DeadlineCore => DateTime.MaxValue;
            protected override Metadata RequestHeadersCore => _headers;
            protected override CancellationToken CancellationTokenCore => CancellationToken.None;
            protected override Metadata ResponseTrailersCore { get; } = new Metadata();
            protected override Status StatusCore { get; set; }
            protected override WriteOptions WriteOptionsCore { get; set; }
            protected override AuthContext AuthContextCore => null;

            protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) =>
                throw new NotSupportedException();

            protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        }
    }
}
