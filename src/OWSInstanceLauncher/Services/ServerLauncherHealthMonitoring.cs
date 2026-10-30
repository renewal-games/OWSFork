using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OWSData.Models;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.RequestPayloads;
using OWSShared.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Serilog;

namespace OWSInstanceLauncher.Services
{
    public class ServerLauncherHealthMonitoring : IServerHealthMonitoringJob
    {
        private readonly IOptions<OWSInstanceLauncherOptions> _OWSInstanceLauncherOptions;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IZoneServerProcessesRepository _zoneServerProcessesRepository;
        private readonly IOWSInstanceLauncherDataRepository _owsInstanceLauncherDataRepository;
        private const int ZoneInstanceReadyStatus = 2;
        private const int DefaultStaleServerShutdownMinutes = 2;
        private const int DefaultOrphanProcessGraceMinutes = 5;

        //An orphan is only killed after it is missing from this many consecutive successful zone instance lists, so a single
        //odd response can't take down a healthy zone server.
        public const int OrphanProcessMissesBeforeKill = 3;

        //Consecutive successful lists each tracked zone instance has been missing from. Only touched by DoWork, which the timer runs one at a time.
        private readonly Dictionary<int, int> _orphanMissCounts = new Dictionary<int, int>();

        public ServerLauncherHealthMonitoring(IOptions<OWSInstanceLauncherOptions> OWSInstanceLauncherOptions, IHttpClientFactory httpClientFactory, IZoneServerProcessesRepository zoneServerProcessesRepository,
            IOWSInstanceLauncherDataRepository owsInstanceLauncherDataRepository)
        {
            _OWSInstanceLauncherOptions = OWSInstanceLauncherOptions;
            _httpClientFactory = httpClientFactory;
            _zoneServerProcessesRepository = zoneServerProcessesRepository;
            _owsInstanceLauncherDataRepository = owsInstanceLauncherDataRepository;
        }

        public void DoWork()
        {
            Log.Information("Server Health Monitoring is checking for broken Zone Server Instances...");

            int worldServerID = _owsInstanceLauncherDataRepository.GetWorldServerID();

            if (worldServerID < 1)
            {
                Log.Warning("Server Health Monitoring is waiting for a valid World Server ID...");
                return;
            }

            Log.Information("Server Health Monitoring is getting a list of Zone Server Instances...");

            //Get a list of ZoneInstances from api/Instance/GetZoneInstancesForWorldServer
            List<GetZoneInstancesForWorldServer> zoneInstances = GetZoneInstancesForWorldServer(worldServerID);

            //A failed request is not the same as "no instances". Treating it as an empty list would make every tracked process
            //look orphaned and kill every running zone server, so skip the shutdown and reconcile steps until the API answers.
            if (zoneInstances == null)
            {
                Log.Error("Server Health Monitoring could not get the Zone Server Instance list. Skipping shutdown and orphan reconciliation this cycle.");
                RunCleanUpInstances();
                return;
            }

            int staleServerShutdownMinutes = _OWSInstanceLauncherOptions.Value.StaleServerShutdownMinutes;
            if (staleServerShutdownMinutes <= 0)
            {
                staleServerShutdownMinutes = DefaultStaleServerShutdownMinutes;
            }

            foreach (var zoneInstance in zoneInstances)
            {
                if (ShouldShutdownZoneInstance(zoneInstance, staleServerShutdownMinutes))
                {
                    ShutDownZoneInstance(worldServerID, zoneInstance.MapInstanceID);
                }
            }

            //Safety net: kill any zone-server process we started that no longer has a matching DB instance.
            //Without this, a server whose DB row was deleted (e.g. by CleanUpInstances after its heartbeat died) is
            //invisible to the DB-driven shutdown above and leaks forever.
            ReconcileOrphanedProcesses(zoneInstances);

            //Deterministic DB sweep of stale CharOnMapInstance rows and dead MapInstances. Without this,
            //cleanup only ran piggybacked on player joins (exceptions swallowed), so a crashed player's
            //phantom "online" row could survive indefinitely when nobody else was joining.
            RunCleanUpInstances();
        }

