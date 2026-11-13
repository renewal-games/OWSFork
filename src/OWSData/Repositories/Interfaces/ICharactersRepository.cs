using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GuildServiceApp.Protos;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Models.Tables;
using PartyServiceApp.Protos;

namespace OWSData.Repositories.Interfaces
{
    public interface ICharactersRepository
    {
        Task AddCharacterToMapInstanceByCharName(Guid customerGUID, string characterName, int mapInstanceID);
        Task ReleaseCharacterMapReservation(Guid customerGUID, string characterName, int mapInstanceID);
        // For a spin-up whose command never reached a launcher: deletes the zone instance row and every reservation on
        // it, but only while the row is still waiting for its server (Status 1). Returns whether the row was deleted.
        // Without this the row stays for up to two minutes and each joiner who finds it waits on it instead of spinning
        // up a new instance.
        Task<bool> RemoveUnstartedMapInstance(Guid customerGUID, int mapInstanceID);
        // For a write the caller has already tied to the player's own session (the public API). A zone save uses
        // AddOrUpdateOwnedCustomCharacterData.
        Task AddOrUpdateCustomCharacterData(Guid customerGUID, AddOrUpdateCustomCharacterData addOrUpdateCustomCharacterData);
        // The standalone zone saves below return null when written, else why not: "character_not_found", "stale_owner"
        // (Postgres: the character has an owner and ownerToken is neither that owner nor the one a running handover
        // replaced) or, where a zone instance is passed, "stale_zone".
        Task<string> AddOrUpdateOwnedCustomCharacterData(Guid customerGUID, AddOrUpdateCustomCharacterData addOrUpdateCustomCharacterData, Guid? ownerToken);
        Task<MapInstances> CheckMapInstanceStatus(Guid customerGUID, int mapInstanceID);
        Task CleanUpInstances(Guid customerGUID);
        Task<CharacterSaveData> GetSaveDataByCharName(Guid customerGUID, string characterName);
        Task<IEnumerable<CustomCharacterData>> GetCustomCharacterData(Guid customerGUID, string characterName);
        Task<IEnumerable<GetCharStatsByCharName>> GetCharStatsByCharName(Guid customerGUID, string characterName);
        Task<IEnumerable<GetCharQuestsByCharName>> GetCharQuetsByCharName(Guid customerGUID, string characterName);
        Task<IEnumerable<GetCharInventoryByCharName>> GetCharInventoryByCharName(Guid customerGUID, string characterName);
        Task<GetCharByCharName> GetCharByCharName(Guid customerGUID, string characterName);
        Task<JoinMapByCharName> JoinMapByCharName(Guid customerGUID, string characterName, string zoneName, int playerGroupType);
        // callerZoneInstanceId is the MapInstanceID of the zone server issuing the save. When supplied,
        // the write is refused if the character has since been handed to a different instance. Returns
        // false when refused. Null keeps the legacy last-write-wins behaviour.
        Task<string> UpdateCharacterStats(Guid customerGUID, string characterName, IEnumerable<UpdateCharacterStats> updateCharacterStats, Guid? ownerToken, int? callerZoneInstanceId = null);
        Task<string> UpdateCharacterQuests(Guid customerGUID, string characterName, IEnumerable<UpdateCharacterQuest> updateCharacterQuests, Guid? ownerToken, int? callerZoneInstanceId = null);
        // Whole-bag rewrite. expectedRevision is opt-in: pass the Characters.EconomyRevision the bag
        // was computed from to have a stale snapshot rejected instead of clobbering a shop
        // transaction that committed in the meantime; pass null for legacy last-write-wins.
        // ownerToken is the caller's CharacterOwners token. Once the character has an owner, a save whose token is
        // neither that owner nor the one a running handover replaced is refused with "stale_owner" and writes nothing.
        // It is checked under the Characters row lock, so a claim cannot commit between the check and the write.
        // A null token passes only while the character has no owner (legacy builds).
        Task<UpdateCharacterInventoryResponse> UpdateCharacterInventory(Guid customerGUID, string characterName, IEnumerable<UpdateCharacterInventory> updateCharacterInventory, Guid? ownerToken, long? expectedRevision = null);
        // Quests, inventory and stats in one transaction. Every section is required (an empty list is a
        // valid empty section, null is rejected) because the inventory write replaces the whole bag.
        // ownerToken works as in UpdateCharacterInventory; a stale owner writes nothing.
        // callerZoneInstanceId works as in UpdateCharacterStats; a stale zone writes nothing.
        // expectedRevision works as in UpdateCharacterInventory, and requireExpectedRevision refuses a
        // revision-less bag with "revision_required". Either revision refusal skips only the bag: quests
        // and stats still commit (together), and the response reports the refusal with Success = false.
        Task<UpdateCharacterDataResponse> UpdateCharacterData(Guid customerGUID, string characterName, IEnumerable<UpdateCharacterQuest> updateCharacterQuests,
            IEnumerable<UpdateCharacterInventory> updateCharacterInventory, IEnumerable<UpdateCharacterStats> updateCharacterStats, Guid? ownerToken,
            int? callerZoneInstanceId = null, long? expectedRevision = null, bool requireExpectedRevision = false);
        // Absolute wallet write. expectedRevision is opt-in, mirroring UpdateCharacterInventory: pass the
        // Characters.EconomyRevision the wallet was computed from to have a stale copy rejected instead of
        // undoing a shop transaction that committed in the meantime; pass null for legacy last-write-wins.
        // Response carries the post-write EconomyRevision (0 when unsupported) so callers can resync.
        // Postgres refuses the write with ReasonCode "stale_owner" when the character has an owner and ownerToken is
        // neither that owner nor the one a running handover replaced.
        Task<UpdateCharacterCurrencyResponse> UpdateCharacterCurrency(Guid customerGUID, string characterName, int gold, Guid? ownerToken, long? expectedRevision = null);
        Task<string> UpdateCharacterClass(Guid customerGUID, string characterName, string className, Guid? ownerToken);
        /// <summary>Upserts the abilities in <paramref name="characterAbilities"/> (a camelCase JSON array).</summary>
        Task<string> UpdateCharacterAbilities(Guid customerGUID, string characterName, string characterAbilities, Guid? ownerToken);
        // False when nothing was written: the character is unknown, or (Postgres) it has an owner and ownerToken is
        // neither that owner nor the one a running handover replaced. The ownership check is part of the write.
        Task<bool> UpdatePosition(Guid customerGUID, string characterName, string mapName, float X, float Y, float Z, float RX, float RY, float RZ, Guid? ownerToken = null);
        Task<bool> PlayerLogout(Guid customerGUID, string characterName, Guid? ownerToken = null);
        Task<MapInstances> SpinUpInstance(Guid customerGUID, string zoneName, int playerGroupId = 0);
        Task AddQuestListToDatabase(Guid customerGUID, IEnumerable<AddQuestListToDabase> addQuestListToDabase);
        Task<IEnumerable<GetQuestsFromDb>> GetQuestsFromDatabase(Guid customerGUID);

