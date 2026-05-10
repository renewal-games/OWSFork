using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OWSData.Repositories.Interfaces;
using OWSParty.Service;
using OWSShared.Interfaces;
using PartyServiceApp.Protos;

namespace OWSParty.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PartyController : ControllerBase
    {

        private readonly ILogger<PartyController> _logger;
        private readonly ICharactersRepository _charactersRepository;
        private readonly IHeaderCustomerGUID _customerGuid;

        public PartyController(ILogger<PartyController> logger, ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid)
        {
            _logger = logger;
            _charactersRepository = charactersRepository;
            _customerGuid = customerGuid;
        }

        [HttpGet]
        [Route("Names/Available")]
        [Produces(typeof(PartyNameAvailabilityResponse))]
        public async Task<IActionResult> IsPartyNameAvailable([FromQuery] string partyName)
        {
            if (_customerGuid.CustomerGUID == Guid.Empty)
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(partyName))
            {
                return BadRequest("partyName is required.");
            }

            string normalizedPartyName = partyName.Trim();
            bool isAvailable = await _charactersRepository.IsPartyNameAvailable(_customerGuid.CustomerGUID, normalizedPartyName);

            return Ok(new PartyNameAvailabilityResponse
            {
                PartyName = normalizedPartyName,
                IsAvailable = isAvailable
            });
        }

        [HttpGet]
        [Route("Names/Generate")]
        [Produces(typeof(PartyNameGenerationResponse))]
        public async Task<IActionResult> GeneratePartyName()
        {
            if (_customerGuid.CustomerGUID == Guid.Empty)
            {
                return Unauthorized();
            }

            string partyName = await _charactersRepository.GenerateAvailablePartyName(_customerGuid.CustomerGUID);

            return Ok(new PartyNameGenerationResponse
            {
                PartyName = partyName
            });
        }

        [HttpPut]
        [Route("{partyGuid}/Description")]
        [Produces(typeof(UpdatePartyDescriptionResponse))]
        public async Task<IActionResult> UpdatePartyDescription([FromRoute] string partyGuid, [FromBody] UpdatePartyDescriptionRequest request)
        {
            if (_customerGuid.CustomerGUID == Guid.Empty)
            {
                return Unauthorized();
            }

            if (!Guid.TryParse(partyGuid, out Guid parsedPartyGuid) || parsedPartyGuid == Guid.Empty)
            {
                return BadRequest("A valid partyGuid is required.");
            }

            if (request == null)
            {
                return BadRequest("Request body is required.");
            }

            if (string.IsNullOrWhiteSpace(request.ActorCharName) && string.IsNullOrWhiteSpace(request.ActorCharGuid))
            {
                return BadRequest("actorCharName or actorCharGuid is required.");
            }

            try
            {
                PartyToSend party = await _charactersRepository.UpdatePartyDescription(
                    _customerGuid.CustomerGUID,
                    parsedPartyGuid,
                    request.ActorCharName,
                    request.ActorCharGuid,
                    request.PartyDescription);

                await PartyService.BroadcastPartyUpdate(party.PartyMembers, party);

                return Ok(new UpdatePartyDescriptionResponse
                {
                    PartyGuid = party.PartyInfo?.PartyGuid ?? string.Empty,
                    PartyName = party.PartyInfo?.PartyName ?? string.Empty,
                    PartyDescription = party.PartyInfo?.PartyDescription ?? string.Empty
                });
            }
            catch (NotSupportedException ex)
            {
                return StatusCode(501, ex.Message);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update party description for {PartyGuid}.", partyGuid);
                return StatusCode(500, "Party description update failed.");
            }
        }

        [HttpPut]
        [Route("{partyGuid}/ExpDistribution")]
        [Produces(typeof(UpdatePartyExpDistributionResponse))]
        public async Task<IActionResult> UpdatePartyExpDistribution([FromRoute] string partyGuid, [FromBody] UpdatePartyExpDistributionRequest request)
        {
            if (_customerGuid.CustomerGUID == Guid.Empty)
            {
                return Unauthorized();
            }

            if (!Guid.TryParse(partyGuid, out Guid parsedPartyGuid) || parsedPartyGuid == Guid.Empty)
            {
                return BadRequest("A valid partyGuid is required.");
            }

            if (request == null)
            {
                return BadRequest("Request body is required.");
            }

            if (string.IsNullOrWhiteSpace(request.ActorCharName) && string.IsNullOrWhiteSpace(request.ActorCharGuid))
            {
                return BadRequest("actorCharName or actorCharGuid is required.");
            }

            try
            {
                PartyToSend party = await _charactersRepository.UpdatePartyExpDistribution(
                    _customerGuid.CustomerGUID,
                    parsedPartyGuid,
                    request.ActorCharName,
                    request.ActorCharGuid,
                    request.ExpDistributionMode);

                await PartyService.BroadcastPartyUpdate(party.PartyMembers, party);

                return Ok(new UpdatePartyExpDistributionResponse
                {
                    PartyGuid = party.PartyInfo?.PartyGuid ?? string.Empty,
                    PartyName = party.PartyInfo?.PartyName ?? string.Empty,
                    ExpDistributionMode = party.PartyInfo?.ExpDistributionMode ?? 0
                });
            }
            catch (NotSupportedException ex)
            {
                return StatusCode(501, ex.Message);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update party exp distribution for {PartyGuid}.", partyGuid);
                return StatusCode(500, "Party exp distribution update failed.");
            }
        }

        [HttpPut]
        [Route("{partyGuid}/LootDistribution")]
        [Produces(typeof(UpdatePartyLootDistributionResponse))]
        public async Task<IActionResult> UpdatePartyLootDistribution([FromRoute] string partyGuid, [FromBody] UpdatePartyLootDistributionRequest request)
        {
            if (_customerGuid.CustomerGUID == Guid.Empty)
            {
                return Unauthorized();
            }

            if (!Guid.TryParse(partyGuid, out Guid parsedPartyGuid) || parsedPartyGuid == Guid.Empty)
            {
                return BadRequest("A valid partyGuid is required.");
            }

            if (request == null)
            {
                return BadRequest("Request body is required.");
            }

            if (string.IsNullOrWhiteSpace(request.ActorCharName) && string.IsNullOrWhiteSpace(request.ActorCharGuid))
            {
                return BadRequest("actorCharName or actorCharGuid is required.");
            }

            try
            {
                PartyToSend party = await _charactersRepository.UpdatePartyLootDistribution(
                    _customerGuid.CustomerGUID,
                    parsedPartyGuid,
                    request.ActorCharName,
                    request.ActorCharGuid,
                    request.LootDistributionMode);

                await PartyService.BroadcastPartyUpdate(party.PartyMembers, party);

                return Ok(new UpdatePartyLootDistributionResponse
                {
                    PartyGuid = party.PartyInfo?.PartyGuid ?? string.Empty,
                    PartyName = party.PartyInfo?.PartyName ?? string.Empty,
                    LootDistributionMode = party.PartyInfo?.LootDistributionMode ?? 0
                });
            }
            catch (NotSupportedException ex)
            {
                return StatusCode(501, ex.Message);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update party loot distribution for {PartyGuid}.", partyGuid);
                return StatusCode(500, "Party loot distribution update failed.");
            }
        }
    }

    public class PartyNameAvailabilityResponse
    {
        public string PartyName { get; set; }
        public bool IsAvailable { get; set; }
    }

    public class PartyNameGenerationResponse
    {
        public string PartyName { get; set; }
    }

    public class UpdatePartyDescriptionRequest
    {
        public string ActorCharName { get; set; }
        public string ActorCharGuid { get; set; }
        public string PartyDescription { get; set; }
    }

    public class UpdatePartyDescriptionResponse
    {
        public string PartyGuid { get; set; }
        public string PartyName { get; set; }
        public string PartyDescription { get; set; }
    }

    public class UpdatePartyExpDistributionRequest
    {
        public string ActorCharName { get; set; }
        public string ActorCharGuid { get; set; }
        public int ExpDistributionMode { get; set; }
    }

    public class UpdatePartyExpDistributionResponse
    {
        public string PartyGuid { get; set; }
        public string PartyName { get; set; }
        public int ExpDistributionMode { get; set; }
    }

    public class UpdatePartyLootDistributionRequest
    {
        public string ActorCharName { get; set; }
        public string ActorCharGuid { get; set; }
        public int LootDistributionMode { get; set; }
    }

    public class UpdatePartyLootDistributionResponse
    {
        public string PartyGuid { get; set; }
        public string PartyName { get; set; }
        public int LootDistributionMode { get; set; }
    }
}
