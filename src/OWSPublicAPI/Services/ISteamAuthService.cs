using System.Threading.Tasks;

namespace OWSPublicAPI.Services
{
    public class SteamTicketValidationResult
    {
        public bool IsValid { get; set; }
        public string SteamId { get; set; }
        public bool VacBanned { get; set; }
        public bool PublisherBanned { get; set; }
        public string ErrorMessage { get; set; } = "";
    }

    public interface ISteamAuthService
    {
        /// <summary>
        /// Validates a WebAPI auth ticket (from ISteamUser::GetAuthTicketForWebApi) against
        /// ISteamUserAuth/AuthenticateUserTicket and returns the owning SteamId + ban flags.
        /// </summary>
        Task<SteamTicketValidationResult> ValidateAuthTicket(string ticketHex);

        /// <summary>
        /// Fetches the player's persona (display) name via ISteamUser/GetPlayerSummaries.
        /// Returns null on any failure; callers should fall back to a default.
        /// </summary>
        Task<string> GetPersonaName(string steamId);
    }
}
