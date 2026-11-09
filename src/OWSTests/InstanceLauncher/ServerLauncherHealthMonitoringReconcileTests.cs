using OWSData.Repositories.Interfaces;
using OWSInstanceLauncher.Services;
using OWSShared;
using OWSShared.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DoWork_DropsTheEntryButLeavesTheProcess_WhenItCannotBeShownToBeTheServer(bool stampRecorded)
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("ping", "-n 300 127.0.0.1")
                : new ProcessStartInfo("sleep", "300");
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.CreateNoWindow = true;
            using Process unrelated = Process.Start(startInfo);

            try
            {
                var processes = new FakeZoneServerProcessesRepository(new ZoneServerProcess
                {
                    ZoneInstanceId = TrackedZoneInstanceId,
                    ProcessId = unrelated.Id,
                    ProcessStartTimeUtc = unrelated.StartTime.ToUniversalTime().AddHours(-1),
                    ProcessStartStamp = stampRecorded ? ProcessStartStamp.Read(unrelated) + 1 : null
                });
                var monitor = CreateMonitor(new StubHandler(HttpStatusCode.OK, "[]"), processes);

                for (int tick = 0; tick < ServerLauncherHealthMonitoring.OrphanProcessMissesBeforeKill; tick++)
                {
                    monitor.DoWork();
                }

                Assert.Equal(new[] { TrackedZoneInstanceId }, processes.Removed);
                Assert.False(unrelated.HasExited);
            }
            finally
            {
                unrelated.Kill(entireProcessTree: true);
            }
        }

        //An idle shutdown must ask InstanceManagement to recheck idleness at the transition (a player may have been routed
        //to the instance since the list was read); a dead server's shutdown must not, or a stale reservation would keep it.
        [Theory]
        [InlineData("\"Status\":2,\"NumberOfReportedPlayers\":0,\"MinutesToShutdownAfterEmpty\":5,\"LastServerEmptyDate\":\"2026-01-01T00:00:00\",\"MinutesServerHasBeenEmpty\":10,\"MinutesSinceLastUpdate\":0", true)]
        [InlineData("\"Status\":2,\"NumberOfReportedPlayers\":3,\"MinutesSinceLastUpdate\":10", false)]
        public void DoWork_AsksForAnIdleOnlyShutdown_OnlyWhenTheInstanceIsIdleRatherThanStale(string instanceFields, bool expectedOnlyIfIdle)
        {
            var processes = new FakeZoneServerProcessesRepository();
            var handler = new StubHandler(HttpStatusCode.OK, $"[{{\"MapInstanceID\":{TrackedZoneInstanceId},{instanceFields}}}]");
            var monitor = CreateMonitor(handler, processes);

            monitor.DoWork();

            string body = Assert.Single(handler.ShutDownBodies);
            using var payload = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal(TrackedZoneInstanceId, payload.RootElement.GetProperty("ZoneInstanceID").GetInt32());
            Assert.Equal(expectedOnlyIfIdle, payload.RootElement.GetProperty("OnlyIfIdle").GetBoolean());
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

            public List<string> ShutDownBodies { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("ShutDownServerInstance"))
                {
                    ShutDownBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).Result);
                }

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

            public void RemoveZoneServerProcess(int zoneInstanceId, int processId)
            {
                if (_processes.RemoveAll(p => p.ZoneInstanceId == zoneInstanceId && p.ProcessId == processId) > 0)
                {
                    Removed.Add(zoneInstanceId);
                }
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
