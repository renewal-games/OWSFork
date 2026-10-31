using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OWSData.Models.Composites;
using OWSData.Repositories.Implementations.InMemory;
using OWSData.Repositories.Interfaces;
using OWSInstanceLauncher.Services;
using OWSInstanceManagement.Requests.Instance;
using OWSShared.Interfaces;
using OWSShared.Messages;
using OWSShared.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace OWSTests.InstanceLauncher
{
    /// <summary>
    /// Spin-up and shut-down commands must not be lost between Instance Management and the launcher: the publisher only
    /// reports success once RabbitMQ confirmed the command reached a launcher queue, and the launcher only acks a command
    /// after handling it, so one it dies on is delivered again, without starting a second server.
    /// </summary>
    public class LauncherCommandDeliveryTests
    {
        private static readonly byte[] Body = { 1, 2, 3 };

        [Fact]
        public void Publish_IsPersistentMandatoryAndConfirmed()
        {
            FakeModel model = FakeModel.Create();

            LauncherCommandChannel.Publish(model.Model, LauncherCommandChannel.SpinUpExchange, 7, Body);

            Assert.True(model.ConfirmSelected);
            FakeModel.Published published = Assert.Single(model.Publishes);
            Assert.Equal("ows.serverspinup", published.Exchange);
            Assert.Equal("ows.serverspinup.7", published.RoutingKey);
            Assert.True(published.Mandatory);
            Assert.True(published.Properties.Persistent);
            Assert.True(model.WaitedForConfirms);
            Assert.Empty(model.ReturnHandlers);
        }

        [Fact]
        public void Publish_ThrowsWhenNoLauncherQueueIsBound()
        {
            FakeModel model = FakeModel.Create();
            model.ReturnUnroutable = true;

            var ex = Assert.Throws<LauncherCommandNotDeliveredException>(
                () => LauncherCommandChannel.Publish(model.Model, LauncherCommandChannel.ShutDownExchange, 7, Body));

            Assert.Contains("World Server 7", ex.Message);
            Assert.Empty(model.ReturnHandlers);
        }

        [Fact]
        public void Publish_ThrowsWhenTheBrokerDoesNotConfirm()
        {
            FakeModel model = FakeModel.Create();
            model.ConfirmFailure = new TimeoutException("no confirm");

            Assert.Throws<LauncherCommandNotDeliveredException>(
                () => LauncherCommandChannel.Publish(model.Model, LauncherCommandChannel.SpinUpExchange, 7, Body));
        }

        [Fact]
        public void DeclareLauncherQueue_IsDurableSharedAndBound()
        {
            FakeModel model = FakeModel.Create();

            string queue = LauncherCommandChannel.DeclareLauncherQueue(model.Model, LauncherCommandChannel.SpinUpExchange, 7);

            Assert.Equal("ows.serverspinup.7.launcher", queue);
            FakeModel.DeclaredQueue declared = Assert.Single(model.Queues);
            Assert.True(declared.Durable);
            Assert.False(declared.Exclusive);
            Assert.False(declared.AutoDelete);
            Assert.Equal(("ows.serverspinup.7.launcher", "ows.serverspinup", "ows.serverspinup.7"), Assert.Single(model.Bindings));
        }

        [Fact]
        public async Task SpinUpServerInstance_ReportsFailureWhenThePublishIsNotConfirmed()
        {
            var request = new RecordingSpinUpRequest { WorldServerID = 7, ZoneInstanceID = 42, ZoneName = "Map", Port = 7777, PublishFails = true };
            request.SetData(Microsoft.Extensions.Options.Options.Create(new RabbitMQOptions()), null, new FixedCustomerGuid());

            var result = Assert.IsType<OkObjectResult>(await request.Handle());

            var output = Assert.IsType<SuccessAndErrorMessage>(result.Value);
            Assert.False(output.Success);
            Assert.Contains("42", output.ErrorMessage);
        }

        [Fact]
        public async Task SpinUpServerInstance_ReportsSuccessOnceConfirmed()
        {
            var request = new RecordingSpinUpRequest { WorldServerID = 7, ZoneInstanceID = 42, ZoneName = "Map", Port = 7777 };
            request.SetData(Microsoft.Extensions.Options.Options.Create(new RabbitMQOptions()), null, new FixedCustomerGuid());

            var result = Assert.IsType<OkObjectResult>(await request.Handle());

            Assert.True(Assert.IsType<SuccessAndErrorMessage>(result.Value).Success);
            MQSpinUpServerMessage published = Assert.Single(request.Published);
            Assert.Equal(42, published.ZoneInstanceID);
            Assert.Equal(7, published.WorldServerID);
        }

        [Fact]
        public async Task ProcessDelivery_AcksOnlyAfterTheHandlerFinishes()
        {
            FakeModel model = FakeModel.Create();
            var handlerDone = new TaskCompletionSource();

            Task processing = ServerLauncherMQListener.ProcessDelivery(model.Model, Delivery(5, redelivered: false), "spin-up", () => handlerDone.Task);

            //A launcher that dies here never acked, so the broker keeps the command for the next consumer.
            Assert.Empty(model.Acks);

            handlerDone.SetResult();
            await processing;

            Assert.Equal(new ulong[] { 5 }, model.Acks);
            Assert.Empty(model.Nacks);
        }

        [Fact]
        public async Task ProcessDelivery_RequeuesOnceThenDrops()
        {
            FakeModel model = FakeModel.Create();
            Func<Task> failing = () => throw new InvalidOperationException("boom");

            await ServerLauncherMQListener.ProcessDelivery(model.Model, Delivery(5, redelivered: false), "spin-up", failing);
            await ServerLauncherMQListener.ProcessDelivery(model.Model, Delivery(6, redelivered: true), "spin-up", failing);

            Assert.Empty(model.Acks);
            Assert.Equal(new[] { (5UL, true), (6UL, false) }, model.Nacks);
        }

        [Fact]
        public async Task SpinUp_InterruptedBeforeStart_IsRedeliveredAndStartsExactlyOneServer()
        {
            FakeModel model = FakeModel.Create();
            IZoneServerProcessesRepository processes = new ZoneServerProcessesRepository();
            var liveProcessIds = new HashSet<int>();
            int started = 0;
            bool crashBeforeStart = true;

            //Mirrors HandleServerSpinUpMessage: skip a zone instance that already runs, otherwise start and track it.
            Func<Task> spinUp = () =>
            {
                if (ServerLauncherMQListener.IsZoneInstanceAlreadyRunning(processes, 42, pid => !liveProcessIds.Contains(pid)))
                {
                    return Task.CompletedTask;
                }

                if (crashBeforeStart)
                {
                    crashBeforeStart = false;
                    throw new InvalidOperationException("launcher went away before Process.Start");
                }

                started++;
                liveProcessIds.Add(1000 + started);
                processes.AddZoneServerProcess(new ZoneServerProcess { ZoneInstanceId = 42, ProcessId = 1000 + started });
                return Task.CompletedTask;
            };

            //First delivery dies before the server starts: not acked, back in the queue.
            await ServerLauncherMQListener.ProcessDelivery(model.Model, Delivery(1, redelivered: false), "spin-up", spinUp);
            Assert.Equal(0, started);
            Assert.Equal(new[] { (1UL, true) }, model.Nacks);

            //Redelivery starts the server.
            await ServerLauncherMQListener.ProcessDelivery(model.Model, Delivery(2, redelivered: true), "spin-up", spinUp);
            Assert.Equal(1, started);

            //Delivered again (the launcher died after starting but before acking): no second server.
            await ServerLauncherMQListener.ProcessDelivery(model.Model, Delivery(3, redelivered: true), "spin-up", spinUp);
            Assert.Equal(1, started);
            Assert.Equal(new ulong[] { 2, 3 }, model.Acks);
        }

        [Fact]
        public void IsZoneInstanceAlreadyRunning_IgnoresExitedProcesses()
        {
            IZoneServerProcessesRepository processes = new ZoneServerProcessesRepository();
            processes.AddZoneServerProcess(new ZoneServerProcess { ZoneInstanceId = 42, ProcessId = 1234 });

            Assert.True(ServerLauncherMQListener.IsZoneInstanceAlreadyRunning(processes, 42, _ => false));
            Assert.False(ServerLauncherMQListener.IsZoneInstanceAlreadyRunning(processes, 42, _ => true));
            Assert.False(ServerLauncherMQListener.IsZoneInstanceAlreadyRunning(processes, 43, _ => false));
        }

        private static BasicDeliverEventArgs Delivery(ulong deliveryTag, bool redelivered)
        {
            return new BasicDeliverEventArgs { DeliveryTag = deliveryTag, Redelivered = redelivered, Body = Body };
        }

        private sealed class FixedCustomerGuid : IHeaderCustomerGUID
        {
            public Guid CustomerGUID { get; set; } = Guid.NewGuid();
        }

        private sealed class RecordingSpinUpRequest : SpinUpServerInstanceRequest
        {
            public bool PublishFails { get; set; }

            public List<MQSpinUpServerMessage> Published { get; } = new();

            protected override void PublishSpinUpMessage(MQSpinUpServerMessage serverSpinUpMessage)
            {
                if (PublishFails)
                {
                    throw new LauncherCommandNotDeliveredException("No launcher is listening for World Server 7.");
                }

                Published.Add(serverSpinUpMessage);
            }
        }

        /// <summary>IBasicProperties as a plain property bag.</summary>
        public class FakeProperties : DispatchProxy
        {
            private readonly Dictionary<string, object> _values = new();

            public static IBasicProperties Create() => Create<IBasicProperties, FakeProperties>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name.StartsWith("set_"))
                {
                    _values[targetMethod.Name.Substring(4)] = args[0];
                    return null;
                }

                if (targetMethod.Name.StartsWith("get_") && _values.TryGetValue(targetMethod.Name.Substring(4), out object value))
                {
                    return value;
                }

                return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        /// <summary>Records the IModel calls these tests care about; anything else is a no-op.</summary>
        public class FakeModel : DispatchProxy
        {
            public sealed record Published(string Exchange, string RoutingKey, bool Mandatory, IBasicProperties Properties);

            public sealed record DeclaredQueue(string Name, bool Durable, bool Exclusive, bool AutoDelete);

            public IModel Model { get; private set; }
            public bool ReturnUnroutable { get; set; }
            public Exception ConfirmFailure { get; set; }
            public bool ConfirmSelected { get; private set; }
            public bool WaitedForConfirms { get; private set; }
            public List<Published> Publishes { get; } = new();
            public List<DeclaredQueue> Queues { get; } = new();
            public List<(string Queue, string Exchange, string RoutingKey)> Bindings { get; } = new();
            public List<EventHandler<BasicReturnEventArgs>> ReturnHandlers { get; } = new();
            public List<ulong> Acks { get; } = new();
            public List<(ulong DeliveryTag, bool Requeue)> Nacks { get; } = new();

            public static FakeModel Create()
            {
                IModel model = Create<IModel, FakeModel>();
                var fake = (FakeModel)(object)model;
                fake.Model = model;
                return fake;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case "ConfirmSelect":
                        ConfirmSelected = true;
                        return null;
                    case "add_BasicReturn":
                        ReturnHandlers.Add((EventHandler<BasicReturnEventArgs>)args[0]);
                        return null;
                    case "remove_BasicReturn":
                        ReturnHandlers.Remove((EventHandler<BasicReturnEventArgs>)args[0]);
                        return null;
                    case "CreateBasicProperties":
                        return FakeProperties.Create();
                    case "BasicPublish":
                        Publishes.Add(new Published((string)args[0], (string)args[1], (bool)args[2], (IBasicProperties)args[3]));
                        if (ReturnUnroutable && (bool)args[2])
                        {
                            foreach (var handler in ReturnHandlers.ToList())
                            {
                                handler(Model, new BasicReturnEventArgs { ReplyCode = 312, ReplyText = "NO_ROUTE", Exchange = (string)args[0], RoutingKey = (string)args[1] });
                            }
                        }
                        return null;
                    case "WaitForConfirmsOrDie":
                        WaitedForConfirms = true;
                        if (ConfirmFailure != null)
                        {
                            throw ConfirmFailure;
                        }
                        return null;
                    case "QueueDeclare":
                        Queues.Add(new DeclaredQueue((string)args[0], (bool)args[1], (bool)args[2], (bool)args[3]));
                        return new QueueDeclareOk((string)args[0], 0, 0);
                    case "QueueBind":
                        Bindings.Add(((string)args[0], (string)args[1], (string)args[2]));
                        return null;
                    case "BasicAck":
                        Acks.Add((ulong)args[0]);
                        return null;
                    case "BasicNack":
                        Nacks.Add(((ulong)args[0], (bool)args[2]));
                        return null;
                    default:
                        return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                            ? Activator.CreateInstance(targetMethod.ReturnType)
                            : null;
                }
            }
        }
    }
}
