namespace OWSShared.Options
{
    public class OWSInstanceLauncherOptions
    {
        public const string SectionName = "OWSInstanceLauncherOptions";
        public string OWSAPIKey { get; set; }
        public string LauncherGuid { get; set; }
        public string ServerIP { get; set; }
        public int MaxNumberOfInstances { get; set; }
        public string InternalServerIP { get; set; }
        public int StartingInstancePort { get; set; }
        public bool IsServerEditor { get; set; }
        public string PathToDedicatedServer { get; set; }
        public int RunServerHealthMonitoringFrequencyInSeconds { get; set; }
        public string PathToUProject { get; set; }
        public bool UseServerLog { get; set; }
        public bool UseNoSteam { get; set; }
        public string OtherCustomFlags { get; set; }

        //Shut down a zone instance whose server has not reported in (no heartbeat) for this many minutes.
        //Catches both never-ready instances and servers whose heartbeat died while empty. 0 or unset falls back to a code default.
        public int StaleServerShutdownMinutes { get; set; }

        //Grace period before an orphaned zone-server process (one with no matching live DB instance) is force-killed by the
        //health monitor. Protects a process racing its own spin-up. 0 or unset falls back to a code default.
        public int OrphanProcessGraceMinutes { get; set; }

    }
}
