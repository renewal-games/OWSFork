namespace OWSData.Models.Composites
{
    /// <summary>
    /// A row of the Maps table as the management console grid needs it. MapData is
    /// deliberately absent: it is a bytea the console never shows, and sending it would put
    /// an arbitrary blob through the API for every row.
    /// </summary>
    /// <remarks>
    /// ZoneName is what the Instance Launcher resolves when it spins a server up, and it must
    /// match the name of a level the packaged server build actually contains. A row here only
    /// tells OWS the zone exists; it does not put the map on disk.
    /// </remarks>
    public class ZoneSummary
    {
        public int MapID { get; set; }
        public string MapName { get; set; }
        public string ZoneName { get; set; }
        public short Width { get; set; }
        public short Height { get; set; }
        public string WorldCompContainsFilter { get; set; }
        public string WorldCompListFilter { get; set; }
        public int MapMode { get; set; }
        public int SoftPlayerCap { get; set; }
        public int HardPlayerCap { get; set; }
        public int MinutesToShutdownAfterEmpty { get; set; }

        /// <summary>
        /// Live MapInstances rows pointing at this map. Non-zero means deleting the zone would
        /// orphan running or pending instances, so the console blocks it.
        /// </summary>
        public int MapInstanceCount { get; set; }
    }
}
