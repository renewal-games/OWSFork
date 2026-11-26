using System;
using System.Threading.Tasks;
using ChatServiceApp.Protos;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OWSChat.Service;
using OWSShared.Grpc;
using Xunit;

namespace OWSTests.Grpc
{
    /// <summary>
    /// The chat service's gRPC receive limit stops a crafted client from pushing megabyte frames into the
    /// stream; without it gRPC's 4 MB default applies. Runs the real service over an in-process HTTP/2 host.
    /// </summary>
    [Collection(SessionAuthEnvironmentCollection.Name)]
    public class ChatMessageSizeTests : IDisposable
    {
        private static readonly Method<ClientMessage, ServerMessage> ChatMethod = new(
            MethodType.DuplexStreaming,
            "Chat.ChatApp",
            "Chat",
            Marshallers.Create(m => m.ToByteArray(), ClientMessage.Parser.ParseFrom),
            Marshallers.Create(m => m.ToByteArray(), ServerMessage.Parser.ParseFrom));

        private readonly string _previous = Environment.GetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable);

        // Auth off: the stream identifies by the first frame's name, so the test needs no session repository.
        public ChatMessageSizeTests() => Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, "false");
        public void Dispose() => Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, _previous);

        [Fact]
        public async Task FrameJustUnderGrpcDefaultLimit_FailsTheStream_WhileNormalChatStillFlows()
        {
            using IHost host = await StartHost();
            using GrpcChannel channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = host.GetTestServer().CreateHandler(),
                MaxReceiveMessageSize = null
            });
            using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(ChatMethod, null, new CallOptions());

            await call.RequestStream.WriteAsync(new ClientMessage { PlayerName = "A" });
            string atLimit = new string('x', ChatService.MaxMessageLength);
            await call.RequestStream.WriteAsync(General(atLimit));
            Assert.True(await call.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(atLimit, call.ResponseStream.Current.Chat.Text);

            // Fits gRPC's default 4,194,304-byte limit, with room for the envelope.
            Task oversizedWrite = call.RequestStream.WriteAsync(General(new string('y', 4 * 1024 * 1024 - 4096)));
            RpcException ex = await Assert.ThrowsAsync<RpcException>(
                () => call.ResponseStream.MoveNext().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
            try
            {
                await oversizedWrite.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // The write may fault or never complete once the server ends the call; only the status matters.
            }
        }

        private static ClientMessage General(string text) => new()
        {
            PlayerName = "A",
            MesssageType = ChatType.MessageTypeGeneral,
            Chat = new ClientMessageChat { Text = text }
        };

        private static async Task<IHost> StartHost()
        {
            IHost host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddGrpc().AddServiceOptions<ChatService>(ChatService.ConfigureGrpc);
                        services.AddSingleton(new ChatService(null, null, new ChatLogWriter(null), null));
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapGrpcService<ChatService>());
                    }))
                .Build();
            await host.StartAsync();
            return host;
        }
    }
}
