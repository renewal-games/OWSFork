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
                await charactersRepository.UpdateCharacterQuests(customerGUID, CharacterName, CharQuests);

                // The inventory write can now fail its own guard (missing CharInventory row, and
                // stale_revision once callers opt in). It used to return void, so a refused bag save
                // reported success to the zone server; surface it instead.
                // A missing revision under RequireExpectedRevision refuses only the bag, the same way
                // a stale_revision would; quests and stats are not economy state and still save.
                long? expectedRevision = ExpectedRevision >= 0 ? ExpectedRevision : null;
                UpdateCharacterInventoryResponse inventoryResult =
                    expectedRevision == null && economyOptions.RequireExpectedRevision
                        ? new UpdateCharacterInventoryResponse { Success = false, ReasonCode = "revision_required" }
                        : await charactersRepository.UpdateCharacterInventory(customerGUID, CharacterName, CharInventory, expectedRevision);
                successAndErrorMessage.NewEconomyRevision = inventoryResult.NewEconomyRevision;
                successAndErrorMessage.ReasonCode = inventoryResult.ReasonCode;

                // Deliberately NOT an early return. These three are separate transactions, so
                // bailing here would leave quests committed and silently drop the stats save
                // forever — a refused bag must not also cost the player their stats. Run the
                // remaining write, then report the inventory failure.
                await charactersRepository.UpdateCharacterStats(customerGUID, CharacterName, CharStats);

                if (!inventoryResult.Success)
                {
                    successAndErrorMessage.Success = false;
                    successAndErrorMessage.ErrorMessage = $"UpdateCharacterInventory failed: {inventoryResult.ReasonCode}";
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
