using OWSData.Models;
using System;
using System.Collections.Generic;
using System.Text;
using OWSShared.Options;

namespace OWSData.Repositories.Interfaces
{
    public interface IZoneServerProcessesRepository
    {
        void AddZoneServerProcess(ZoneServerProcess zoneServerProcess);
        List<ZoneServerProcess> GetZoneServerProcesses();
        int FindZoneServerProcessId(int zoneInstanceId);
        void RemoveZoneServerProcess(int zoneInstanceId);

        //Removes the entry only if it still tracks processId, so a server that has been replaced can't remove its replacement.
        void RemoveZoneServerProcess(int zoneInstanceId, int processId);
    }
}
