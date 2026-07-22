using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChatServiceApp.Protos;
using Grpc.Core;
using OWSShared.Grpc;

namespace OWSChat.Service
{
    public class ChatService : ChatApp.ChatAppBase
    {
        private sealed class Client
        {
            public string Name;
            public Channel<ServerMessage> Outbox = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(128)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        }

        private static readonly ConcurrentDictionary<IServerStreamWriter<ServerMessage>, Client> _clients
            = new ConcurrentDictionary<IServerStreamWriter<ServerMessage>, Client>();

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

            // Session auth is gated off by default (the current client sends no session token);
            // when enabled, the client must supply valid customerguid/usersessionguid metadata.
            if (GrpcSessionAuth.IsRequired &&
                !GrpcSessionAuth.TryReadCredentials(context, out _, out _))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing session credentials."));
            }

            // The first frame identifies the client and is not broadcast as chat.
            if (!await requestStream.MoveNext())
                return;

            var first = requestStream.Current;
            if (string.IsNullOrWhiteSpace(first.PlayerName))
                return;

            var me = new Client { Name = first.PlayerName.Trim() };
            _clients.TryAdd(responseStream, me);

            // Per-connection message rate limit; over-limit messages are dropped, not broadcast.
            GrpcStreamGuard.MessageRateLimiter rateLimiter = GrpcStreamGuard.CreateMessageRateLimiter();

            var writerLoop = Task.Run(async () =>
            {
                try
                {
                    await foreach (var msg in me.Outbox.Reader.ReadAllAsync(context.CancellationToken))
                        await responseStream.WriteAsync(msg);
                }
                catch
                {
                    // Client disconnected or call was cancelled.
                }
            });

            try
            {
                await foreach (var incoming in requestStream.ReadAllAsync(context.CancellationToken))
                {
                    if (!rateLimiter.Allow())
                        continue;

                    await BroadcastFrom(incoming, me);
                }
            }
            finally
            {
                _clients.TryRemove(responseStream, out _);
                me.Outbox.Writer.TryComplete();
                await writerLoop;
            }
        }

        private Task BroadcastFrom(ClientMessage incoming, Client sender)
        {
            if (incoming.Chat == null)
                return Task.CompletedTask;

            var text = incoming.Chat.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return Task.CompletedTask;

            var messageType = incoming.MesssageType;
            var recipientName = incoming.Chat.RecipientName?.Trim();

            if (messageType == ChatType.MessageTypeWhisper && string.IsNullOrWhiteSpace(recipientName))
                return Task.CompletedTask;

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
                if (messageType == ChatType.MessageTypeWhisper)
                {
                    bool toSender = string.Equals(client.Name, sender.Name, StringComparison.Ordinal);
                    bool toTarget = string.Equals(client.Name, recipientName, StringComparison.Ordinal);

                    if (!toSender && !toTarget)
                        continue;
                }

                client.Outbox.Writer.TryWrite(outgoing);
            }

            return Task.CompletedTask;
        }
    }
}
