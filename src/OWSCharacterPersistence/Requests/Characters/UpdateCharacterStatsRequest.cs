using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class UpdateCharacterStatsRequest : IOwnedCharacterWrite
    {
        public string CharacterName { get; set; }
        public Guid? OwnerToken { get; set; }
        string IOwnedCharacterWrite.OwnedCharacterName => CharacterName;
       
        public IEnumerable<UpdateCharacterStats> CharacterStats { get; set; }

        // MapInstanceID of the zone server sending this save. The backend refuses the write if the
        // character has since been handed to a different instance, so an in-flight save from the zone
        // the player just left cannot overwrite the destination's newer state. Omit for legacy callers.
        public int? ZoneInstanceID { get; set; }

        // Where this snapshot falls in the owner token's save order: take the next number when the snapshot is built,
        // from one counter shared by the stats, quests, abilities and UpdateCharacterData saves, and send the same
        // number again on a retry. A stat whose stored value came from a higher or equal number of the same token is
        // left alone, so a save that reached the database late cannot put back older values. Requires OwnerToken.
        // Omit (or send 0) for legacy last-write-wins. See ICharactersRepository.UpdateCharacterStats.
        public long? SaveSequence { get; set; }

        private Guid customerGUID;
        private ICharactersRepository charactersRepository;

        public void SetData(ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid)
        {
            this.charactersRepository = charactersRepository;
            customerGUID = customerGuid.CustomerGUID;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            SuccessAndErrorMessage successAndErrorMessage = new SuccessAndErrorMessage();
            successAndErrorMessage.Success = true;

            try
            {
                string refusal = await charactersRepository.UpdateCharacterStats(customerGUID, CharacterName, CharacterStats, OwnerToken, ZoneInstanceID, SaveSequence);
                if (refusal == "stale_owner")
                {
                    return new StaleOwnerResult();
                }

                if (refusal == "stale_zone")
                {
                    // Success stays true: the caller is fire-and-forget and must not retry a write that
                    // was refused precisely because a newer zone owns the character.
                    successAndErrorMessage.ErrorMessage = "stale_zone";
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
