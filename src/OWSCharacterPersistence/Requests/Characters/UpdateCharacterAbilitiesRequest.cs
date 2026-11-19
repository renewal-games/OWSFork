using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Models.Composites;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class UpdateCharacterAbilitiesRequest : IOwnedCharacterWrite
    {
        // Payload from Unreal
        public string CharacterName { get; set; } = default!;
        public Guid? OwnerToken { get; set; }
        string IOwnedCharacterWrite.OwnedCharacterName => CharacterName;
        public List<CharacterAbilityDto> CharacterAbilities { get; set; } = new();

        // Orders this save against the token's other stat, quest and ability saves, per ability; same contract as
        // UpdateCharacterStatsRequest.SaveSequence. Without it the token's saves are ordered by receipt.
        public long? SaveSequence { get; set; }

        // As in UpdateCharacterStatsRequest: when model binding built this request; no body can set it.
        private readonly DateTime _receivedAt = SaveClock.Next();

        private ICharactersRepository _repo = default!;
        private IHeaderCustomerGUID _cust = default!;

        public void SetData(ICharactersRepository repo, IHeaderCustomerGUID cust)
        {
            _repo = repo;
            _cust = cust;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            var json = JsonSerializer.Serialize(
                            CharacterAbilities,
                            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            string refusal = await _repo.UpdateCharacterAbilities(
                    _cust.CustomerGUID,
                    CharacterName,
                    json,
                    OwnerToken,
                    new SaveOrder(SaveSequence, _receivedAt));

            if (refusal == "stale_owner")
            {
                return new StaleOwnerResult();
            }

            if (refusal == "save_sequence_required")
            {
                // Nothing was written: this token sends SaveSequence numbers and this save had none.
                return new SuccessAndErrorMessage { Success = false, ErrorMessage = refusal };
            }

            if (refusal == "character_not_found" && CharacterAbilities.Count > 0)
            {
                return new SuccessAndErrorMessage
                {
                    Success = false,
                    ErrorMessage = "Character not found."
                };
            }

            return new SuccessAndErrorMessage
            {
                Success = true,
                ErrorMessage = string.Empty
            };
        }
    }
}