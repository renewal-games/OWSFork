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
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task AccountCap_NewestReplacesOldest_LeavesGlobalSlotForAnotherAccount(bool waitingForFirstFrame, bool blockedWrite)
        {
            using var settings = new Settings(("OWS_GRPC_MAX_CONNECTIONS", "3"));
            await using var chat = new ChatHarness();
            var user = Guid.NewGuid();
            var oldest = waitingForFirstFrame ? chat.Start("A", user: user, firstFrame: false) : await chat.Connect("A", user: user);
            oldest.ExpectedFailure = true;
            var second = await chat.Connect("A", user: user);
            if (blockedWrite)
            {
                oldest.BlockWrites = true;
                await second.Send(ChatType.MessageTypeGeneral, "start blocked write");
                await oldest.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var newest = await chat.Connect("A", user: user);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldest.Running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(second.Running.IsCompleted);
            Assert.False(newest.Running.IsCompleted);
            var b = await chat.Connect("B");
            await newest.Send(ChatType.MessageTypeGeneral, "reconnected");
            await chat.Finish();
            Assert.Equal("reconnected", Assert.Single(b.Written).Chat.Text);
            if (blockedWrite) Assert.True(oldest.WriteCancelled);
        }

        [Fact]
        public async Task AccountBudget_IsSharedAcrossStreams_AndSurvivesReconnect()
        {
            using var settings = new Settings(("OWS_GRPC_MSGS_PER_WINDOW", "20"), ("OWS_GRPC_RATE_WINDOW_SECONDS", "60"));
            await using var chat = new ChatHarness();
            var user = Guid.NewGuid();
            var a1 = await chat.Connect("A", user: user);
            var a2 = await chat.Connect("A", user: user);
            var b = await chat.Connect("B");
            for (int i = 0; i < 11; i++)
            {
                await a1.Send(ChatType.MessageTypeGeneral, "one");
                await a2.Send(ChatType.MessageTypeGeneral, "two");
            }
            a1.ExpectedFailure = true;
            var replacement = await chat.Connect("A", user: user);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a1.Running.WaitAsync(TimeSpan.FromSeconds(5)));
            await replacement.Send(ChatType.MessageTypeGeneral, "over budget after eviction");
            a2.Complete();
            replacement.Complete();
            await Task.WhenAll(a2.Running, replacement.Running);
            var reconnected = await chat.Connect("A", user: user);
            await reconnected.Send(ChatType.MessageTypeGeneral, "over budget after reconnect");
            await chat.Finish();
            Assert.Equal(20, b.Written.Count);
            Assert.DoesNotContain(b.Written, m => m.Chat.Text.Contains("over budget"));
        }

        [Fact]
        public async Task FirstFrameTimeout_ReleasesAllAdmissionSlots()
        {
            using var settings = new Settings(("OWS_CHAT_HANDSHAKE_TIMEOUT_SECONDS", "1"),
                ("OWS_GRPC_MAX_CONNECTIONS", "1"), ("OWS_CHAT_MAX_STREAMS_PER_USER", "1"), ("OWS_CHAT_MAX_STREAMS_PER_IP", "1"));
            await using var chat = new ChatHarness();
            var user = Guid.NewGuid();
            var idle = chat.Start("A", user: user, firstFrame: false);
            idle.ExpectedFailure = true;
            var error = await Assert.ThrowsAsync<RpcException>(() => idle.Running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(StatusCode.DeadlineExceeded, error.StatusCode);
            await chat.Connect("A", user: user);
        }

        [Fact]
        public async Task AddressCap_IsAppliedBeforeSessionLookup_AndIgnoresSourcePort()
        {
            using var settings = new Settings(("OWS_CHAT_MAX_STREAMS_PER_IP", "1"));
            await using var chat = new ChatHarness();
            await chat.Connect("A");
            var rejected = new Connection();
            var error = await Assert.ThrowsAsync<RpcException>(() => new ChatService(null, null, null, null)
                .Chat(rejected, rejected, new CallContext(Guid.NewGuid(), Guid.NewGuid(), "ipv4:127.0.0.1:999")));
            Assert.Equal(StatusCode.ResourceExhausted, error.StatusCode);
        }

        private sealed class Settings : IDisposable
        {
            private readonly (string Name, string Value)[] _previous;
            public Settings(params (string Name, string Value)[] values)
            {
                _previous = values.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name))).ToArray();
                foreach (var v in values) Environment.SetEnvironmentVariable(v.Name, v.Value);
            }
            public void Dispose()
            {
                foreach (var v in _previous) Environment.SetEnvironmentVariable(v.Name, v.Value);
            }
        }

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

            public async Task<Connection> Connect(string name, Guid? customer = null, Guid? user = null)
            {
                var connection = Start(name, customer, user);
                await connection.Identified.WaitAsync(TimeSpan.FromSeconds(5));
                return connection;
            }

            public Connection Start(string name, Guid? customer = null, Guid? user = null, bool firstFrame = true)
            {
                Guid session = Guid.NewGuid();
                ((SessionRepository)(object)_users).Sessions[session] = new GetUserSession { SelectedCharacterName = name, UserGuid = user ?? Guid.NewGuid() };
                var connection = new Connection();
                _connections.Add(connection);
                if (firstFrame) connection.Identified = connection.Queue(new ClientMessage { PlayerName = "Spoofed" });
                connection.Running = new ChatService(_users, null, new ChatLogWriter(null), Membership)
                    .Chat(connection, connection, new CallContext(customer ?? Customer, session));
                return connection;
            }

            public async Task Finish()
            {
                foreach (var connection in _connections)
                    connection.Complete();
                await Task.WhenAll(_connections.Select(c => c.ExpectedFailure
                    ? c.Running.ContinueWith(_ => { }) : c.Running)).WaitAsync(TimeSpan.FromSeconds(10));
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
            public bool ExpectedFailure { get; set; }
            public Task Identified { get; set; } = Task.CompletedTask;
            public bool BlockWrites { get; set; }
            public bool WriteCancelled { get; private set; }
            public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            public async Task WriteAsync(ServerMessage message, CancellationToken cancellationToken)
            {
                if (BlockWrites)
                {
                    WriteStarted.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { WriteCancelled = true; throw; }
                }
                await WriteAsync(message);
            }
        }

        private sealed class CallContext : ServerCallContext
        {
            private readonly Metadata _headers = new();
            private readonly string _peer;
            public CallContext(Guid customer, Guid session, string peer = "ipv4:127.0.0.1:1")
            {
                _peer = peer;
                _headers.Add(GrpcSessionAuth.CustomerGuidHeader, customer.ToString());
                _headers.Add(GrpcSessionAuth.UserSessionGuidHeader, session.ToString());
            }
            protected override string MethodCore => "/Chat.ChatApp/Chat";
            protected override string HostCore => "localhost";
            protected override string PeerCore => _peer;
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
