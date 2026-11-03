using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OWSData.Repositories.Implementations.InMemory;
using OWSData.Repositories.Implementations.Local;
using OWSData.Repositories.Interfaces;
using OWSInstanceLauncher.Services;
using OWSShared.Interfaces;
using OWSShared.Options;
using Xunit;

namespace OWSTests.InstanceLauncher
{
    /// <summary>
    /// Drives the real listener against real child processes. RabbitMQ is pointed at a closed port, which the listener logs
    /// and continues past, so the handlers are called directly.
    /// </summary>
    public sealed class ZoneProcessLifecycleTests : IDisposable
    {
        private const int WorldServerId = 7;
        private const int NotRunningProcessId = int.MaxValue;

        private static readonly Guid CustomerGuid = Guid.NewGuid();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly List<Process> _bystanders = new();
        private readonly LockedZoneServerProcessesRepository _processes = new();
        private readonly RecordingHandler _http = new();
        private readonly string _serverDirectory = Directory.CreateTempSubdirectory("ows-fake-zone-").FullName;

        [Fact]
        public async Task ShutDown_LeavesAProcessWithAReusedPidRunning()
        {
            Process bystander = StartLongRunningProcess();
            _processes.AddZoneServerProcess(TrackedAs(1, bystander, bystander.StartTime.ToUniversalTime().AddHours(-1)));

            await Listener().HandleServerShutDownMessage(CustomerGuid, 1);

            Assert.False(bystander.HasExited);
            //The zone server itself is gone, so the row and the stale entry can both go.
            Assert.Contains(_http.Calls, path => path.EndsWith("CompleteZoneInstanceShutdown"));
            Assert.Empty(_processes.GetZoneServerProcesses());
        }

        [Fact]
        public async Task ShutDown_KillsTheProcessItStarted()
        {
            Process server = StartLongRunningProcess();
            _processes.AddZoneServerProcess(TrackedAs(1, server, server.StartTime.ToUniversalTime()));

            await Listener().HandleServerShutDownMessage(CustomerGuid, 1);

            Assert.True(server.WaitForExit(Timeout));
            Assert.Contains(_http.Calls, path => path.EndsWith("CompleteZoneInstanceShutdown"));
        }

        [Fact]
        public void ShutDownAll_StopsTheLiveServerAfterADeadPid_AndSparesAReusedPid()
        {
            Process reusedPid = StartLongRunningProcess();
            Process server = StartLongRunningProcess();
            _processes.AddZoneServerProcess(new ZoneServerProcess { ZoneInstanceId = 1, ProcessId = NotRunningProcessId });
            _processes.AddZoneServerProcess(TrackedAs(2, reusedPid, reusedPid.StartTime.ToUniversalTime().AddHours(-1)));
            _processes.AddZoneServerProcess(TrackedAs(3, server, server.StartTime.ToUniversalTime()));

            Listener().ShutDownAllZoneServerInstances();

            Assert.True(server.WaitForExit(Timeout));
            Assert.False(reusedPid.HasExited);
        }

        [Fact]
        public void Dispose_StopsTheServers_WhenInstanceManagementCannotBeReached()
        {
            Process server = StartLongRunningProcess();
            _processes.AddZoneServerProcess(TrackedAs(1, server, server.StartTime.ToUniversalTime()));
            ServerLauncherMQListener listener = Listener();
            _http.FailPathsEndingWith = "ShutDownInstanceLauncher";

            listener.Dispose();

            Assert.True(server.WaitForExit(Timeout));
        }

        [Fact]
        public async Task SpinUp_TwiceForOneZoneInstanceOnDifferentPorts_StartsOneServer()
        {
            ServerLauncherMQListener listener = Listener();

            await listener.HandleServerSpinUpMessage(CustomerGuid, WorldServerId, 1, "Map", FreeUdpPort());
            ZoneServerProcess first = Assert.Single(_processes.GetZoneServerProcesses());
            await listener.HandleServerSpinUpMessage(CustomerGuid, WorldServerId, 1, "Map", FreeUdpPort());

            ZoneServerProcess tracked = Assert.Single(_processes.GetZoneServerProcesses());
            Assert.Equal(first.ProcessId, tracked.ProcessId);
            Assert.Equal(first.Port, tracked.Port);
        }

        [Fact]
        public async Task SpinUp_StartsAServer_WhenTheTrackedPidWasReused()
        {
            Process bystander = StartLongRunningProcess();
            _processes.AddZoneServerProcess(TrackedAs(1, bystander, bystander.StartTime.ToUniversalTime().AddHours(-1)));

            await Listener().HandleServerSpinUpMessage(CustomerGuid, WorldServerId, 1, "Map", FreeUdpPort());

            ZoneServerProcess tracked = Assert.Single(_processes.GetZoneServerProcesses());
            Assert.NotEqual(bystander.Id, tracked.ProcessId);
            Assert.False(bystander.HasExited);
        }

