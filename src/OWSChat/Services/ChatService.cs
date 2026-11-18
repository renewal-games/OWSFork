using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChatServiceApp.Protos;
using Grpc.Core;
using Microsoft.Extensions.Options;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Grpc;

namespace OWSChat.Service
{
    public class ChatService : ChatApp.ChatAppBase
    {
        private readonly TimeSpan _recheckInterval;
        private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

        private sealed class Client
        {
            public string Name;
            public Guid? CustomerGuid;
            public Guid? UserGuid;
            public Sanction Mute;
            public Guid SessionGuid;
            public DateTime? ExpiresAt;
            public Channel<ServerMessage> Outbox = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(128)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        }

        private static readonly ConcurrentDictionary<IServerStreamWriter<ServerMessage>, Client> _clients
            = new ConcurrentDictionary<IServerStreamWriter<ServerMessage>, Client>();

        private readonly IUsersRepository _usersRepository;
        private readonly ISanctionsRepository _sanctionsRepository;
        private readonly ChatLogWriter _chatLog;
        private readonly IChatMembershipRepository _membershipRepository;

        public ChatService(IUsersRepository usersRepository, ISanctionsRepository sanctionsRepository, ChatLogWriter chatLog,
            IChatMembershipRepository membershipRepository, IOptions<ChatSessionOptions> options = null)
        {
            _usersRepository = usersRepository;
            _sanctionsRepository = sanctionsRepository;
            _chatLog = chatLog;
            _membershipRepository = membershipRepository;
            _recheckInterval = (options?.Value ?? new ChatSessionOptions()).RecheckInterval;
            if (_recheckInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        public override async Task Chat(
            IAsyncStreamReader<ClientMessage> requestStream,
            IServerStreamWriter<ServerMessage> responseStream,
            ServerCallContext context)
        {
            // Bound the number of concurrent chat streams so a flood of connections can't
            // exhaust memory (each stream holds a bounded outbox channel and a writer task).
            using GrpcStreamGuard.ConnectionLease lease = GrpcStreamGuard.TryAcquireConnection();
            if (lease == null)
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "Chat connection limit reached."));

            // With session auth on, the stream speaks as the session's character, not the name the client sends.
            string sessionCharacterName = null;
            GetUserSession session = null;
            Guid customerGuid = Guid.Empty;
            if (GrpcSessionAuth.IsRequired)
            {
                (session, customerGuid) = await GetSession(context);
                sessionCharacterName = session?.SelectedCharacterName?.Trim();
                if (string.IsNullOrEmpty(sessionCharacterName))
                {
                    GrpcSessionAuth.LogRejected(context, "chat");
                    throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid session for chat."));
                }
            }

            // The first frame identifies the client and is not broadcast as chat.
            if (!await requestStream.MoveNext())
                return;

            var first = requestStream.Current;
            if (string.IsNullOrWhiteSpace(first.PlayerName))
                return;

            var me = new Client
            {
                Name = sessionCharacterName ?? first.PlayerName.Trim(),
                CustomerGuid = session != null ? customerGuid : null,
                UserGuid = session?.UserGuid,
                ExpiresAt = session?.ExpiresAt
            };
            if (session != null)
            {
                GrpcSessionAuth.TryReadCredentials(context, out _, out me.SessionGuid);
                await ValidateSession(me, context.CancellationToken);
            }
            _clients.TryAdd(responseStream, me);

            // Per-connection message rate limit; over-limit messages are dropped, not broadcast.
            GrpcStreamGuard.MessageRateLimiter rateLimiter = GrpcStreamGuard.CreateMessageRateLimiter();

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            Task validationLoop = session == null ? Task.CompletedTask : Revalidate(me, stop);
            var writerLoop = Task.Run(async () =>
            {
                try
                {
                    await foreach (var msg in me.Outbox.Reader.ReadAllAsync(stop.Token))
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        await responseStream.WriteAsync(msg).WaitAsync(stop.Token);
                    }
                }
                catch
                {
                    // Client disconnected or call was cancelled.
                }
            });

