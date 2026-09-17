namespace OWSManagement.DTOs
{
    /// <summary>
    /// A zone as the console's Add / Edit dialog submits it. MapID is ignored on add and
    /// required on edit.
    /// </summary>
    public class AddOrUpdateZoneDTO
    {
        public int MapID { get; set; }

        /// <summary>
        /// The level the packaged server build loads. For a single-zone map this is normally
        /// the same string as ZoneName; a multi-zone map repeats MapName across several rows.
        /// </summary>
        public string MapName { get; set; }

        /// <summary>
        /// What the Instance Launcher resolves when spinning a server up. Unique per customer,
        /// enforced by the repository rather than the schema.
        /// </summary>
        public string ZoneName { get; set; }

        public string WorldCompContainsFilter { get; set; }
        public string WorldCompListFilter { get; set; }
        public int SoftPlayerCap { get; set; }
        public int HardPlayerCap { get; set; }
        public int MapMode { get; set; }
        public int MinutesToShutdownAfterEmpty { get; set; }
    }
}