        [Theory]
        [InlineData(WorldServerId, 1, -1)]
        [InlineData(WorldServerId, 1, 0)]
        [InlineData(WorldServerId, 1, 65536)]
        [InlineData(WorldServerId, 0, 7777)]
        [InlineData(WorldServerId, -1, 7777)]
        [InlineData(WorldServerId + 1, 1, 7777)]
        public async Task SpinUp_WithInvalidValues_StartsNothingAndDoesNotThrow(int worldServerId, int zoneInstanceId, int port)
        {
            await Listener().HandleServerSpinUpMessage(CustomerGuid, worldServerId, zoneInstanceId, "Map", port);

            Assert.Empty(_processes.GetZoneServerProcesses());
            //The command can't be trusted to name the right row, so it must not clean one up either.
            Assert.DoesNotContain(_http.Calls, path => path.EndsWith("SetZoneInstanceStatus") || path.EndsWith("CompleteZoneInstanceShutdown"));
        }

        [Fact]
        public async Task SpinUp_AtTheInstanceLimit_StartsNothingAndCleansUpTheRow()
        {
            Process running = StartLongRunningProcess();
            _processes.AddZoneServerProcess(TrackedAs(1, running, running.StartTime.ToUniversalTime()));

            await Listener(maxNumberOfInstances: 1).HandleServerSpinUpMessage(CustomerGuid, WorldServerId, 2, "Map", FreeUdpPort());

            Assert.Equal(1, Assert.Single(_processes.GetZoneServerProcesses()).ZoneInstanceId);
            Assert.Contains(_http.Calls, path => path.EndsWith("CompleteZoneInstanceShutdown"));
        }

        [Fact]
        public async Task SpinUp_ExitedServerDoesNotCountAgainstTheLimit()
        {
            _processes.AddZoneServerProcess(new ZoneServerProcess { ZoneInstanceId = 1, ProcessId = NotRunningProcessId });

            await Listener(maxNumberOfInstances: 1).HandleServerSpinUpMessage(CustomerGuid, WorldServerId, 2, "Map", FreeUdpPort());

            Assert.Contains(_processes.GetZoneServerProcesses(), process => process.ZoneInstanceId == 2);
        }

        [Fact]
        public async Task ExitedServer_RemovesItsEntry()
        {
            await Listener().HandleServerSpinUpMessage(CustomerGuid, WorldServerId, 1, "Map", FreeUdpPort());
            ZoneServerProcess tracked = Assert.Single(_processes.GetZoneServerProcesses());

            using (Process server = Process.GetProcessById(tracked.ProcessId))
            {
                server.Kill(entireProcessTree: true);
            }

            await WaitUntil(() => _processes.GetZoneServerProcesses().Count == 0);
        }

        [Fact]
        public void TrackedProcess_StateFollowsTheStartTime()
        {
            Process process = StartLongRunningProcess();
            DateTime startTime = process.StartTime.ToUniversalTime();

            Assert.Equal(TrackedZoneProcessState.Running, TrackedZoneProcess.GetState(TrackedAs(1, process, startTime)));
            Assert.Equal(TrackedZoneProcessState.Running, TrackedZoneProcess.GetState(TrackedAs(1, process, null)));
            Assert.Equal(TrackedZoneProcessState.PidReused, TrackedZoneProcess.GetState(TrackedAs(1, process, startTime.AddSeconds(30))));
            Assert.Equal(TrackedZoneProcessState.Gone, TrackedZoneProcess.GetState(new ZoneServerProcess { ProcessId = NotRunningProcessId }));
        }

        public static IEnumerable<object[]> Repositories()
        {
            yield return new object[] { new ZoneServerProcessesRepository() };
            yield return new object[] { new PersistentZoneServerProcessesRepository() };
        }

        [Theory]
        [MemberData(nameof(Repositories))]
        public void RemoveByProcessId_KeepsAnEntryThatTracksAnotherProcess(IZoneServerProcessesRepository processes)
        {
            processes.AddZoneServerProcess(new ZoneServerProcess { ZoneInstanceId = 1, ProcessId = 200 });

            processes.RemoveZoneServerProcess(1, 100);
            Assert.Equal(200, processes.FindZoneServerProcessId(1));

            processes.RemoveZoneServerProcess(1, 200);
            Assert.Equal(-1, processes.FindZoneServerProcessId(1));
        }

        public void Dispose()
        {
            foreach (int processId in _processes.GetZoneServerProcesses().Select(process => process.ProcessId).Where(id => id != NotRunningProcessId))
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    //Already gone.
                }
            }