        Task<PartyToSend> CreatePartyOrAddMember(Guid customerGUID, PartyToSend partyRequest);
        Task<PartyToSend> GetInitialPartySettings(Guid customerGUID, string charName);

        Task<PartyToSend> LeaveParty(Guid customerGUID, PartyToSend partyRequest);

        Task<PartyToSend> ChangePartyLeader(Guid customerGUID, PartyToSend partyRequest);
        Task<bool> IsPartyNameAvailable(Guid customerGUID, string partyName);
        Task<string> GenerateAvailablePartyName(Guid customerGUID);
        Task<PartyToSend> UpdatePartyName(Guid customerGUID, Guid partyGuid, string actorCharName, string actorCharGuid, string partyName);
        Task<PartyToSend> UpdatePartyDescription(Guid customerGUID, Guid partyGuid, string actorCharName, string actorCharGuid, string partyDescription);
        Task<PartyToSend> UpdatePartyExpDistribution(Guid customerGUID, Guid partyGuid, string actorCharName, string actorCharGuid, int expDistributionMode);
        Task<PartyToSend> UpdatePartyLootDistribution(Guid customerGUID, Guid partyGuid, string actorCharName, string actorCharGuid, int lootDistributionMode);

        Task<GuildToSend> CreateGuildOrAddMember(Guid customerGUID, GuildToSend guildRequest);

        Task<GuildToSend> GetInitialGuildSettings(Guid customerGUID, string charName);

        Task<GuildToSend> AddGuildAbilities(Guid customerGUID, GuildToSend guildInfo);
        Task<IEnumerable<CharacterAbilityDto>> GetCharacterAbilities(Guid customerGUID, string characterName);

        // Management-console character administration. These are cross-user lookups and
        // writes with no session check of their own, so they must only be reached from
        // OWSManagement, never from the public API.
        Task<IEnumerable<AdminCharacterSummary>> GetCharactersForUser(Guid customerGUID, Guid userGUID);
        Task<IEnumerable<AdminCharacterSummary>> SearchCharacters(Guid customerGUID, string searchText);
        // Null leaves a flag unchanged, so a caller can toggle one without knowing the others.
        Task<SuccessAndErrorMessage> SetCharacterFlags(Guid customerGUID, int characterID, bool? isAdmin, bool? isModerator, bool? isInternalNetworkTestUser);
    }
}
