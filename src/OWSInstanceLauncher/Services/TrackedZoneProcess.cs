using OWSShared;
using OWSShared.Options;
using Serilog;
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace OWSInstanceLauncher.Services
{
    public enum TrackedZoneProcessState
    {
        //No process runs with the tracked PID.
        Gone,

        //A process runs with the tracked PID, but nothing shows it is the one the launcher started: its start stamp differs,
        //can't be read, or was never recorded.
        Unverified,

        //The process the launcher started is still running.
        Running
    }

    public enum TrackedZoneProcessStopResult
    {
        //The process is gone, either killed now or already exited.
        Stopped,

        //The process with this PID could not be shown to be the zone server, so it was left alone.
        Unverified,

        //The process is still running after the kill attempt.
        StillRunning
    }

    /// <summary>
    /// A tracked PID alone doesn't identify a zone server: the OS can give it to an unrelated process once the server has
    /// exited. Every path that decides to kill, or to treat an instance as running, goes through here, and only a process
    /// whose start stamp matches exactly is ever killed.
    /// </summary>
    public static class TrackedZoneProcess
    {
        private static readonly TimeSpan WaitForExitAfterKill = TimeSpan.FromSeconds(10);

        public static TrackedZoneProcessState GetState(ZoneServerProcess tracked)
        {
            try
            {
                using Process live = Process.GetProcessById(tracked.ProcessId);
                return GetState(live, tracked);
            }
            catch (ArgumentException)
            {
                return TrackedZoneProcessState.Gone;
            }
            catch (InvalidOperationException)
            {
                //It exited between GetProcessById and reading it.
                return TrackedZoneProcessState.Gone;
            }
        }

        public static bool IsRunning(ZoneServerProcess tracked)
        {
            return GetState(tracked) == TrackedZoneProcessState.Running;
        }

        //Kills the zone server and its child processes, but only if the PID can be shown to still belong to it.
        public static TrackedZoneProcessStopResult Stop(ZoneServerProcess tracked)
        {
            try
            {
                using Process live = Process.GetProcessById(tracked.ProcessId);

                switch (GetState(live, tracked))
                {
                    case TrackedZoneProcessState.Gone:
                        return TrackedZoneProcessStopResult.Stopped;
                    case TrackedZoneProcessState.Unverified:
                        Log.Warning($"PID {tracked.ProcessId} can't be shown to belong to Zone Instance {tracked.ZoneInstanceId}: its start stamp does not match the recorded one, so the PID may have been reused. Not killing it.");
                        return TrackedZoneProcessStopResult.Unverified;
                }

                live.Kill(entireProcessTree: true);

                return live.WaitForExit(WaitForExitAfterKill) ? TrackedZoneProcessStopResult.Stopped : TrackedZoneProcessStopResult.StillRunning;
            }
            catch (ArgumentException)
            {
                return TrackedZoneProcessStopResult.Stopped;
            }
            catch (Exception ex)
            {
                //Kill can throw while the process is already exiting. Only a process that is really gone counts as stopped.
                Log.Warning($"Error killing Zone Instance {tracked.ZoneInstanceId} process {tracked.ProcessId}: {ex.Message}");
                return GetStopResultAfterFailedKill(tracked);
            }
        }

        private static TrackedZoneProcessStopResult GetStopResultAfterFailedKill(ZoneServerProcess tracked)
        {
            try
            {
                switch (GetState(tracked))
                {
                    case TrackedZoneProcessState.Gone:
                        return TrackedZoneProcessStopResult.Stopped;
                    case TrackedZoneProcessState.Unverified:
                        return TrackedZoneProcessStopResult.Unverified;
                    default:
                        return TrackedZoneProcessStopResult.StillRunning;
                }
            }
            catch (Exception)
            {
                return TrackedZoneProcessStopResult.StillRunning;
            }
        }

        private static TrackedZoneProcessState GetState(Process live, ZoneServerProcess tracked)
        {
            try
            {
                if (live.HasExited)
                {
                    return TrackedZoneProcessState.Gone;
                }

                return ProcessStartStamp.Matches(tracked.ProcessStartStamp, ProcessStartStamp.Read(live))
                    ? TrackedZoneProcessState.Running
                    : TrackedZoneProcessState.Unverified;
            }
            catch (Win32Exception)
            {
                //Access denied: the launcher can always read a process it started, so this one belongs to someone else.
                return TrackedZoneProcessState.Unverified;
            }
        }
    }
}