            foreach (Process process in _bystanders)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(Timeout);
                }
                catch (Exception)
                {
                    //Already gone.
                }

                process.Dispose();
            }

            try
            {
                Directory.Delete(_serverDirectory, recursive: true);
            }
            catch (IOException)
            {
                //A killed fake server may still hold its script briefly; it is only a temp file.
            }
        }

        private ServerLauncherMQListener Listener(int maxNumberOfInstances = 10)
        {
            var options = new OWSInstanceLauncherOptions
            {
                OWSAPIKey = CustomerGuid.ToString(),
                LauncherGuid = Guid.NewGuid().ToString(),
                MaxNumberOfInstances = maxNumberOfInstances
            };
            ConfigureFakeServer(options);

            var rabbitMQ = Microsoft.Extensions.Options.Options.Create(new RabbitMQOptions { RabbitMQHostName = "127.0.0.1", RabbitMQPort = 1, RabbitMQUserName = "none", RabbitMQPassword = "none" });

            return new ServerLauncherMQListener(new FixedWritableOptions(options), Microsoft.Extensions.Options.Options.Create(new APIPathOptions()), rabbitMQ,
                new StubHttpClientFactory(_http), _processes, new FixedWorldServerRepository());
        }

        //A dedicated server that ignores its arguments and stays up until it is killed.
        private void ConfigureFakeServer(OWSInstanceLauncherOptions options)
        {
            if (OperatingSystem.IsWindows())
            {
                options.PathToDedicatedServer = Path.Combine(_serverDirectory, "fake-zone-server.cmd");
                File.WriteAllText(options.PathToDedicatedServer, "@ping -n 300 127.0.0.1 > nul\r\n");
                return;
            }

            //The script is passed to sh instead of being executed itself: writing an executable and starting it straight
            //away can fail with "text file busy" while another test forks.
            string script = Path.Combine(_serverDirectory, "fake-zone-server.sh");
            File.WriteAllText(script, "exec sleep 300\n");
            options.PathToDedicatedServer = "/bin/sh";
            options.IsServerEditor = true;
            options.PathToUProject = script;
        }

        //A process a test tracks either as its own zone server or as an unrelated one that was given a reused PID.
        private Process StartLongRunningProcess()
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("ping", "-n 300 127.0.0.1")
                : new ProcessStartInfo("sleep", "300");
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.CreateNoWindow = true;

            Process process = Process.Start(startInfo);
            _bystanders.Add(process);
            return process;
        }

        private static ZoneServerProcess TrackedAs(int zoneInstanceId, Process process, DateTime? startTimeUtc)
        {
            return new ZoneServerProcess { ZoneInstanceId = zoneInstanceId, MapName = "Map", Port = 7777, ProcessId = process.Id, ProcessStartTimeUtc = startTimeUtc };
        }

        private static int FreeUdpPort()
        {
            using var udpClient = new UdpClient(0);
            return ((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
                await Task.Delay(50);
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

        private sealed class FixedWorldServerRepository : IOWSInstanceLauncherDataRepository
        {
            public void SetWorldServerID(int worldServerID) { }
            public int GetWorldServerID() => WorldServerId;
            public void SetLauncherGuid(Guid launcherGuid) { }
            public Guid GetLauncherGuid() => Guid.Empty;
        }

        //Exited handlers run on the thread pool, so unlike the in-memory repository this one can be used from several threads.
        private sealed class LockedZoneServerProcessesRepository : IZoneServerProcessesRepository
        {
            private readonly object _lock = new();
            private readonly List<ZoneServerProcess> _processes = new();

            public void AddZoneServerProcess(ZoneServerProcess zoneServerProcess)
            {
                lock (_lock)
                {
                    _processes.RemoveAll(process => process.ZoneInstanceId == zoneServerProcess.ZoneInstanceId);
                    _processes.Add(zoneServerProcess);
                }
            }

            public List<ZoneServerProcess> GetZoneServerProcesses()
            {
                lock (_lock)
                {
                    return _processes.ToList();
                }
            }

            public int FindZoneServerProcessId(int zoneInstanceId)
            {
                lock (_lock)
                {
                    return _processes.Find(process => process.ZoneInstanceId == zoneInstanceId)?.ProcessId ?? -1;
                }
            }

            public void RemoveZoneServerProcess(int zoneInstanceId)
            {
                lock (_lock)
                {
                    _processes.RemoveAll(process => process.ZoneInstanceId == zoneInstanceId);
                }
            }

            public void RemoveZoneServerProcess(int zoneInstanceId, int processId)
            {
                lock (_lock)
                {
                    _processes.RemoveAll(process => process.ZoneInstanceId == zoneInstanceId && process.ProcessId == processId);
                }
            }
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly List<string> _calls = new();

            public string FailPathsEndingWith { get; set; }

            public List<string> Calls
            {
                get
                {
                    lock (_calls)
                    {
                        return _calls.ToList();
                    }
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri.AbsolutePath;
                lock (_calls)
                {
                    _calls.Add(path);
                }

                if (FailPathsEndingWith != null && path.EndsWith(FailPathsEndingWith))
                {
                    throw new HttpRequestException("Instance Management is down.");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"Success\":true,\"ErrorMessage\":\"\"}", Encoding.UTF8, "application/json")
                });
            }
        }

        private sealed class StubHttpClientFactory : IHttpClientFactory
        {
            private readonly RecordingHandler _handler;

            public StubHttpClientFactory(RecordingHandler handler)
            {
                _handler = handler;
            }

            public HttpClient CreateClient(string name)
            {
                return new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
            }
        }
    }
}
