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
        Task AddOrUpdateCustomCharacterData(Guid customerGUID, AddOrUpdateCustomCharacterData addOrUpdateCustomCharacterData);
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
        Task<bool> UpdateCharacterStats(Guid customerGUID, string characterName, IEnumerable<UpdateCharacterStats> updateCharacterStats, int? callerZoneInstanceId = null);
        Task<bool> UpdateCharacterQuests(Guid customerGUID, string characterName, IEnumerable<UpdateCharacterQuest> updateCharacterQuests, int? callerZoneInstanceId = null);
        // Whole-bag rewrite. expectedRevision is opt-in: pass the Characters.EconomyRevision the bag
        // was computed from to have a stale snapshot rejected instead of clobbering a shop
        // transaction that committed in the meantime; pass null for legacy last-write-wins.
        Task<UpdateCharacterInventoryResponse> UpdateCharacterInventory(Guid customerGUID, string characterName, IEnumerable<UpdateCharacterInventory> updateCharacterInventory, long? expectedRevision = null);
        // Absolute wallet write. expectedRevision is opt-in, mirroring UpdateCharacterInventory: pass the
        // Characters.EconomyRevision the wallet was computed from to have a stale copy rejected instead of
        // undoing a shop transaction that committed in the meantime; pass null for legacy last-write-wins.
        // Response carries the post-write EconomyRevision (0 when unsupported) so callers can resync.
        Task<UpdateCharacterCurrencyResponse> UpdateCharacterCurrency(Guid customerGUID, string characterName, int gold, long? expectedRevision = null);
        Task UpdateCharacterClass(Guid customerGUID, string characterName, string className);
        Task UpdateCharacterAbilities(Guid customerGUID, string characterName, string characterAbilities);
        Task UpdatePosition(Guid customerGUID, string characterName, string mapName, float X, float Y, float Z, float RX, float RY, float RZ);
        Task PlayerLogout(Guid customerGUID, string characterName);
        Task<MapInstances> SpinUpInstance(Guid customerGUID, string zoneName, int playerGroupId = 0);
        Task AddQuestListToDatabase(Guid customerGUID, IEnumerable<AddQuestListToDabase> addQuestListToDabase);
        Task<IEnumerable<GetQuestsFromDb>> GetQuestsFromDatabase(Guid customerGUID);

        Task<PartyToSend> CreatePartyOrAddMember(Guid customerGUID, PartyToSend partyRequest);
        Task<PartyToSend> GetInitialPartySettings(Guid customerGUID, string charName);

        Task<PartyToSend> LeaveParty(Guid customerGUID, PartyToSend partyRequest);

        Task<PartyToSend> ChangePartyLeader(Guid customerGUID, PartyToSend partyRequest);
        Task<bool> IsPartyNameAvailable(Guid customerGUID, string partyName);
        Task<string> GenerateAvailablePartyName(Guid customerGUID);
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
