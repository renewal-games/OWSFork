namespace OWSPublicAPI.DTOs
{
    /// <summary>
    /// Public custom character data request.
    /// </summary>
    public class GetCustomDataDTO
    {
        /// <summary>
        /// Authenticated user session.
        /// </summary>
        public string UserSessionGUID { get; set; }

        /// <summary>
        /// Character name to read custom data for.
        /// </summary>
        public string CharacterName { get; set; }
    }
}
