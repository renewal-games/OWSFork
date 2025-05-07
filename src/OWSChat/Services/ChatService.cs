using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChatServiceApp.Protos;
using Grpc.Core;


namespace OWSChat.Service
{
    public class ChatService : ChatApp.ChatAppBase
    {
        private class Client
        {
            public string Name;
            public Channel<ServerMessage> Outbox = Channel.CreateUnbounded<ServerMessage>();
        }

        private readonly ConcurrentDictionary<IServerStreamWriter<ServerMessage>, Client> _clients
            = new ConcurrentDictionary<IServerStreamWriter<ServerMessage>, Client>();

        public override async Task Chat(
            IAsyncStreamReader<ClientMessage> requestStream,
            IServerStreamWriter<ServerMessage> responseStream,
            ServerCallContext context)
        {
            // first message must identify user
            if (!await requestStream.MoveNext())
                return;

            var first = requestStream.Current;
            var me = new Client { Name = first.PlayerName };
            _clients.TryAdd(responseStream, me);

            // send the very first message to everyone (including self)
            _ = BroadcastFrom(first, me);

            // pump outgoing queue → writer
            var writerLoop = Task.Run(async () =>
            {
                try
                {
                    await foreach (var msg in me.Outbox.Reader.ReadAllAsync(context.CancellationToken))
                        await responseStream.WriteAsync(msg);
                }
                catch { /* client disconnected */ }
            });

            // pump incoming stream → broadcast
            try
            {
                await foreach (var incoming in requestStream.ReadAllAsync(context.CancellationToken))
                    _ = BroadcastFrom(incoming, me);
            }
            finally
            {
                _clients.TryRemove(responseStream, out _);
                me.Outbox.Writer.Complete();
                await writerLoop;
            }
        }

        private Task BroadcastFrom(ClientMessage incoming, Client sender)
        {
            var outgoing = new ServerMessage
            {
                // note the *double-s* – this matches the proto field name
                MessageType = incoming.MesssageType,

                PlayerName = incoming.PlayerName,
                Chat = new ServerMessageChat          // build the nested object
                {
                    Now = DateTime.UtcNow.ToString("O"),
                    Text = incoming.Chat.Text
                }
            };

            foreach (var kv in _clients.Values)
            {
                bool whisper = incoming.MesssageType == ChatType.MessageTypeWhisper;
                bool toMe = kv.Name == incoming.PlayerName;
                bool toTarget = kv.Name == incoming.Chat.RecipientName;

                if (whisper && !(toMe || toTarget)) continue;

                kv.Outbox.Writer.TryWrite(outgoing);
            }
            return Task.CompletedTask;
        }
    }
}