        public static bool ShouldShutdownZoneInstance(GetZoneInstancesForWorldServer zoneInstance, int staleServerShutdownMinutes)
        {
            //Normal case: a ready server that has been empty longer than its configured idle timeout.
            bool emptyIdle = zoneInstance.Status == ZoneInstanceReadyStatus
                && zoneInstance.NumberOfReportedPlayers == 0
                && zoneInstance.MinutesToShutdownAfterEmpty > 0
                && zoneInstance.LastServerEmptyDate.HasValue
                && zoneInstance.MinutesServerHasBeenEmpty >= zoneInstance.MinutesToShutdownAfterEmpty;

            //Broken case: the server has stopped reporting in. The dedicated server heartbeats every ~10s
            //(UpdateNumberOfPlayers), so a multi-minute gap means it never became ready or its heartbeat died while
            //occupied. Either way the instance is dead and should be reclaimed.
            bool stale = staleServerShutdownMinutes > 0
                && zoneInstance.MinutesSinceLastUpdate >= staleServerShutdownMinutes;

            return emptyIdle || stale;
        }

        public void Dispose()
        {
            Log.Information("Shutting Down OWS Server Health Monitoring...");
        }

        //Kills zone-server processes this launcher started that no longer correspond to a live DB instance.
        private void ReconcileOrphanedProcesses(List<GetZoneInstancesForWorldServer> zoneInstances)
        {
            int graceMinutes = _OWSInstanceLauncherOptions.Value.OrphanProcessGraceMinutes;
            if (graceMinutes <= 0)
            {
                graceMinutes = DefaultOrphanProcessGraceMinutes;
            }

            var liveInstanceIds = new HashSet<int>(zoneInstances.Select(instance => instance.MapInstanceID));
            var trackedProcesses = _zoneServerProcessesRepository.GetZoneServerProcesses();

            //Forget miss counts for processes that are no longer tracked.
            var trackedInstanceIds = new HashSet<int>(trackedProcesses.Select(process => process.ZoneInstanceId));
            foreach (int zoneInstanceId in _orphanMissCounts.Keys.Where(id => !trackedInstanceIds.Contains(id)).ToList())
            {
                _orphanMissCounts.Remove(zoneInstanceId);
            }

            foreach (var trackedProcess in trackedProcesses)
            {
                //Still has a DB instance -> handled by the normal shutdown flow above, leave it alone.
                if (liveInstanceIds.Contains(trackedProcess.ZoneInstanceId))
                {
                    _orphanMissCounts.Remove(trackedProcess.ZoneInstanceId);
                    continue;
                }

                //Grace window: a freshly spun-up process may briefly have no visible DB row yet. Don't kill it.
                if (trackedProcess.ProcessStartTimeUtc.HasValue
                    && (DateTime.UtcNow - trackedProcess.ProcessStartTimeUtc.Value).TotalMinutes < graceMinutes)
                {
                    continue;
                }

                _orphanMissCounts.TryGetValue(trackedProcess.ZoneInstanceId, out int misses);
                misses++;
                if (misses < OrphanProcessMissesBeforeKill)
                {
                    _orphanMissCounts[trackedProcess.ZoneInstanceId] = misses;
                    Log.Warning($"Reconciliation: Zone Instance {trackedProcess.ZoneInstanceId} (PID {trackedProcess.ProcessId}) is missing from the zone instance list ({misses} of {OrphanProcessMissesBeforeKill} checks).");
                    continue;
                }

                _orphanMissCounts.Remove(trackedProcess.ZoneInstanceId);
                KillOrphanedProcess(trackedProcess);
            }
        }

        private void KillOrphanedProcess(ZoneServerProcess trackedProcess)
        {
            Log.Warning($"Reconciliation: Zone Instance {trackedProcess.ZoneInstanceId} (Port {trackedProcess.Port}, PID {trackedProcess.ProcessId}) has no live DB instance. Killing orphaned zone server process.");

            try
            {
                System.Diagnostics.Process procToKill = System.Diagnostics.Process.GetProcessById(trackedProcess.ProcessId);

                //PID-reuse guard: if the live process's start time no longer matches, the PID was reused by an unrelated
                //process. Never kill it; just drop the stale map entry.
                if (trackedProcess.ProcessStartTimeUtc.HasValue
                    && Math.Abs((procToKill.StartTime.ToUniversalTime() - trackedProcess.ProcessStartTimeUtc.Value).TotalSeconds) > 2)
                {
                    Log.Warning($"Reconciliation: PID {trackedProcess.ProcessId} start time no longer matches Zone Instance {trackedProcess.ZoneInstanceId}; PID was reused. Dropping stale map entry.");
                    _zoneServerProcessesRepository.RemoveZoneServerProcess(trackedProcess.ZoneInstanceId);
                    return;
                }

                if (!procToKill.HasExited)
                {
                    procToKill.Kill(entireProcessTree: true);
                    procToKill.WaitForExit(10000);
                }
            }
            catch (ArgumentException)
            {
                //No process with that Id is running; it already exited.
            }
            catch (Exception ex)
            {
                Log.Error($"Reconciliation: error killing orphaned process for Zone Instance {trackedProcess.ZoneInstanceId}: {ex.Message}. Will retry next cycle.");
                return;
            }

            _zoneServerProcessesRepository.RemoveZoneServerProcess(trackedProcess.ZoneInstanceId);
        }

