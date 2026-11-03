using System;
using System.Diagnostics;
using System.IO;

namespace OWSShared
{
    /// <summary>
    /// The OS's own record of when a process started, which together with its PID identifies it. Compared exactly: any
    /// tolerance would let a process that reused the PID pass as the original.
    /// </summary>
    public static class ProcessStartStamp
    {
        //Field 22 of /proc/[pid]/stat, counted from field 3, which is the first one after the command name.
        private const int LinuxStartTimeIndexAfterCommandName = 19;

        //Throws InvalidOperationException if the process has exited.
        public static long Read(Process process)
        {
            //On Linux Process.StartTime is worked out from a boot time the runtime calculates once per process, so two
            //launchers can read different values for one server. The kernel's own value never changes.
            return OperatingSystem.IsLinux()
                ? ParseLinuxStat(ReadLinuxStat(process.Id))
                : process.StartTime.ToUniversalTime().Ticks;
        }

        //An entry with no recorded stamp can't be tied to any process, so it never matches.
        public static bool Matches(long? recordedStamp, long liveStamp)
        {
            return recordedStamp == liveStamp;
        }

        //The command name is in parentheses and can itself contain spaces and parentheses, so fields are counted from the last ')'.
        public static long ParseLinuxStat(string stat)
        {
            string afterCommandName = stat.Substring(stat.LastIndexOf(')') + 1).TrimStart();

            return long.Parse(afterCommandName.Split(' ')[LinuxStartTimeIndexAfterCommandName]);
        }

        private static string ReadLinuxStat(int processId)
        {
            try
            {
                return File.ReadAllText($"/proc/{processId}/stat");
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Process {processId} has exited.", ex);
            }
        }
    }
}
