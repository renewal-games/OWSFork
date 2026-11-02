using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSShared.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class UpdateCharacterDataRequest : IOwnedCharacterWrite
    {
        public string CharacterName { get; set; }
        public Guid? OwnerToken { get; set; }
        string IOwnedCharacterWrite.OwnedCharacterName => CharacterName;
       
        public IEnumerable<UpdateCharacterQuest> CharQuests { get; set; }

        public IEnumerable<UpdateCharacterInventory> CharInventory { get; set; }

        public IEnumerable<UpdateCharacterStats> CharStats { get; set; }

        // The Characters.EconomyRevision CharInventory was computed from; same contract as
        // UpdateCharacterInventoryRequest.ExpectedRevision. This is the periodic autosave, so it is the
        // write most likely to land a stale bag over a shop transaction that just committed.
        public long? ExpectedRevision { get; set; }

        // MapInstanceID of the zone server sending this save. The backend refuses the write if the
        // character has since been handed to a different instance. Omit for legacy callers.
        public int? ZoneInstanceID { get; set; }

        private Guid customerGUID;
        private ICharactersRepository charactersRepository;
        private EconomyOptions economyOptions;

        public void SetData(ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid, EconomyOptions economyOptions)
        {
            this.charactersRepository = charactersRepository;
            customerGUID = customerGuid.CustomerGUID;
            this.economyOptions = economyOptions;
        }

        public async Task<UpdateCharacterDataResponse> Handle()
        {
            UpdateCharacterDataResponse successAndErrorMessage = new UpdateCharacterDataResponse();
            successAndErrorMessage.Success = true;

            try
            {
                // One transaction: quests, inventory and stats are saved together. A missing section is
                // refused rather than treated as empty, which would wipe the bag. The economy revision
                // guard (ExpectedRevision / RequireExpectedRevision) refuses only the bag, as it did when
                // these were separate saves: quests and stats are not economy state and still commit.
                long? expectedRevision = ExpectedRevision >= 0 ? ExpectedRevision : null;
                UpdateCharacterDataResponse result = await charactersRepository.UpdateCharacterData(customerGUID, CharacterName,
                    CharQuests, CharInventory, CharStats, OwnerToken, ZoneInstanceID, expectedRevision, economyOptions.RequireExpectedRevision);

                successAndErrorMessage.NewEconomyRevision = result.NewEconomyRevision;
                successAndErrorMessage.ReasonCode = result.ReasonCode;

                if (result.ReasonCode == "stale_owner")
                {
                    // A claim committed after the controller's owner check: nothing was written. Reported exactly
                    // as the controller reports a stale owner, so the zone server handles both the same way.
                    successAndErrorMessage.Success = false;
                    successAndErrorMessage.ErrorMessage = "stale_owner";
                }
                else if (result.ReasonCode == "stale_zone")
                {
                    // Success stays true, as in UpdateCharacterStats: the caller must not retry a write
                    // that was refused because a newer zone owns the character.
                    successAndErrorMessage.ErrorMessage = "stale_zone";
                }
                else if (!result.Success)
                {
                    successAndErrorMessage.Success = false;
                    successAndErrorMessage.ErrorMessage = $"UpdateCharacterData failed: {result.ReasonCode}";
                }
            }
            catch (Exception ex)
            {
                successAndErrorMessage.ErrorMessage = ex.Message;
                successAndErrorMessage.Success = false;
            }

            return successAndErrorMessage;
        }
    }
}
