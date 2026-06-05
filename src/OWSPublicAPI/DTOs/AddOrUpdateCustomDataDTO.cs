using OWSData.Models.StoredProcs;

namespace OWSPublicAPI.DTOs
{
    /// <summary>
    /// Public custom character data write request.
    /// </summary>
    public class AddOrUpdateCustomDataDTO
    {
        /// <summary>
        /// Authenticated user session.
        /// </summary>
        public string UserSessionGUID { get; set; }

        /// <summary>
        /// Custom data payload to update.
        /// </summary>
        public AddOrUpdateCustomCharacterData AddOrUpdateCustomCharacterData { get; set; }
    }
}
