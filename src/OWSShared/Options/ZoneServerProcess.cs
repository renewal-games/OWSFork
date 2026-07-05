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

        //Process start time, used to guard against PID reuse when the map is reloaded from disk after a launcher restart.
        public DateTime? ProcessStartTimeUtc { get; set; }
    }
}
