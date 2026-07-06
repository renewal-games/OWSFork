using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OWSPublicAPI.Options;

namespace OWSPublicAPI.Services
{
    public class SteamAuthService : ISteamAuthService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SteamOptions _steamOptions;
        private readonly ILogger<SteamAuthService> _logger;

        public SteamAuthService(IHttpClientFactory httpClientFactory, IOptions<SteamOptions> steamOptions, ILogger<SteamAuthService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _steamOptions = steamOptions.Value;
            _logger = logger;
        }

        public async Task<SteamTicketValidationResult> ValidateAuthTicket(string ticketHex)
        {
            var result = new SteamTicketValidationResult();

            if (string.IsNullOrWhiteSpace(ticketHex))
            {
                result.ErrorMessage = "Missing Steam auth ticket";
                return result;
            }

            if (string.IsNullOrWhiteSpace(_steamOptions.WebApiKey) || string.IsNullOrWhiteSpace(_steamOptions.AppId))
            {
                _logger.LogError("SteamConfig is incomplete (WebApiKey/AppId missing) - rejecting Steam login");
                result.ErrorMessage = "Steam login is not configured";
                return result;
            }

            try
            {
                var client = _httpClientFactory.CreateClient("SteamWebAPI");
                var url = $"{_steamOptions.WebApiHost.TrimEnd('/')}/ISteamUserAuth/AuthenticateUserTicket/v1/" +
                          $"?key={Uri.EscapeDataString(_steamOptions.WebApiKey)}" +
                          $"&appid={Uri.EscapeDataString(_steamOptions.AppId)}" +
                          $"&ticket={Uri.EscapeDataString(ticketHex)}" +
                          $"&identity={Uri.EscapeDataString(_steamOptions.Identity ?? "")}";

                using var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // Do not log the ticket or the full URL (contains the key).
                    _logger.LogWarning("AuthenticateUserTicket returned HTTP {StatusCode}", (int)response.StatusCode);
                    result.ErrorMessage = "Steam ticket validation failed";
                    return result;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("response", out var responseElement))
                {
                    result.ErrorMessage = "Unexpected response from Steam";
                    return result;
                }

                if (responseElement.TryGetProperty("error", out var errorElement))
                {
                    var errorDesc = errorElement.TryGetProperty("errordesc", out var desc) ? desc.GetString() : "unknown";
                    _logger.LogWarning("AuthenticateUserTicket error: {ErrorDesc}", errorDesc);
                    result.ErrorMessage = "Steam ticket rejected";
                    return result;
                }

                if (!responseElement.TryGetProperty("params", out var paramsElement) ||
                    !paramsElement.TryGetProperty("result", out var resultElement) ||
                    resultElement.GetString() != "OK")
                {
                    result.ErrorMessage = "Steam ticket rejected";
                    return result;
                }

                result.SteamId = paramsElement.TryGetProperty("steamid", out var steamIdElement) ? steamIdElement.GetString() : null;
                result.VacBanned = paramsElement.TryGetProperty("vacbanned", out var vac) && vac.GetBoolean();
                result.PublisherBanned = paramsElement.TryGetProperty("publisherbanned", out var pub) && pub.GetBoolean();

                if (string.IsNullOrWhiteSpace(result.SteamId))
                {
                    result.ErrorMessage = "Steam ticket rejected";
                    return result;
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AuthenticateUserTicket call failed");
                result.ErrorMessage = "Steam ticket validation failed";
                return result;
            }
        }

        public async Task<string> GetPersonaName(string steamId)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("SteamWebAPI");
                var url = $"{_steamOptions.WebApiHost.TrimEnd('/')}/ISteamUser/GetPlayerSummaries/v2/" +
                          $"?key={Uri.EscapeDataString(_steamOptions.WebApiKey)}" +
                          $"&steamids={Uri.EscapeDataString(steamId)}";

                using var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("response", out var responseElement) &&
                    responseElement.TryGetProperty("players", out var playersElement) &&
                    playersElement.GetArrayLength() > 0 &&
                    playersElement[0].TryGetProperty("personaname", out var personaElement))
                {
                    return personaElement.GetString();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetPlayerSummaries call failed for SteamId {SteamId}", steamId);
                return null;
            }
        }
    }
}
