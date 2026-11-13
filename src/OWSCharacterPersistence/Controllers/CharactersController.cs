using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.Options;
using OWSCharacterPersistence.Requests.Characters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OWSData.Models.Tables;
using OWSData.Models.Composites;

namespace OWSCharacterPersistence.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CharactersController : Controller
    {
        private readonly ICharactersRepository _charactersRepository;
        private readonly IJoinTicketsRepository _joinTicketsRepository;
        private readonly ICharacterOwnersRepository _ownersRepository;
        private readonly IHeaderCustomerGUID _customerGuid;
        private readonly EconomyOptions _economyOptions;

        public CharactersController(ICharactersRepository charactersRepository,
            IJoinTicketsRepository joinTicketsRepository,
            ICharacterOwnersRepository ownersRepository,
            IHeaderCustomerGUID customerGuid,
            IOptions<EconomyOptions> economyOptions)
        {
            _economyOptions = economyOptions.Value;
            _charactersRepository = charactersRepository;
            _joinTicketsRepository = joinTicketsRepository;
            _ownersRepository = ownersRepository;
            _customerGuid = customerGuid;
        }

        // Every save names its character; once the character has an owner, only the owner's token may write it.
        // The service key is checked before this, by RequireServiceKeyMiddleware (see Startup).
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            foreach (object argument in context.ActionArguments.Values)
            {
                // A replaced owner's logout would delete the new session's map instance row.
                if (argument is IOwnedCharacterWrite write && !String.IsNullOrEmpty(write.OwnedCharacterName)
                    && await _ownersRepository.IsStaleOwnerWrite(_customerGuid.CustomerGUID, write.OwnedCharacterName, write.OwnerToken,
                        allowPreviousOwner: argument is not PlayerLogoutRequest))
                {
                    context.Result = new OkObjectResult(new StaleOwnerResult());
                    return;
                }
            }

            await base.OnActionExecutionAsync(context, next);
        }

        [HttpPost]
        [Route("GetSaveDataByName")]
        [Produces(typeof(CharacterSaveData))]
        public async Task<IActionResult> GetSaveDataByName([FromBody] GetSaveDataByNameRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("GetStatsByName")]
        [Produces(typeof(GetCharStatsByCharName))]
        public async Task<IActionResult> GetStatsByName([FromBody] GetStatsByNameRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("GetQuestsByName")]
        [Produces(typeof(GetCharQuestsByCharName))]
        public async Task<IActionResult> GetQuestsByName([FromBody] GetQuestsByNameRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("GetInventoryByName")]
        [Produces(typeof(GetCharInventoryByCharName))]
        public async Task<IActionResult> GetInventoryByName([FromBody] GetInventoryByNameRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("GetByName")]
        [Produces(typeof(GetCharByCharName))]
        public async Task<IActionResult> GetByName([FromBody] GetByNameRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        // The zone server learns who joins from this reply.
        [HttpPost]
        [Route("RedeemJoinTicket")]
        [Produces(typeof(RedeemJoinTicketResult))]
        public async Task<IActionResult> RedeemJoinTicket([FromBody] RedeemJoinTicketRequest request)
        {
            if (!SamsaraServiceKey.IsValid(Request)) return Unauthorized();
            request.SetData(_joinTicketsRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("ClaimCharacter")]
        [Produces(typeof(ClaimCharacterResult))]
        public async Task<IActionResult> ClaimCharacter([FromBody] ClaimCharacterRequest request)
        {
            if (!SamsaraServiceKey.IsValid(Request)) return Unauthorized();
            request.SetData(_ownersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("CancelJoinTicket")]
        [Produces(typeof(CancelJoinTicketResult))]
        public async Task<IActionResult> CancelJoinTicket([FromBody] CancelJoinTicketRequest request)
        {
            if (!SamsaraServiceKey.IsValid(Request)) return Unauthorized();
            request.SetData(_ownersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("CheckOwners")]
        [Produces(typeof(CheckOwnersResult))]
        public async Task<IActionResult> CheckOwners([FromBody] CheckOwnersRequest request)
        {
            if (!SamsaraServiceKey.IsValid(Request)) return Unauthorized();
            request.SetData(_ownersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("UpdateAllPlayerPositions")]
        [Produces(typeof(SuccessAndErrorMessage))]
        public async Task<SuccessAndErrorMessage> UpdateAllPlayerPositions([FromBody] UpdateAllPlayerPositionsRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("UpdateCharacterData")]
        [Produces(typeof(UpdateCharacterDataResponse))]
        public async Task<UpdateCharacterDataResponse> UpdateCharacterData([FromBody] UpdateCharacterDataRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid, _economyOptions);
            return await request.Handle();
        }

        [HttpPost]
        [Route("UpdateCharacterStats")]
        [Produces(typeof(SuccessAndErrorMessage))]
        public async Task<SuccessAndErrorMessage> UpdateCharacterStats([FromBody] UpdateCharacterStatsRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("UpdateCharacterQuests")]
        [Produces(typeof(SuccessAndErrorMessage))]
        public async Task<SuccessAndErrorMessage> UpdateCharacterQuests([FromBody] UpdateCharacterQuestsRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("UpdateCharacterInventory")]
        [Produces(typeof(UpdateCharacterInventoryResponse))]
        public async Task<UpdateCharacterInventoryResponse> UpdateCharacterInventory([FromBody] UpdateCharacterInventoryRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid, _economyOptions);
            return await request.Handle();
        }

        [HttpPost]
        [Route("UpdateCharacterCurrency")]
        [Produces(typeof(UpdateCharacterCurrencyResponse))]
        public async Task<UpdateCharacterCurrencyResponse> UpdateCharacterCurrency([FromBody] UpdateCharacterCurrencyRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid, _economyOptions);
            return await request.Handle();
        }

        [HttpPost]
        [Route("UpdateCharacterClass")]
        [Produces(typeof(SuccessAndErrorMessage))]
        public async Task<SuccessAndErrorMessage> UpdateCharacterClass([FromBody] UpdateCharacterClassRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("UpdateCharacterAbilities")]
        [Produces(typeof(SuccessAndErrorMessage))]
        public async Task<SuccessAndErrorMessage> UpdateCharacterAbilities([FromBody] UpdateCharacterAbilitiesRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("GetCustomData")]
        [Produces(typeof(CustomCharacterDataRows))]
        public async Task<CustomCharacterDataRows> GetCustomData([FromBody] GetCustomDataRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("AddOrUpdateCustomData")]
        public async Task<IActionResult> AddOrUpdateCustomData([FromBody] AddOrUpdateCustomDataRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            string refusal = await request.Handle();

            return refusal == null ? new EmptyResult() : Ok(new StaleOwnerResult());
        }

        [HttpPost]
        [Route("PlayerLogout")]
        [Produces(typeof(SuccessAndErrorMessage))]
        public async Task<SuccessAndErrorMessage> PlayerLogout([FromBody] PlayerLogoutRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            SuccessAndErrorMessage result = await request.Handle();
            return result;
        }

        [HttpPost]
        [Route("AddQuestListToDatabase")]
        [Produces(typeof(SuccessAndErrorMessage))]
        public async Task<SuccessAndErrorMessage> AddQuestListToDatabase([FromBody] AddQuestListToDatabaseRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("GetQuestsFromDatabase")]
        [Produces(typeof(GetQuestsFromDb))]
        public async Task<IActionResult> GetQuestsFromDatabase(GetQuestsFromDatabaseRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }

        [HttpPost]
        [Route("GetAbilitiesByName")]
        [Produces(typeof(IEnumerable<CharacterAbilityDto>))]
        public async Task<IActionResult> GetAbilitiesByName([FromBody] GetAbilitiesByNameRequest request)
        {
            request.SetData(_charactersRepository, _customerGuid);
            return await request.Handle();
        }
    }
}