            try
            {
                await foreach (var incoming in requestStream.ReadAllAsync(stop.Token))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (!rateLimiter.Allow())
                        continue;

                    await BroadcastFrom(incoming, me, stop.Token);
                }
            }
            catch (OperationCanceledException) when (validationLoop.IsFaulted)
            {
                await validationLoop;
            }
            finally
            {
                _clients.TryRemove(responseStream, out _);
                me.Outbox.Writer.TryComplete();
                await writerLoop;
                stop.Cancel();
                try { await validationLoop; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }

        private async Task<(GetUserSession Session, Guid CustomerGuid)> GetSession(ServerCallContext context)
        {
            if (!GrpcSessionAuth.TryReadCredentials(context, out Guid customerGuid, out Guid userSessionGuid))
                return (null, Guid.Empty);

            var session = await _usersRepository.GetUserSession(customerGuid, userSessionGuid);
            if (session == null || !session.UserGuid.HasValue || session.ExpiresAt <= DateTime.UtcNow)
                return (null, Guid.Empty);

            return (session, customerGuid);
        }

        private async Task ValidateSession(Client client, CancellationToken cancellationToken)
        {
            var session = await _usersRepository.GetUserSession(client.CustomerGuid.Value, client.SessionGuid)
                .WaitAsync(LookupTimeout, cancellationToken);
            if (session == null || session.ExpiresAt <= DateTime.UtcNow || session.UserGuid != client.UserGuid ||
                !string.Equals(session.SelectedCharacterName?.Trim(), client.Name, StringComparison.Ordinal))
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid session for chat."));

            var sanctions = await _sanctionsRepository.GetActive(client.CustomerGuid.Value, client.UserGuid.Value)
                .WaitAsync(LookupTimeout, cancellationToken);
            if (sanctions.Ban != null)
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid session for chat."));
            client.ExpiresAt = session.ExpiresAt;
            client.Mute = sanctions.Mute;
        }

        // Runs independently of incoming messages so idle listeners lose access as well.
        private async Task Revalidate(Client client, CancellationTokenSource stop)
        {
            int failures = 0;
            try
            {
                while (true)
                {
                    var delay = _recheckInterval;
                    if (client.ExpiresAt.HasValue)
                    {
                        var remaining = client.ExpiresAt.Value - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                            throw new RpcException(new Status(StatusCode.Unauthenticated, "Chat session expired."));
                        if (remaining < delay) delay = remaining;
                    }
                    await Task.Delay(delay, stop.Token);
                    if (client.ExpiresAt <= DateTime.UtcNow)
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Chat session expired."));
                    try
                    {
                        await ValidateSession(client, stop.Token);
                        failures = 0;
                    }
                    catch (Exception ex) when (ex is not RpcException && ex is not OperationCanceledException)
                    {
                        // Bound access during a lookup outage; a hung lookup also counts as a failure.
                        if (++failures >= 3)
                            throw new RpcException(new Status(StatusCode.Unauthenticated, "Unable to verify chat session."));
                    }
                }
            }
            catch
            {
                stop.Cancel();
                throw;
            }
        }

        private static Sanction GetActiveMute(Client sender)
        {
            Sanction mute = sender.Mute;
            return mute != null && (!mute.ExpiresAt.HasValue || mute.ExpiresAt.Value > DateTime.UtcNow) ? mute : null;
        }

        private async Task BroadcastFrom(ClientMessage incoming, Client sender, CancellationToken cancellationToken)
        {
            if (incoming.Chat == null)
                return;

            var text = incoming.Chat.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            var messageType = incoming.MesssageType;
            var recipientName = incoming.Chat.RecipientName?.Trim();

            if (messageType == ChatType.MessageTypeWhisper && string.IsNullOrWhiteSpace(recipientName))
                return;

            Sanction mute = GetActiveMute(sender);
            if (mute != null)
            {
                LogChat(sender, messageType, recipientName, text, dropped: true);
                sender.Outbox.Writer.TryWrite(new ServerMessage
                {
                    MessageType = ChatType.MessageTypeGeneral,
                    PlayerName = "[Server]",
                    Chat = new ServerMessageChat { Now = DateTime.UtcNow.ToString("O"), Text = mute.PlayerMessage() }
                });
                return;
            }

            HashSet<string> groupMembers = null;
            if (messageType == ChatType.MessageTypeParty || messageType == ChatType.MessageTypeGuild)
            {
                string group = messageType == ChatType.MessageTypeParty ? "party" : "guild";
                string rejection = null;
                if (!sender.CustomerGuid.HasValue)
                {
                    // Auth-disabled development streams have no trusted tenant/character identity.
                    rejection = $"Sign in to use {group} chat.";
                }
                else
                {
                    // Resolve sender and recipients together for every send: never retain stale membership
                    // on a long-lived stream after a leave, kick, disband or character joining another group.
                    try
                    {
                        var members = messageType == ChatType.MessageTypeParty
                            ? await _membershipRepository.GetPartyMembers(sender.CustomerGuid.Value, sender.Name)
                            : await _membershipRepository.GetGuildMembers(sender.CustomerGuid.Value, sender.Name);
                        groupMembers = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
                        if (!groupMembers.Contains(sender.Name))
                            rejection = $"You're not in a {group}.";
                    }
                    catch (Exception)
                    {
                        // Fail closed when membership cannot be verified; never fall back to a broadcast.
                        rejection = $"Unable to verify {group} membership. Please try again.";
                    }
                }

                if (rejection != null)
                {
                    LogChat(sender, messageType, recipientName, text, dropped: true);
                    sender.Outbox.Writer.TryWrite(new ServerMessage
                    {
                        MessageType = messageType,
                        PlayerName = "[Server]",
                        Chat = new ServerMessageChat { Now = DateTime.UtcNow.ToString("O"), Text = rejection }
                    });
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            LogChat(sender, messageType, recipientName, text, dropped: false);
            var outgoing = new ServerMessage
            {
                MessageType = messageType,
                PlayerName = sender.Name,
                Chat = new ServerMessageChat
                {
                    Now = DateTime.UtcNow.ToString("O"),
                    Text = text
                }
            };

            foreach (var client in _clients.Values)
            {
                if (groupMembers != null && (client.CustomerGuid != sender.CustomerGuid || !groupMembers.Contains(client.Name)))
                    continue;

                if (messageType == ChatType.MessageTypeWhisper)
                {
                    bool toSender = string.Equals(client.Name, sender.Name, StringComparison.Ordinal);
                    bool toTarget = string.Equals(client.Name, recipientName, StringComparison.Ordinal);

                    if (!toSender && !toTarget)
                        continue;
                }

                client.Outbox.Writer.TryWrite(outgoing);
            }
        }

        private void LogChat(Client sender, ChatType messageType, string recipientName, string text, bool dropped)
        {
            if (!sender.CustomerGuid.HasValue)
                return;

            _chatLog.Enqueue(new ChatLogEntry
            {
                CustomerGUID = sender.CustomerGuid.Value,
                CreateDate = DateTime.UtcNow,
                Channel = messageType switch
                {
                    ChatType.MessageTypeTrade => "trade",
                    ChatType.MessageTypeGuild => "guild",
                    ChatType.MessageTypeParty => "party",
                    ChatType.MessageTypeWhisper => "whisper",
                    _ => "general"
                },
                SenderName = sender.Name,
                SenderUserGUID = sender.UserGuid,
                TargetName = messageType == ChatType.MessageTypeWhisper ? recipientName : null,
                Message = text.Length > 2000 ? text.Substring(0, 2000) : text,
                Dropped = dropped
            });
        }
    }
}
