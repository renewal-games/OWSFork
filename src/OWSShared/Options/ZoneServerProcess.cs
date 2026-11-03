using System;

namespace OWSShared.Options
{
    public class ZoneServerProcess
    {
        public int ZoneInstanceId { get; set; }
        public string MapName { get; set; }
        public int Port { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }

        //When the process started, for the health monitor's grace window. Not precise or stable enough to identify a process.
        public DateTime? ProcessStartTimeUtc { get; set; }

        //What identifies the process, together with its PID (see OWSShared.ProcessStartStamp). Without one the process is never killed.
        public long? ProcessStartStamp { get; set; }
    }
}
