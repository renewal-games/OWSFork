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

            foreach (var zoneInstance in zoneInstances)
            {
                if (ShouldShutdownZoneInstance(zoneInstance))
                {
                    ShutDownZoneInstance(worldServerID, zoneInstance.MapInstanceID);
                }
            }
        }

        public static bool ShouldShutdownZoneInstance(GetZoneInstancesForWorldServer zoneInstance)
        {
            return zoneInstance.Status == ZoneInstanceReadyStatus
                && zoneInstance.NumberOfReportedPlayers == 0
                && zoneInstance.MinutesToShutdownAfterEmpty > 0
                && zoneInstance.LastServerEmptyDate.HasValue
                && zoneInstance.MinutesServerHasBeenEmpty >= zoneInstance.MinutesToShutdownAfterEmpty;
        }

        public void Dispose()
        {
            Log.Information("Shutting Down OWS Server Health Monitoring...");
        }

        private List<GetZoneInstancesForWorldServer> GetZoneInstancesForWorldServer(int worldServerId)
        {
            List<GetZoneInstancesForWorldServer> output;

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

            if (responseMessage.IsSuccessStatusCode)
            {
                var responseContentAsync = responseMessage.Content.ReadAsStringAsync();
                string responseContentString = responseContentAsync.Result;
                output = JsonSerializer.Deserialize<List<GetZoneInstancesForWorldServer>>(responseContentString, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            else
            {
                output = new List<GetZoneInstancesForWorldServer>();
            }

            return output;
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
