using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChatServiceApp.Protos;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OWSChat.Service;
using OWSParty.Service;
using OWSShared.Grpc;
using PartyServiceApp.Protos;
using Xunit;

namespace OWSTests.Grpc
{
    /// <summary>
    /// Chat and Party streams must be bound to a live session unless OWS_GRPC_REQUIRE_SESSION_AUTH is
    /// explicitly false; otherwise a caller can claim any character name and speak or join parties as them.
    /// </summary>
    [Collection(SessionAuthEnvironmentCollection.Name)]
    public class GrpcSessionAuthTests : IDisposable
    {
        private readonly string _previous = Environment.GetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, _previous);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("true", true)]
        [InlineData("yes", true)]
        [InlineData("false", false)]
        [InlineData(" FALSE ", false)]
        public void IsRequired_IsOnUnlessExplicitlyFalse(string value, bool expected)
        {
            Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, value);

            Assert.Equal(expected, GrpcSessionAuth.IsRequired);
        }

        [Fact]
        public async Task Chat_RejectsStreamWithoutSession_WhenVariableUnset()
        {
            Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, null);
            var writer = new RecordingStreamWriter<ServerMessage>();

            RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
                NewChatService().Chat(ImpersonationFrames(), writer, new FakeServerCallContext()));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
            Assert.Empty(writer.Written);
        }

        [Fact]
        public async Task Chat_SpeaksAsClaimedName_OnlyWhenExplicitlyDisabled()
        {
            Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, "false");
            var writer = new RecordingStreamWriter<ServerMessage>();

            await NewChatService().Chat(ImpersonationFrames(), writer, new FakeServerCallContext());

            ServerMessage broadcast = Assert.Single(writer.Written);
            Assert.Equal("Victim", broadcast.PlayerName);
        }

        [Fact]
        public async Task Party_RejectsRegistrationWithoutSession_WhenVariableUnset()
        {
            Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, null);
            var service = new PartyService(null, null, null, NullLogger<PartyService>.Instance);
            var request = new PartyRegister { CustomerGuid = Guid.NewGuid().ToString(), UserId = "1", UserName = "Victim" };

            RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
                service.RegisterParty(request, new RecordingStreamWriter<PartyToSend>(), new FakeServerCallContext()));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
            Assert.False(PartyService._partyClients.ContainsKey("Victim"));
        }

        private static ChatService NewChatService() => new ChatService(null, null, new ChatLogWriter(null));

        /// <summary>A first frame claiming another player's name, then one general chat line.</summary>
        private static ListStreamReader<ClientMessage> ImpersonationFrames() => new ListStreamReader<ClientMessage>(
            new ClientMessage { PlayerName = "Victim" },
            new ClientMessage { MesssageType = ChatType.MessageTypeGeneral, Chat = new ClientMessageChat { Text = "hello" } });

        private sealed class ListStreamReader<T> : IAsyncStreamReader<T>
        {
            private readonly Queue<T> _items;

            public ListStreamReader(params T[] items) => _items = new Queue<T>(items);

            public T Current { get; private set; }

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                if (_items.Count == 0)
                    return Task.FromResult(false);

                Current = _items.Dequeue();
                return Task.FromResult(true);
            }
        }

        private sealed class RecordingStreamWriter<T> : IServerStreamWriter<T>
        {
            public List<T> Written { get; } = new List<T>();

            public WriteOptions WriteOptions { get; set; }

            public Task WriteAsync(T message)
            {
                lock (Written)
                    Written.Add(message);
                return Task.CompletedTask;
            }
        }

        /// <summary>A call with no request headers: the client sent no customerguid/usersessionguid.</summary>
        private sealed class FakeServerCallContext : ServerCallContext
        {
            protected override string MethodCore => "/test";
            protected override string HostCore => "localhost";
            protected override string PeerCore => "ipv4:127.0.0.1:1";
            protected override DateTime DeadlineCore => DateTime.MaxValue;
            protected override Metadata RequestHeadersCore { get; } = new Metadata();
            protected override CancellationToken CancellationTokenCore => CancellationToken.None;
            protected override Metadata ResponseTrailersCore { get; } = new Metadata();
            protected override Status StatusCore { get; set; }
            protected override WriteOptions WriteOptionsCore { get; set; }
            protected override AuthContext AuthContextCore => new AuthContext(null, new Dictionary<string, List<AuthProperty>>());

            protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options)
                => throw new NotSupportedException();

            protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        }
    }

    /// <summary>Tests that set OWS_GRPC_REQUIRE_SESSION_AUTH run one at a time: the variable is process-wide.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class SessionAuthEnvironmentCollection
    {
        public const string Name = "OWS_GRPC_REQUIRE_SESSION_AUTH";
    }
}
