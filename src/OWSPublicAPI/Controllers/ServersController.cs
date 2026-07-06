using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OWSPublicAPI.Options;

namespace OWSPublicAPI.Controllers
{
    /// <summary>
    /// Public game-server list API.
    /// </summary>
    /// <remarks>
    /// Serves the server-selector list shown on the login screen. Entries come from the
    /// GameServersConfig appsettings section; population is config-stubbed for now.
    /// </remarks>
    [Route("api/[controller]")]
    [ApiController]
    public class ServersController : Controller
    {
        private readonly IOptions<GameServersOptions> _gameServersOptions;

        /// <summary>
        /// Constructor for public game-server list API.
        /// </summary>
        public ServersController(IOptions<GameServersOptions> gameServersOptions)
        {
            _gameServersOptions = gameServersOptions;
        }

        /// <summary>
        /// Get the list of game servers for the server selector.
        /// </summary>
        [HttpGet]
        [Route("List")]
        public IActionResult List()
        {
            var servers = _gameServersOptions.Value.Servers
                .Select(s => new
                {
                    s.Name,
                    s.Region,
                    s.Status,
                    s.Population,
                    s.PingHost
                });

            return new OkObjectResult(new { Servers = servers });
        }
    }
}
