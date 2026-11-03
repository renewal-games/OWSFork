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

        //A process runs with the tracked PID but it is not the one the launcher started.
        PidReused,

        //The process the launcher started is still running.
        Running
    }

    public enum TrackedZoneProcessStopResult
    {
        //The process is gone, either killed now or already exited.
        Stopped,

        //The PID belongs to an unrelated process, which was left alone. The zone server itself is gone.
        PidReused,

        //The process is still running after the kill attempt.
        StillRunning
    }

    /// <summary>
    /// A tracked PID alone doesn't identify a zone server: the OS can give it to an unrelated process once the server has
    /// exited. Every path that decides to kill, or to treat an instance as running, goes through here so the start time is
    /// always checked first.
    /// </summary>
    public static class TrackedZoneProcess
    {
        private const double StartTimeToleranceSeconds = 2;
        private static readonly TimeSpan WaitForExitAfterKill = TimeSpan.FromSeconds(10);

        //An entry with no recorded start time can't be checked, and counts as the same process. New entries always have
        //one (see HandleServerSpinUpMessage); only entries persisted by an older launcher don't.
        public static bool IsSameProcess(DateTime? recordedStartTimeUtc, DateTime liveStartTimeUtc)
        {
            return !recordedStartTimeUtc.HasValue
                || Math.Abs((liveStartTimeUtc - recordedStartTimeUtc.Value).TotalSeconds) <= StartTimeToleranceSeconds;
        }

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

        //Kills the zone server and its child processes, unless the PID no longer belongs to it.
        public static TrackedZoneProcessStopResult Stop(ZoneServerProcess tracked)
        {
            try
            {
                using Process live = Process.GetProcessById(tracked.ProcessId);

                switch (GetState(live, tracked))
                {
                    case TrackedZoneProcessState.Gone:
                        return TrackedZoneProcessStopResult.Stopped;
                    case TrackedZoneProcessState.PidReused:
                        Log.Warning($"PID {tracked.ProcessId} no longer belongs to Zone Instance {tracked.ZoneInstanceId}: its start time changed, so the PID was reused. Not killing it.");
                        return TrackedZoneProcessStopResult.PidReused;
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
                    case TrackedZoneProcessState.PidReused:
                        return TrackedZoneProcessStopResult.PidReused;
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

                return IsSameProcess(tracked.ProcessStartTimeUtc, live.StartTime.ToUniversalTime())
                    ? TrackedZoneProcessState.Running
                    : TrackedZoneProcessState.PidReused;
            }
            catch (Win32Exception)
            {
                //Access denied: the launcher can always read a process it started, so this one belongs to someone else.
                return TrackedZoneProcessState.PidReused;
            }
        }
    }
}
