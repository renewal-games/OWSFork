using System;

namespace OWSPublicAPI.Options
{
    public class SteamOptions
    {
        public const string SectionName = "SteamConfig";

        // Steam Web API host. Regular keys use https://api.steampowered.com;
        // switch to https://partner.steam-api.com once a publisher key is provisioned.
        public string WebApiHost { get; set; } = "https://api.steampowered.com";
        public string WebApiKey { get; set; }
        public string AppId { get; set; }
        // Must match the identity string the client passes to GetAuthTicketForWebApi ("WebAPI:<Identity>").
        public string Identity { get; set; } = "samsarasaga";
    }
}
