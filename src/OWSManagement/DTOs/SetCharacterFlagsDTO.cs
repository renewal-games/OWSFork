namespace OWSManagement.DTOs
{
    public class SetCharacterFlagsDTO
    {
        public int CharacterID { get; set; }

        /// <summary>
        /// Grants the in-game admin flag the client reads at login. Omit to leave unchanged.
        /// </summary>
        public bool? IsAdmin { get; set; }

        /// <summary>
        /// Grants the in-game moderator flag. Omit to leave unchanged.
        /// </summary>
        public bool? IsModerator { get; set; }

        /// <summary>
        /// Makes the server hand this character 127.0.0.1 instead of the zone server's real IP
        /// when it connects. Omit to leave unchanged.
        /// </summary>
        public bool? IsInternalNetworkTestUser { get; set; }
    }
}
