using OWSData.Repositories.Interfaces;
using OWSShared;
using OWSShared.Options;
using Serilog;
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
    /// <remarks>
    /// The live file is never written in place. Each save goes to a temp file beside it, which is then
    /// swapped over the live file, and the previous live file is kept as a .bak. A crash or power loss
    /// mid-save therefore leaves the last committed map intact, and a live file that is damaged anyway
    /// falls back to the .bak on load.
    /// </remarks>
    public class PersistentZoneServerProcessesRepository : IZoneServerProcessesRepository
    {
        private readonly object _lock = new object();
        private readonly string _filePath;
        private readonly string _backupPath;
        private readonly string _tempPath;
        private readonly Func<string, Stream> _openTempFile;
        private List<ZoneServerProcess> _zoneServerProcesses;

        //Single public constructor so the DI container (SimpleInjector) can construct it.
        public PersistentZoneServerProcessesRepository()
            : this(Path.Combine(AppContext.BaseDirectory, "zone-server-processes.json"))
        {
        }

        //Tests use this to point the map at their own directory and to fault the temp-file write.
        internal PersistentZoneServerProcessesRepository(string filePath, Func<string, Stream> openTempFile = null)
        {
            _filePath = filePath;
            _backupPath = filePath + ".bak";
            _tempPath = filePath + ".tmp";
            _openTempFile = openTempFile ?? (path => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None));
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

        public void RemoveZoneServerProcess(int zoneInstanceId, int processId)
        {
            lock (_lock)
            {
                if (_zoneServerProcesses.RemoveAll(item => item.ZoneInstanceId == zoneInstanceId && item.ProcessId == processId) > 0)
                {
                    Persist();
                }
            }
        }

        //Load the persisted map and drop any entry whose process is gone or can't be shown to be the same one (start stamp mismatch).
        private List<ZoneServerProcess> LoadAndReconcile()
        {
            //A corrupt or unreadable map must not stop the launcher from starting: fall back to the backup, then to an empty map, logging either way.
            if (!TryReadMap(_filePath, out List<ZoneServerProcess> stored))
            {
                if (TryReadMap(_backupPath, out stored))
                {
                    Log.Warning("Zone server process map {FilePath} could not be read. Loaded the previous copy {BackupPath} instead; a zone server started by the last save may be untracked.", _filePath, _backupPath);
                }
                else if (File.Exists(_filePath) || File.Exists(_backupPath))
                {
                    Log.Error("Neither zone server process map {FilePath} nor its backup {BackupPath} could be read. Any zone server still running from before this restart is untracked and will not be stopped by the launcher.", _filePath, _backupPath);
                }

                stored ??= new List<ZoneServerProcess>();
            }

            return stored.Where(entry => entry != null && IsProcessStillAlive(entry)).ToList();
        }

        //False when the file is missing (a fresh launcher) or can't be read or parsed (logged).
        private static bool TryReadMap(string path, out List<ZoneServerProcess> stored)
        {
            stored = null;

            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                stored = JsonSerializer.Deserialize<List<ZoneServerProcess>>(File.ReadAllText(path)) ?? new List<ZoneServerProcess>();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not read zone server process map {Path}.", path);
                return false;
            }
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

                //Guard against PID reuse. An entry with no stamp can't be tied to this process, so it is dropped too.
                return ProcessStartStamp.Matches(entry.ProcessStartStamp, ProcessStartStamp.Read(process));
            }
            catch
            {
                //GetProcessById throws when no process with that Id is running.
                return false;
            }
        }

        private void Persist()
        {
            //An IO failure must not break spin-up/shutdown handling, so it is logged rather than thrown.
            try
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(_zoneServerProcesses);

                using (Stream stream = _openTempFile(_tempPath))
                {
                    stream.Write(json, 0, json.Length);
                    stream.Flush();

                    //On disk before the swap, so a power loss can't leave the live name pointing at unwritten data.
                    if (stream is FileStream fileStream)
                    {
                        fileStream.Flush(flushToDisk: true);
                    }
                }

                if (File.Exists(_filePath))
                {
                    File.Replace(_tempPath, _filePath, _backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(_tempPath, _filePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not save zone server process map {FilePath}. The previous copy on disk is kept, but it is missing the latest change until a later save succeeds.", _filePath);

                try
                {
                    File.Delete(_tempPath);
                }
                catch (Exception)
                {
                    //Only a leftover temp file; the next save truncates it.
                }
            }
        }
    }
}
