using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OWSData.Repositories.Implementations.InMemory;
using OWSInstanceLauncher.Services;
using OWSShared.Interfaces;
using OWSShared.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;
using FakeModel = OWSTests.InstanceLauncher.LauncherCommandDeliveryTests.FakeModel;

namespace OWSTests.InstanceLauncher
{
    /// <summary>
    /// A launcher that starts while RabbitMQ is unreachable keeps heartbeating, so Instance Management keeps routing spin-ups
    /// to it. It must keep trying to connect until it is consuming both of its command queues, without needing a restart.
    /// TimedHostedService calls DoWork again every 10 seconds until a call returns without throwing.
    /// </summary>
    public class LauncherBrokerConnectTests
    {
        private const int WorldServerId = 7;

        [Fact]
        public void DoWork_RetriesUntilRabbitMQIsReachable_ThenConsumesBothQueues()
        {
            var broker = new FakeBroker { UnreachableAttempts = 1 };
            ServerLauncherMQListener listener = Listener(broker);

            //Building the listener does not depend on the broker.
            Assert.Equal(0, broker.ConnectAttempts);

            //The broker is down: the call throws, which keeps TimedHostedService retrying.
            Assert.ThrowsAny<Exception>(listener.DoWork);
            Assert.Empty(broker.Models);

            //The broker is back.
            listener.DoWork();

            Assert.Equal(2, broker.ConnectAttempts);
            Assert.Equal(new[] { "ows.servershutdown.7.launcher", "ows.serverspinup.7.launcher" },
                broker.Models.SelectMany(model => model.Consumers).Select(consumer => consumer.Queue).OrderBy(queue => queue));
            Assert.All(broker.Models.SelectMany(model => model.Consumers), consumer => Assert.False(consumer.AutoAck));

            //A later call, should one still run, neither reconnects nor adds consumers.
            listener.DoWork();

            Assert.Equal(2, broker.ConnectAttempts);
            Assert.Equal(2, broker.Models.SelectMany(model => model.Consumers).Count());
        }

        [Fact]
        public void DoWork_ThatFailsHalfwayThrough_AbortsTheConnection_SoTheRetryDoesNotDoubleTheConsumers()
        {
            //Connects, then the broker goes away before the shut-down channel is open.
            var broker = new FakeBroker { FailCreateModelNumber = 2 };
            ServerLauncherMQListener listener = Listener(broker);

            Assert.ThrowsAny<Exception>(listener.DoWork);
            Assert.Equal(1, broker.Aborts);

            listener.DoWork();

            Assert.Equal(2, broker.ConnectAttempts);
            Assert.Equal(1, broker.Aborts);
            //Only the channels of the second connection have consumers: one per queue.
            List<FakeModel> secondConnection = broker.Models.Skip(1).ToList();
            Assert.Equal(new[] { "ows.servershutdown.7.launcher", "ows.serverspinup.7.launcher" },
                secondConnection.SelectMany(model => model.Consumers).Select(consumer => consumer.Queue).OrderBy(queue => queue));
            Assert.Empty(broker.Models[0].Consumers);
        }

        private static ServerLauncherMQListener Listener(FakeBroker broker)
        {
            var options = new OWSInstanceLauncherOptions
            {
                OWSAPIKey = Guid.NewGuid().ToString(),
                LauncherGuid = Guid.NewGuid().ToString(),
                MaxNumberOfInstances = 10
            };

            var launcherData = new OWSInstanceLauncherDataRepository();
            launcherData.SetWorldServerID(WorldServerId);

            return new ServerLauncherMQListener(new FixedWritableOptions(options), Microsoft.Extensions.Options.Options.Create(new APIPathOptions()),
                new OkHttpClientFactory(), new ZoneServerProcessesRepository(), launcherData, broker.Factory);
        }

        /// <summary>An IConnectionFactory whose connections hand out FakeModels; it can refuse connections or fail a channel.</summary>
        private sealed class FakeBroker
        {
            public int UnreachableAttempts { get; set; }
            public int FailCreateModelNumber { get; set; }
            public int ConnectAttempts { get; private set; }
            public int Aborts { get; private set; }
            public List<FakeModel> Models { get; } = new();

            private int _createModelCalls;

            public IConnectionFactory Factory => OWSTests.PublicAPI.GetServerToConnectToRequestTests.Stub<IConnectionFactory>.Create(new()
            {
                [nameof(IConnectionFactory.CreateConnection)] = _ => Connect()
            });

            private IConnection Connect()
            {
                ConnectAttempts++;
                if (ConnectAttempts <= UnreachableAttempts)
                {
                    throw new BrokerUnreachableException(new InvalidOperationException("connection refused"));
                }

                return OWSTests.PublicAPI.GetServerToConnectToRequestTests.Stub<IConnection>.Create(new()
                {
                    [nameof(IConnection.CreateModel)] = _ => CreateModel(),
                    ["add_" + nameof(IConnection.ConnectionShutdown)] = _ => null,
                    [nameof(IConnection.Abort)] = _ => { Aborts++; return null; }
                });
            }

            private IModel CreateModel()
            {
                if (++_createModelCalls == FailCreateModelNumber)
                {
                    throw new InvalidOperationException("connection lost");
                }

                FakeModel model = FakeModel.Create();
                Models.Add(model);
                return model.Model;
            }
        }

        private sealed class FixedWritableOptions : IWritableOptions<OWSInstanceLauncherOptions>
        {
            public FixedWritableOptions(OWSInstanceLauncherOptions value)
            {
                Value = value;
            }

            public OWSInstanceLauncherOptions Value { get; }

            public void Update(Action<OWSInstanceLauncherOptions> applyChanges) => applyChanges(Value);
        }

        //Instance Management accepts the launcher registration.
        private sealed class OkHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new HttpClient(new OkHandler()) { BaseAddress = new Uri("http://localhost/") };

            private sealed class OkHandler : HttpMessageHandler
            {
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
                }
            }
        }
    }
}