        //Returns null when the list could not be fetched (non-success status, unreadable body or request failure).
        private List<GetZoneInstancesForWorldServer> GetZoneInstancesForWorldServer(int worldServerId)
        {
            try
            {
                return RequestZoneInstancesForWorldServer(worldServerId);
            }
            catch (Exception ex)
            {
                Log.Error($"Server Health Monitoring failed to get Zone Server Instances: {ex.Message}");
                return null;
            }
        }

        private List<GetZoneInstancesForWorldServer> RequestZoneInstancesForWorldServer(int worldServerId)
        {
            var instanceManagementHttpClient = _httpClientFactory.CreateClient("OWSInstanceManagement");

            var worldServerIDRequestPayload = new
            {
                request = new WorldServerIDRequestPayload
                {
                    WorldServerID = worldServerId
                }
            };

            var getZoneInstancesForWorldServerRequest = new StringContent(JsonSerializer.Serialize(worldServerIDRequestPayload), Encoding.UTF8, "application/json");

            var responseMessageTask = instanceManagementHttpClient.PostAsync("api/Instance/GetZoneInstancesForWorldServer", getZoneInstancesForWorldServerRequest);
            var responseMessage = responseMessageTask.Result;

            if (!responseMessage.IsSuccessStatusCode)
            {
                Log.Error($"Server Health Monitoring failed to get Zone Server Instances. HTTP Status: {responseMessage.StatusCode}");
                return null;
            }

            var responseContentAsync = responseMessage.Content.ReadAsStringAsync();
            string responseContentString = responseContentAsync.Result;

            //A null result (body "null") is unreadable, not empty; the caller treats it as a failed request.
            return JsonSerializer.Deserialize<List<GetZoneInstancesForWorldServer>>(responseContentString, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        private void RunCleanUpInstances()
        {
            try
            {
                var instanceManagementHttpClient = _httpClientFactory.CreateClient("OWSInstanceManagement");

                var cleanUpInstancesRequest = new StringContent("{}", Encoding.UTF8, "application/json");
                var responseMessage = instanceManagementHttpClient.PostAsync("api/Instance/CleanUpInstances", cleanUpInstancesRequest).Result;

                if (!responseMessage.IsSuccessStatusCode)
                {
                    Log.Error($"Server Health Monitoring failed to run CleanUpInstances. HTTP Status: {responseMessage.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Server Health Monitoring failed to run CleanUpInstances: {ex.Message}. Will retry next cycle.");
            }
        }

        private void ShutDownZoneInstance(int worldServerId, int zoneInstanceId)
        {
            Log.Information($"Server Health Monitoring is shutting down empty Zone Server Instance {zoneInstanceId}.");

            var instanceManagementHttpClient = _httpClientFactory.CreateClient("OWSInstanceManagement");

            var shutDownServerInstancePayload = new
            {
                WorldServerID = worldServerId,
                ZoneInstanceID = zoneInstanceId
            };

            var shutDownServerInstanceRequest = new StringContent(JsonSerializer.Serialize(shutDownServerInstancePayload), Encoding.UTF8, "application/json");
            var responseMessageTask = instanceManagementHttpClient.PostAsync("api/Instance/ShutDownServerInstance", shutDownServerInstanceRequest);
            var responseMessage = responseMessageTask.Result;

            if (!responseMessage.IsSuccessStatusCode)
            {
                Log.Error($"Server Health Monitoring failed to request shutdown for Zone Server Instance {zoneInstanceId}. HTTP Status: {responseMessage.StatusCode}");
            }
        }
    }
}
