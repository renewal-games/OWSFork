using OWSData.Repositories.Interfaces;
using OWSShared.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OWSData.Repositories.Implementations.Local
{
    /// <summary>
    /// Tracks the zone-server processes this launcher has spawned, persisted to a JSON file so the
    /// mapping survives a launcher restart. Without persistence, a restart loses the PID map and any
    /// still-running zone server becomes an untrackable orphan that can never be shut down.
    /// </summary>
    public class PersistentZoneServerProcessesRepository : IZoneServerProcessesRepository
    {
        private readonly object _lock = new object();
        private readonly string _filePath;
        private List<ZoneServerProcess> _zoneServerProcesses;

        public PersistentZoneServerProcessesRepository()
            : this(Path.Combine(AppContext.BaseDirectory, "zone-server-processes.json"))
        {
        }

        public PersistentZoneServerProcessesRepository(string filePath)
        {
            _filePath = filePath;
            _zoneServerProcesses = LoadAndReconcile();
        }

        public void AddZoneServerProcess(ZoneServerProcess zoneServerProcess)
        {
            lock (_lock)
            {
                _zoneServerProcesses.RemoveAll(item => item.ZoneInstanceId == zoneServerProcess.ZoneInstanceId);
                _zoneServerProcesses.Add(zoneServerProcess);
                Persist();
            }
        }

        public List<ZoneServerProcess> GetZoneServerProcesses()
        {
            lock (_lock)
            {
                return new List<ZoneServerProcess>(_zoneServerProcesses);
            }
        }

        //Returns the processId.  Returns -1 if not found.
        public int FindZoneServerProcessId(int zoneInstanceId)
        {
            lock (_lock)
            {
                var foundZoneServerProcess = _zoneServerProcesses.Find(item => item.ZoneInstanceId == zoneInstanceId);

                if (foundZoneServerProcess == null)
                {
                    return -1;
                }

                return (foundZoneServerProcess.ProcessId > 0 ? foundZoneServerProcess.ProcessId : -1);
            }
        }

        public void RemoveZoneServerProcess(int zoneInstanceId)
        {
            lock (_lock)
            {
                _zoneServerProcesses.RemoveAll(item => item.ZoneInstanceId == zoneInstanceId);
                Persist();
            }
        }

        //Load the persisted map and drop any entry whose process is gone or whose PID has been reused (start time mismatch).
        private List<ZoneServerProcess> LoadAndReconcile()
        {
            var loaded = new List<ZoneServerProcess>();

            try
            {
                if (!File.Exists(_filePath))
                {
                    return loaded;
                }

                var json = File.ReadAllText(_filePath);
                var stored = JsonSerializer.Deserialize<List<ZoneServerProcess>>(json) ?? new List<ZoneServerProcess>();

                foreach (var entry in stored)
                {
                    if (IsProcessStillAlive(entry))
                    {
                        loaded.Add(entry);
                    }
                }
            }
            catch
            {
                //Best-effort: a corrupt or unreadable map file must not stop the launcher from starting.
                return new List<ZoneServerProcess>();
            }

            return loaded;
        }

        private static bool IsProcessStillAlive(ZoneServerProcess entry)
        {
            if (entry.ProcessId <= 0)
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(entry.ProcessId);
                if (process.HasExited)
                {
                    return false;
                }

                //Guard against PID reuse: if we recorded a start time, it must match the live process.
                if (entry.ProcessStartTimeUtc.HasValue)
                {
                    var liveStartUtc = process.StartTime.ToUniversalTime();
                    if (Math.Abs((liveStartUtc - entry.ProcessStartTimeUtc.Value).TotalSeconds) > 2)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                //GetProcessById throws when no process with that Id is running.
                return false;
            }
        }

        private void Persist()
        {
            try
            {
                var json = JsonSerializer.Serialize(_zoneServerProcesses);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                //Best-effort persistence: an IO failure must not break spin-up/shutdown handling.
            }
        }
    }
}
