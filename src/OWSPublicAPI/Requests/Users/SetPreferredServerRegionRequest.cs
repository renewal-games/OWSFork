using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Threading.Tasks;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;

namespace OWSPublicAPI.Requests.Users
{
    /// <summary>
    /// Pins the signed-in account to a server region, which is what routes it to one launcher host.
    /// </summary>
    /// <remarks>
    /// Called by the login screen when the player presses Enter on the server selector, before
    /// characters are fetched. The region is stored on the User rather than the session because the
    /// paths that reach GetServerToConnectTo do not all carry a UserSessionGUID (the InstanceManagement
    /// twin of that request has no session field at all), while the character name is always present.
    /// </remarks>
    public class SetPreferredServerRegionRequest : IRequestHandler<SetPreferredServerRegionRequest, IActionResult>, IRequest
    {
        public Guid UserSessionGUID { get; set; }
        public string Region { get; set; }

        private SuccessAndErrorMessage output;
        private Guid customerGUID;
        private IUsersRepository usersRepository;
        private IOptions<OWSPublicAPI.Options.GameServersOptions> gameServersOptions;

        public void SetData(IUsersRepository usersRepository, IOptions<OWSPublicAPI.Options.GameServersOptions> gameServersOptions, IHeaderCustomerGUID customerGuid)
        {
            customerGUID = customerGuid.CustomerGUID;
            this.usersRepository = usersRepository;
            this.gameServersOptions = gameServersOptions;
        }

        public async Task<IActionResult> Handle()
        {
            output = new SuccessAndErrorMessage();

            //The server list is the only definition of which regions exist. Storing the CONFIGURED
            //spelling rather than the caller's is what keeps the routing queries - which compare with
            //exact, case-sensitive equality in Postgres - from stranding an account on "sea" vs "SEA".
            string canonicalRegion = gameServersOptions.Value.Servers
                .Select(s => s.Region)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)
                    && string.Equals(r.Trim(), (Region ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(canonicalRegion))
            {
                output.Success = false;
                output.ErrorMessage = $"SetPreferredServerRegion: Unknown server region: {Region}";

                return new OkObjectResult(output);
            }

            GetUserSession userSession = await usersRepository.GetUserSession(customerGUID, UserSessionGUID);

            if (userSession == null || !userSession.UserGuid.HasValue)
            {
                output.Success = false;
                output.ErrorMessage = "SetPreferredServerRegion: Invalid or expired User Session.";

                return new OkObjectResult(output);
            }

            output = await usersRepository.SetPreferredServerRegion(customerGUID, userSession.UserGuid.Value, canonicalRegion.Trim());

            return new OkObjectResult(output);
        }
    }
}
