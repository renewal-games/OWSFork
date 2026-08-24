using System;

namespace OWSManagement.DTOs
{
    public class SetUserNetworkTestFlagDTO
    {
        public Guid UserGUID { get; set; }

        /// <summary>
        /// Applied to every character this user owns. The flag itself lives on Characters;
        /// there is no account-level column.
        /// </summary>
        public bool IsInternalNetworkTestUser { get; set; }
    }
}
