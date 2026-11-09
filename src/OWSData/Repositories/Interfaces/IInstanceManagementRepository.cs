using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace OWSData.Repositories.Interfaces
{
    public interface IInstanceManagementRepository
    {
        Task<GetServerInstanceFromPort> GetZoneInstance(Guid customerGUID, int zoneInstanceId);

        //Returns null when this customer has no such row, and throws when the lookup fails, so a caller can tell the two apart.
        Task<GetServerInstanceFromPort> GetZoneInstanceForSpinUp(Guid customerGUID, int zoneInstanceId);

        Task<GetServerInstanceFromPort> GetServerInstanceFromPort(Guid customerGUID, string serverIP, int port);
        Task<IEnumerable<GetZoneInstancesForWorldServer>> GetZoneInstancesForWorldServer(Guid customerGUID, int worldServerID);
        Task<SuccessAndErrorMessage> SetZoneInstanceStatus(Guid customerGUID, int zoneInstanceID, int instanceStatus);

        //Moves the row to status 3 (shutting down) under the lock that player joins take, so a join either sees status 3 and
        //goes elsewhere or has its reservation committed before this runs. With onlyIfIdle the move happens only while the
        //instance is still ready, reports no players, has been empty past its zone's timeout and has no reservations;
        //Success is false when nothing changed.
        Task<SuccessAndErrorMessage> BeginZoneInstanceShutdown(Guid customerGUID, int zoneInstanceID, bool onlyIfIdle);

        Task<SuccessAndErrorMessage> CompleteZoneInstanceShutdown(Guid customerGUID, int zoneInstanceID);
        Task<SuccessAndErrorMessage> ShutDownWorldServer(Guid customerGUID, int worldServerID);
        Task<int> StartWorldServer(Guid customerGUID, string launcherGuid);
        Task<SuccessAndErrorMessage> UpdateNumberOfPlayers(Guid customerGUID, int zoneInstanceId, int numberOfPlayers, ZoneServerStats stats);
        Task<IEnumerable<GetZoneInstancesForZone>> GetZoneInstancesOfZone(Guid customerGUID, string ZoneName);
        Task<GetCurrentWorldTime> GetCurrentWorldTime(Guid customerGUID);
        Task<SuccessAndErrorMessage> RegisterLauncher(Guid customerGUID, string launcherGuid, string serverIp, int maxNumberOfInstances, string internalServerIp, int startingInstancePort, string serverRegion);
        Task<IEnumerable<ZoneSummary>> GetZones(Guid customerGUID);
        Task<IEnumerable<ZoneInstanceSummary>> GetLiveZoneInstances(Guid customerGUID);
        // The Postgres implementations reject a ZoneName already in use by another row: the
        // schema does not enforce it and the Instance Launcher resolves zones by name, so a
        // duplicate makes spin-up a coin flip. (MSSQL routes these through a stored procedure
        // and is not a deployment target, so it carries no equivalent guard.)
        Task<SuccessAndErrorMessage> AddZone(Guid customerGUID, string mapName,	string zoneName, string worldCompContainsFilter, string worldCompListFilter, int softPlayerCap, int hardPlayerCap, int mapMode, int minutesToShutdownAfterEmpty);
        Task<SuccessAndErrorMessage> UpdateZone(Guid customerGUID, int mapId, string mapName, string zoneName, string worldCompContainsFilter, string worldCompListFilter, int softPlayerCap, int hardPlayerCap, int mapMode, int minutesToShutdownAfterEmpty);
        Task<SuccessAndErrorMessage> DeleteZone(Guid customerGUID, int mapId);
    }
}
