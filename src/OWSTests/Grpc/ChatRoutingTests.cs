using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChatServiceApp.Protos;
using Grpc.Core;
using OWSChat.Service;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Grpc;
using Xunit;

namespace OWSTests.Grpc
{
    [Collection(SessionAuthEnvironmentCollection.Name)]
    public class ChatRoutingTests : IDisposable
    {
        private readonly string _previous = Environment.GetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable);

        public ChatRoutingTests() => Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, "true");
        public void Dispose() => Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, _previous);

        [Theory]
        [InlineData(ChatType.MessageTypeParty)]
        [InlineData(ChatType.MessageTypeGuild)]
        public async Task GroupChat_ReachesOnlyCurrentMembersOfSendersTenant_AndRejectsSoloSender(ChatType type)
        {
            await using var chat = new ChatHarness();
            var a = await chat.Connect("A");
            var b = await chat.Connect("B");
            var solo = await chat.Connect("C");
            var otherGroup = await chat.Connect("D");
            var otherTenant = await chat.Connect("B", Guid.NewGuid());
            chat.Membership.Set(type, chat.Customer, "A", "A", "b");
            chat.Membership.Set(type, chat.Customer, "D", "D");

            await a.Send(type, "private");
            await solo.Send(type, "must not broadcast");
            await chat.Finish();

            Assert.Equal("private", Assert.Single(a.Written).Chat.Text);
            var delivered = Assert.Single(b.Written);
            Assert.Equal(type, delivered.MessageType);
            Assert.Equal("A", delivered.PlayerName); // session identity, not the frame's spoofed name
            Assert.Equal("private", delivered.Chat.Text);
            Assert.Empty(otherGroup.Written);
            Assert.Empty(otherTenant.Written);
            var notice = Assert.Single(solo.Written);
            Assert.Equal("[Server]", notice.PlayerName);
            Assert.Equal(type, notice.MessageType);
            Assert.Contains(type == ChatType.MessageTypeParty ? "not in a party" : "not in a guild", notice.Chat.Text);
        }

        [Theory]
        [InlineData(ChatType.MessageTypeParty)]
        [InlineData(ChatType.MessageTypeGuild)]
        public async Task GroupChat_RechecksMembershipAfterRecipientAndSenderLeave(ChatType type)
        {
            await using var chat = new ChatHarness();
            var a = await chat.Connect("A");
            var b = await chat.Connect("B");
            chat.Membership.Set(type, chat.Customer, "A", "A", "B");
            await a.Send(type, "before leave");
            chat.Membership.Set(type, chat.Customer, "A", "A");
            await a.Send(type, "after B leaves");
            chat.Membership.Set(type, chat.Customer, "A");
            await a.Send(type, "after A leaves");
            await chat.Finish();

            Assert.Equal("before leave", Assert.Single(b.Written).Chat.Text);
            Assert.Equal(new[] { "before leave", "after B leaves" }, a.Written.Where(m => m.PlayerName == "A").Select(m => m.Chat.Text));
            Assert.Equal("[Server]", a.Written.Last().PlayerName);
        }

        [Theory]
        [InlineData(ChatType.MessageTypeParty)]
        [InlineData(ChatType.MessageTypeGuild)]
        public async Task GroupChat_FailsClosedWhenLookupFails(ChatType type)
        {
            await using var chat = new ChatHarness();
            var a = await chat.Connect("A");
            var b = await chat.Connect("B");
            chat.Membership.Failure = true;
            await a.Send(type, "must not broadcast");
            await chat.Finish();

            Assert.Empty(b.Written);
            Assert.Equal("[Server]", Assert.Single(a.Written).PlayerName);
            Assert.Contains("Unable to verify", a.Written[0].Chat.Text);
        }

        [Theory]
        [InlineData(ChatType.MessageTypeParty)]
        [InlineData(ChatType.MessageTypeGuild)]
        public async Task GroupChat_RejectsUntrustedStreamsWhenSessionAuthIsDisabled(ChatType type)
        {
            Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, "false");
            await using var chat = new ChatHarness();
            var a = await chat.Connect("A");
            var b = await chat.Connect("B");
            await a.Send(type, "must not broadcast");
            await chat.Finish();

            Assert.Empty(b.Written);
            Assert.Equal("[Server]", Assert.Single(a.Written).PlayerName);
            Assert.Contains("Sign in", a.Written[0].Chat.Text);
        }

        [Theory]
        [InlineData(ChatType.MessageTypeGeneral)]
        [InlineData(ChatType.MessageTypeTrade)]
        [InlineData(ChatType.MessageTypeWhisper)]
        public async Task PublicAndWhisperRoutingStillWorks(ChatType type)
        {
            await using var chat = new ChatHarness();
            var a = await chat.Connect("A");
            var b = await chat.Connect("B");
            var c = await chat.Connect("C");
            await a.Send(type, "hello", "B");
            await chat.Finish();

            Assert.Equal("hello", Assert.Single(a.Written).Chat.Text);
            Assert.Equal("hello", Assert.Single(b.Written).Chat.Text);
            Assert.Equal(type == ChatType.MessageTypeWhisper ? 0 : 1, c.Written.Count);
        }

        private sealed class ChatHarness : IAsyncDisposable
        {
            public Guid Customer { get; } = Guid.NewGuid();
            public MembershipRepository Membership { get; } = new();
            private readonly List<Connection> _connections = new();
            private readonly IUsersRepository _users = DispatchProxy.Create<IUsersRepository, SessionRepository>();

            public async Task<Connection> Connect(string name, Guid? customer = null)
            {
                Guid session = Guid.NewGuid();
                ((SessionRepository)(object)_users).Sessions[session] = new GetUserSession { SelectedCharacterName = name, UserGuid = Guid.NewGuid() };
                var connection = new Connection();
                _connections.Add(connection);
                Task identified = connection.Queue(new ClientMessage { PlayerName = "Spoofed" });
                connection.Running = new ChatService(_users, null, new ChatLogWriter(null), Membership)
                    .Chat(connection, connection, new CallContext(customer ?? Customer, session));
                await identified.WaitAsync(TimeSpan.FromSeconds(5));
                return connection;
            }

            public async Task Finish()
            {
                foreach (var connection in _connections)
                    connection.Complete();
                await Task.WhenAll(_connections.Select(c => c.Running)).WaitAsync(TimeSpan.FromSeconds(10));
            }

            public async ValueTask DisposeAsync() => await Finish();
        }

        private sealed class MembershipRepository : IChatMembershipRepository
        {
            private readonly Dictionary<(ChatType, Guid, string), string[]> _members = new();
            public bool Failure { get; set; }
            public void Set(ChatType type, Guid customer, string sender, params string[] members) => _members[(type, customer, sender)] = members;
            public Task<IEnumerable<string>> GetPartyMembers(Guid customerGUID, string characterName) => Get(ChatType.MessageTypeParty, customerGUID, characterName);
            public Task<IEnumerable<string>> GetGuildMembers(Guid customerGUID, string characterName) => Get(ChatType.MessageTypeGuild, customerGUID, characterName);
            private Task<IEnumerable<string>> Get(ChatType type, Guid customer, string name)
            {
                if (Failure) throw new InvalidOperationException("database unavailable");
                return Task.FromResult<IEnumerable<string>>(_members.GetValueOrDefault((type, customer, name), Array.Empty<string>()));
            }
        }

        public class SessionRepository : DispatchProxy
        {
            public Dictionary<Guid, GetUserSession> Sessions { get; } = new();
            protected override object Invoke(MethodInfo method, object[] args) => method.Name == nameof(IUsersRepository.GetUserSession)
                ? Task.FromResult(Sessions[(Guid)args[1]]) : throw new NotSupportedException(method.Name);
        }

        // Acknowledge each frame on the next MoveNext, after Chat has processed it. Completing all
        // streams then drains every outbox, so absence assertions need no sleeps or timing guesses.
        private sealed class Connection : IAsyncStreamReader<ClientMessage>, IServerStreamWriter<ServerMessage>
        {
            private readonly Channel<(ClientMessage Message, TaskCompletionSource Processed)> _input = Channel.CreateUnbounded<(ClientMessage, TaskCompletionSource)>();
            private TaskCompletionSource _processed;
            public List<ServerMessage> Written { get; } = new();
            public Task Running { get; set; } = Task.CompletedTask;
            public ClientMessage Current { get; private set; }
            public WriteOptions WriteOptions { get; set; }
            public Task Queue(ClientMessage message)
            {
                var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _input.Writer.TryWrite((message, processed));
                return processed.Task;
            }
            public Task Send(ChatType type, string text, string recipient = "") => Queue(new ClientMessage
            {
                PlayerName = "Spoofed", MesssageType = type, Chat = new ClientMessageChat { Text = text, RecipientName = recipient }
            }).WaitAsync(TimeSpan.FromSeconds(5));
            public void Complete() => _input.Writer.TryComplete();
            public async Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                _processed?.TrySetResult();
                if (!await _input.Reader.WaitToReadAsync(cancellationToken)) return false;
                var item = await _input.Reader.ReadAsync(cancellationToken);
                Current = item.Message;
                _processed = item.Processed;
                return true;
            }
            public Task WriteAsync(ServerMessage message)
            {
                Written.Add(message);
                return Task.CompletedTask;
            }
        }

        private sealed class CallContext : ServerCallContext
        {
            private readonly Metadata _headers = new();
            public CallContext(Guid customer, Guid session)
            {
                _headers.Add(GrpcSessionAuth.CustomerGuidHeader, customer.ToString());
                _headers.Add(GrpcSessionAuth.UserSessionGuidHeader, session.ToString());
            }
            protected override string MethodCore => "/Chat.ChatApp/Chat";
            protected override string HostCore => "localhost";
            protected override string PeerCore => "ipv4:127.0.0.1:1";
            protected override DateTime DeadlineCore => DateTime.MaxValue;
            protected override Metadata RequestHeadersCore => _headers;
            protected override CancellationToken CancellationTokenCore => CancellationToken.None;
            protected override Metadata ResponseTrailersCore { get; } = new();
            protected override Status StatusCore { get; set; }
            protected override WriteOptions WriteOptionsCore { get; set; }
            protected override AuthContext AuthContextCore => null;
            protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) => throw new NotSupportedException();
            protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        }
    }
}
