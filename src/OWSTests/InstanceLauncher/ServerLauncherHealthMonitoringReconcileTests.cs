using OWSData.Repositories.Interfaces;
using OWSInstanceLauncher.Services;
using OWSShared.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OWSTests.InstanceLauncher
{
    public class ServerLauncherHealthMonitoringReconcileTests
    {
        //No process runs with this PID, so the kill path ends at "already exited" and removes the map entry.
        //A removal is how these tests see that the monitor decided to kill the process.
        private const int NotRunningProcessId = int.MaxValue;
        private const int TrackedZoneInstanceId = 7;

        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.InternalServerError)]
        public void DoWork_KeepsTrackedProcess_WhenZoneInstanceListRequestFails(HttpStatusCode statusCode)
        {
            var processes = new FakeZoneServerProcessesRepository(TrackedProcessPastGrace());
            var monitor = CreateMonitor(new StubHandler(statusCode, ""), processes);

            for (int tick = 0; tick < ServerLauncherHealthMonitoring.OrphanProcessMissesBeforeKill + 1; tick++)
            {
                monitor.DoWork();
            }

            Assert.Empty(processes.Removed);
        }

        [Fact]
        public void DoWork_KeepsTrackedProcess_WhenZoneInstanceListBodyIsNull()
        {
            var processes = new FakeZoneServerProcessesRepository(TrackedProcessPastGrace());
            var monitor = CreateMonitor(new StubHandler(HttpStatusCode.OK, "null"), processes);

            for (int tick = 0; tick < ServerLauncherHealthMonitoring.OrphanProcessMissesBeforeKill + 1; tick++)
            {
                monitor.DoWork();
            }

            Assert.Empty(processes.Removed);
        }

        [Fact]
        public void DoWork_KillsOrphan_OnlyAfterConsecutiveEmptyLists()
        {
            var processes = new FakeZoneServerProcessesRepository(TrackedProcessPastGrace());
            var monitor = CreateMonitor(new StubHandler(HttpStatusCode.OK, "[]"), processes);

            for (int tick = 0; tick < ServerLauncherHealthMonitoring.OrphanProcessMissesBeforeKill - 1; tick++)
            {
                monitor.DoWork();
                Assert.Empty(processes.Removed);
            }

            monitor.DoWork();

            Assert.Equal(new[] { TrackedZoneInstanceId }, processes.Removed);
        }

        [Fact]
        public void DoWork_ResetsMissCount_WhenInstanceReappears()
        {
            var processes = new FakeZoneServerProcessesRepository(TrackedProcessPastGrace());
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            var monitor = CreateMonitor(handler, processes);

            for (int tick = 0; tick < ServerLauncherHealthMonitoring.OrphanProcessMissesBeforeKill - 1; tick++)
            {
                monitor.DoWork();
            }

            handler.Body = $"[{{\"MapInstanceID\":{TrackedZoneInstanceId},\"Status\":2,\"NumberOfReportedPlayers\":3}}]";
            monitor.DoWork();

            handler.Body = "[]";
            for (int tick = 0; tick < ServerLauncherHealthMonitoring.OrphanProcessMissesBeforeKill - 1; tick++)
            {
                monitor.DoWork();
            }

            Assert.Empty(processes.Removed);
        }

        [Fact]
        public void DoWork_DoesNotCountFailedRequestsAsMisses()
        {
            var processes = new FakeZoneServerProcessesRepository(TrackedProcessPastGrace());
            var handler = new StubHandler(HttpStatusCode.OK, "[]");
            var monitor = CreateMonitor(handler, processes);

            for (int tick = 0; tick < ServerLauncherHealthMonitoring.OrphanProcessMissesBeforeKill - 1; tick++)
            {
                monitor.DoWork();
            }

            handler.StatusCode = HttpStatusCode.ServiceUnavailable;
            monitor.DoWork();
            Assert.Empty(processes.Removed);

            handler.StatusCode = HttpStatusCode.OK;
            monitor.DoWork();
            Assert.Equal(new[] { TrackedZoneInstanceId }, processes.Removed);
        }

        private static ZoneServerProcess TrackedProcessPastGrace()
        {
            //No start time means the grace window and PID-reuse guard don't apply, like a process tracked long ago.
            return new ZoneServerProcess
            {
                ZoneInstanceId = TrackedZoneInstanceId,
                MapName = "TestMap",
                Port = 7778,
                ProcessId = NotRunningProcessId,
                ProcessStartTimeUtc = null
            };
        }

        private static ServerLauncherHealthMonitoring CreateMonitor(StubHandler handler, FakeZoneServerProcessesRepository processes)
        {
            var options = Microsoft.Extensions.Options.Options.Create(new OWSInstanceLauncherOptions());
            return new ServerLauncherHealthMonitoring(options, new StubHttpClientFactory(handler), processes, new FakeInstanceLauncherDataRepository());
        }

        private class StubHandler : HttpMessageHandler
        {
            public HttpStatusCode StatusCode { get; set; }
            public string Body { get; set; }

            public StubHandler(HttpStatusCode statusCode, string body)
            {
                StatusCode = statusCode;
                Body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                //Only the zone instance list is under test; every other call (CleanUpInstances, ShutDownServerInstance) succeeds.
                bool isZoneInstanceList = request.RequestUri.AbsolutePath.EndsWith("GetZoneInstancesForWorldServer");
                var response = new HttpResponseMessage(isZoneInstanceList ? StatusCode : HttpStatusCode.OK)
                {
                    Content = new StringContent(isZoneInstanceList ? Body : "{}", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
        }

        private class StubHttpClientFactory : IHttpClientFactory
        {
            private readonly StubHandler _handler;

            public StubHttpClientFactory(StubHandler handler)
            {
                _handler = handler;
            }

            public HttpClient CreateClient(string name)
            {
                return new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
            }
        }

        private class FakeZoneServerProcessesRepository : IZoneServerProcessesRepository
        {
            private readonly List<ZoneServerProcess> _processes;
            public List<int> Removed { get; } = new List<int>();

            public FakeZoneServerProcessesRepository(params ZoneServerProcess[] processes)
            {
                _processes = processes.ToList();
            }

            public void AddZoneServerProcess(ZoneServerProcess zoneServerProcess) => _processes.Add(zoneServerProcess);

            public List<ZoneServerProcess> GetZoneServerProcesses() => _processes.ToList();

            public int FindZoneServerProcessId(int zoneInstanceId) => _processes.FirstOrDefault(p => p.ZoneInstanceId == zoneInstanceId)?.ProcessId ?? -1;

            public void RemoveZoneServerProcess(int zoneInstanceId)
            {
                Removed.Add(zoneInstanceId);
                _processes.RemoveAll(p => p.ZoneInstanceId == zoneInstanceId);
            }
        }

        private class FakeInstanceLauncherDataRepository : IOWSInstanceLauncherDataRepository
        {
            public void SetWorldServerID(int worldServerID) { }
            public int GetWorldServerID() => 1;
            public void SetLauncherGuid(Guid launcherGuid) { }
            public Guid GetLauncherGuid() => Guid.Empty;
        }
    }
}
