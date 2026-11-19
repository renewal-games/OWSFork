using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSCharacterPersistence.Requests.Characters;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Implementations;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// Stats, quests and abilities saves upsert only the keys they carry, under the character row lock. The lock orders
    /// the writes but not the snapshots: a save built first (A) that reaches the lock after a newer one (B) has committed
    /// used to put back A's older values, with the same valid owner token and zone. These saves are A arriving late,
    /// whether it was slow to reach the database, a timed-out request that still went through, or a retry. Each key now
    /// keeps the version of the save that wrote it (owner token, the caller's SaveSequence if any, and when the request
    /// was received), so A loses on the keys B wrote and still writes the keys B did not carry. Issue #247. Migrated
    /// schema only: setup.sql alone has none of these tables.
    /// </summary>
    public class CharacterSaveOrderingTests : IClassFixture<PostgresTestDatabases>
    {
        private const string CharName = "OrderTester";
        private const int OwningZone = 201;
        private const SchemaSource Source = SchemaSource.Migrated;

        private readonly PostgresTestDatabases _databases;

        public CharacterSaveOrderingTests(PostgresTestDatabases databases) => _databases = databases;

        [PostgresFact]
        public async Task OlderStatsSave_ArrivingAfterANewerOne_KeepsTheNewerValue()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            await AssertSaved(Stats(customerGUID, token, 2, ("Strength", 11)));
            await AssertSaved(Stats(customerGUID, token, 1, ("Strength", 10)));

            Assert.Equal(11, await Stat(customerGUID, "Strength"));
        }

        [PostgresFact]
        public async Task OlderStatsSave_StillWritesTheStatsTheNewerOneDidNotCarry()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            await AssertSaved(Stats(customerGUID, token, 2, ("Strength", 11)));
            await AssertSaved(Stats(customerGUID, token, 1, ("Strength", 10), ("Agility", 3)));

            Assert.Equal(11, await Stat(customerGUID, "Strength"));
            Assert.Equal(3, await Stat(customerGUID, "Agility"));
        }

        [PostgresFact]
        public async Task Retries_KeepTheirNumberAndChangeNothingOnceANewerSaveLanded()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            await AssertSaved(Stats(customerGUID, token, 1, ("Strength", 10)));
            await AssertSaved(Stats(customerGUID, token, 2, ("Strength", 11)));
            // A timed-out first attempt of each that went through after all, then the client's retries.
            await AssertSaved(Stats(customerGUID, token, 1, ("Strength", 10)));
            await AssertSaved(Stats(customerGUID, token, 2, ("Strength", 11)));
            await AssertSaved(Stats(customerGUID, token, 1, ("Strength", 10)));

            Assert.Equal(11, await Stat(customerGUID, "Strength"));
        }

        // Today's zone server sends no SaveSequence. Its saves are ordered by when this service received them: A was
        // received (its request built) first, B second; B reaches the lock and commits first, then A arrives.
        [PostgresFact]
        public async Task TodaysClient_StatsSaveReceivedFirstButWrittenLast_KeepsTheNewerValue()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            UpdateCharacterStatsRequest a = Stats(customerGUID, token, null, ("Strength", 10), ("Agility", 3));
            UpdateCharacterStatsRequest b = Stats(customerGUID, token, null, ("Strength", 11));
            await AssertSaved(b);
            await AssertSaved(a);

            Assert.Equal(11, await Stat(customerGUID, "Strength"));
            Assert.Equal(3, await Stat(customerGUID, "Agility"));
        }

        [PostgresFact]
        public async Task TodaysClient_QuestAndAbilitySavesReceivedFirstButWrittenLast_KeepTheNewerValues()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            UpdateCharacterQuestsRequest questsA = Quests(customerGUID, token, null, ("Quest.Main", "step1"), ("Quest.Side", "side1"));
            UpdateCharacterAbilitiesRequest abilitiesA = Abilities(customerGUID, token, null, ("Ability.Fireball", 4), ("Ability.Frost", 1));
            UpdateCharacterQuestsRequest questsB = Quests(customerGUID, token, null, ("Quest.Main", "step2"));
            UpdateCharacterAbilitiesRequest abilitiesB = Abilities(customerGUID, token, null, ("Ability.Fireball", 5));
            await AssertSaved(questsB);
            await AssertSaved(abilitiesB);
            await AssertSaved(questsA);
            await AssertSaved(abilitiesA);

            Assert.Equal("step2", await Journal(customerGUID, "Quest.Main"));
            Assert.Equal("side1", await Journal(customerGUID, "Quest.Side"));
            Assert.Equal(5, await AbilityLevel(customerGUID, "Ability.Fireball"));
            Assert.Equal(1, await AbilityLevel(customerGUID, "Ability.Frost"));
        }

        [PostgresFact]
        public async Task TodaysClient_SavesInReceiptOrder_AllLand()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            await AssertSaved(Stats(customerGUID, token, null, ("Strength", 11)));
            await AssertSaved(Stats(customerGUID, token, null, ("Strength", 10)));
            await AssertSaved(Stats(customerGUID, token, 0, ("Strength", 12)));

            Assert.Equal(12, await Stat(customerGUID, "Strength"));
        }

        // A token that has sent a SaveSequence cannot slip an unnumbered save in among its numbered ones.
        [PostgresFact]
        public async Task AfterATokenSendsASequence_ItsSavesWithoutOneAreRefusedAndWriteNothing()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();
            await AssertSaved(Stats(customerGUID, token, 1, ("Strength", 10)));

            await AssertRequired(await Stats(customerGUID, token, null, ("Strength", 1), ("Agility", 1)).Handle());
            await AssertRequired(await Quests(customerGUID, token, null, ("Quest.Main", "unordered")).Handle());
            await AssertRequired(await Abilities(customerGUID, token, null, ("Ability.Fireball", 1)).Handle());
            UpdateCharacterDataResponse combined = await CharacterData(customerGUID, token, null, ("Strength", 1), ("Quest.Main", "unordered")).Handle();
            Assert.False(combined.Success);
            Assert.Equal("save_sequence_required", combined.ReasonCode);

            Assert.Equal(10, await Stat(customerGUID, "Strength"));
            Assert.Null(await Stat(customerGUID, "Agility"));
            Assert.Null(await Journal(customerGUID, "Quest.Main"));
            Assert.Null(await AbilityLevel(customerGUID, "Ability.Fireball"));

            // The rule is per token: the next owner may still save without numbers.
            Guid newToken = (await Owners().ClaimByName(customerGUID, CharName, OwningZone)).OwnerToken;
            await AssertSaved(Stats(customerGUID, newToken, null, ("Strength", 12)));
            Assert.Equal(12, await Stat(customerGUID, "Strength"));

            static Task AssertRequired(SuccessAndErrorMessage reply)
            {
                Assert.False(reply.Success);
                Assert.Equal("save_sequence_required", reply.ErrorMessage);
                return Task.CompletedTask;
            }
        }

        [PostgresFact]
        public async Task TodaysClient_ReplacedOwnersLateSaveCannotUndoTheNewOwnersWrite()
        {
            (Guid customerGUID, Guid oldToken) = await SeedOwnedCharacter();
            await AssertSaved(Stats(customerGUID, oldToken, null, ("Strength", 5)));
            UpdateCharacterStatsRequest late = Stats(customerGUID, oldToken, null, ("Strength", 4), ("Agility", 9));

            Guid newToken = (await Owners().ClaimByName(customerGUID, CharName, OwningZone)).OwnerToken;
            await AssertSaved(Stats(customerGUID, newToken, null, ("Strength", 6)));
            await AssertSaved(late);

            Assert.Equal(6, await Stat(customerGUID, "Strength"));
            Assert.Equal(9, await Stat(customerGUID, "Agility"));
        }

        // Without an owner token (possible only while the character has no owner, as for legacy builds) nothing is
        // versioned: the last write wins, as before.
        [PostgresFact]
        public async Task OwnerlessSaves_StayLastWriteWins()
        {
            Guid customerGUID = await SeedCharacter();

            UpdateCharacterStatsRequest a = Stats(customerGUID, null, null, ("Strength", 10));
            await AssertSaved(Stats(customerGUID, null, null, ("Strength", 11)));
            await AssertSaved(a);

            Assert.Equal(10, await Stat(customerGUID, "Strength"));
        }

        [PostgresFact]
        public async Task OlderQuestSave_KeepsTheNewerJournal_AndWritesQuestsTheNewerOneDidNotCarry()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            await AssertSaved(Quests(customerGUID, token, 2, ("Quest.Main", "step2")));
            await AssertSaved(Quests(customerGUID, token, 1, ("Quest.Main", "step1"), ("Quest.Side", "side1")));

            Assert.Equal("step2", await Journal(customerGUID, "Quest.Main"));
            Assert.Equal("side1", await Journal(customerGUID, "Quest.Side"));
        }

        [PostgresFact]
        public async Task OlderAbilitySave_KeepsTheNewerLevel_AndWritesAbilitiesTheNewerOneDidNotCarry()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            await AssertSaved(Abilities(customerGUID, token, 2, ("Ability.Fireball", 5)));
            await AssertSaved(Abilities(customerGUID, token, 1, ("Ability.Fireball", 4), ("Ability.Frost", 1)));

            Assert.Equal(5, await AbilityLevel(customerGUID, "Ability.Fireball"));
            Assert.Equal(1, await AbilityLevel(customerGUID, "Ability.Frost"));
        }

        [PostgresFact]
        public async Task CombinedAndStandaloneSaves_ShareOneOrder()
        {
            (Guid customerGUID, Guid token) = await SeedOwnedCharacter();

            // The combined save is newest; standalone saves taken before it arrive afterwards.
            UpdateCharacterDataResponse combined = await CharacterData(customerGUID, token, 3, ("Strength", 11), ("Quest.Main", "step3")).Handle();
            Assert.True(combined.Success, combined.ErrorMessage);
            await AssertSaved(Stats(customerGUID, token, 2, ("Strength", 10)));
            await AssertSaved(Quests(customerGUID, token, 2, ("Quest.Main", "step2")));

            Assert.Equal(11, await Stat(customerGUID, "Strength"));
            Assert.Equal("step3", await Journal(customerGUID, "Quest.Main"));

            // A standalone save is newest; a combined one taken before it arrives afterwards.
            await AssertSaved(Stats(customerGUID, token, 5, ("Strength", 13)));
            combined = await CharacterData(customerGUID, token, 4, ("Strength", 12), ("Quest.Main", "step4")).Handle();
            Assert.True(combined.Success, combined.ErrorMessage);

            Assert.Equal(13, await Stat(customerGUID, "Strength"));
            Assert.Equal("step4", await Journal(customerGUID, "Quest.Main"));
        }

        [PostgresFact]
        public async Task Handover_NewOwnerStartsItsOwnCount_AndTheReplacedOwnersLateSaveCannotUndoIt()
        {
            (Guid customerGUID, Guid oldToken) = await SeedOwnedCharacter();
            await AssertSaved(Stats(customerGUID, oldToken, 100, ("Strength", 5)));

            ClaimCharacterResult claim = await Owners().ClaimByName(customerGUID, CharName, OwningZone);
            Assert.True(claim.Success);
            Assert.True(claim.HandoverSeconds > 0);
            Guid newToken = claim.OwnerToken;

            // The new owner (a new zone server, or the same one restarted) counts from 1 again.
            await AssertSaved(Stats(customerGUID, newToken, 1, ("Strength", 6)));
            Assert.Equal(6, await Stat(customerGUID, "Strength"));

            // The replaced owner's last save still lands during the handover, but only on keys the new owner has not written.
            await AssertSaved(Stats(customerGUID, oldToken, 101, ("Strength", 4), ("Agility", 9)));
            Assert.Equal(6, await Stat(customerGUID, "Strength"));
            Assert.Equal(9, await Stat(customerGUID, "Agility"));

            // The new owner overwrites what the replaced owner wrote, whatever the numbers.
            await AssertSaved(Stats(customerGUID, newToken, 2, ("Agility", 7)));
            Assert.Equal(7, await Stat(customerGUID, "Agility"));
        }

        [PostgresFact]
        public async Task StaleOwnerAndStaleZone_AreStillRefusedWhateverTheSequence()
        {
            (Guid customerGUID, Guid oldToken) = await SeedOwnedCharacter();
            await AssertSaved(Stats(customerGUID, oldToken, 1, ("Strength", 5)));

            Guid newToken = (await Owners().ClaimByName(customerGUID, CharName, OwningZone)).OwnerToken;
            await EndHandover(customerGUID);
            Assert.IsType<StaleOwnerResult>(await Stats(customerGUID, oldToken, 1000, ("Strength", 1)).Handle());

            UpdateCharacterStatsRequest otherZone = Stats(customerGUID, newToken, 1, ("Strength", 2));
            otherZone.ZoneInstanceID = OwningZone + 1;
            Assert.Equal("stale_zone", (await otherZone.Handle()).ErrorMessage);

            Assert.Equal(5, await Stat(customerGUID, "Strength"));
        }

        private static async Task AssertSaved(UpdateCharacterStatsRequest request) => AssertWritten(await request.Handle());
        private static async Task AssertSaved(UpdateCharacterQuestsRequest request) => AssertWritten(await request.Handle());
        private static async Task AssertSaved(UpdateCharacterAbilitiesRequest request) => AssertWritten(await request.Handle());

        // A save that loses to a newer one on every key still succeeds: there is nothing for the caller to retry.
        private static void AssertWritten(SuccessAndErrorMessage result)
        {
            Assert.IsNotType<StaleOwnerResult>(result);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("", result.ErrorMessage ?? "");
        }

        // Each request is stamped as received when it is built, as model binding builds it.
        private UpdateCharacterStatsRequest Stats(Guid customerGUID, Guid? token, long? sequence, params (string Stat, int Value)[] stats)
        {
            var request = new UpdateCharacterStatsRequest
            {
                CharacterName = CharName,
                OwnerToken = token,
                ZoneInstanceID = OwningZone,
                SaveSequence = sequence,
                CharacterStats = stats.Select(s => new UpdateCharacterStats { StatIdentifier = s.Stat, Value = s.Value }).ToList()
            };
            request.SetData(Characters(), new HeaderCustomerGUID { CustomerGUID = customerGUID });
            return request;
        }

        private UpdateCharacterQuestsRequest Quests(Guid customerGUID, Guid token, long? sequence, params (string Quest, string Journal)[] quests)
        {
            var request = new UpdateCharacterQuestsRequest
            {
                CharacterName = CharName,
                OwnerToken = token,
                ZoneInstanceID = OwningZone,
                SaveSequence = sequence,
                CharacterQuests = quests.Select(q => new UpdateCharacterQuest { QuestIDTag = q.Quest, QuestJournalTagContainer = q.Journal, CustomData = "" }).ToList()
            };
            request.SetData(Characters(), new HeaderCustomerGUID { CustomerGUID = customerGUID });
            return request;
        }

        private UpdateCharacterAbilitiesRequest Abilities(Guid customerGUID, Guid token, long? sequence, params (string Ability, int Level)[] abilities)
        {
            var request = new UpdateCharacterAbilitiesRequest
            {
                CharacterName = CharName,
                OwnerToken = token,
                SaveSequence = sequence,
                CharacterAbilities = abilities.Select(a => new CharacterAbilityDto { AbilityIdTag = a.Ability, CurrentAbilityLevel = a.Level, ActualAbilityLevel = a.Level }).ToList()
            };
            request.SetData(Characters(), new HeaderCustomerGUID { CustomerGUID = customerGUID });
            return request;
        }

        private UpdateCharacterDataRequest CharacterData(Guid customerGUID, Guid token, long? sequence, (string Stat, int Value) stat, (string Quest, string Journal) quest)
        {
            var request = new UpdateCharacterDataRequest
            {
                CharacterName = CharName,
                OwnerToken = token,
                ZoneInstanceID = OwningZone,
                SaveSequence = sequence,
                CharQuests = new List<UpdateCharacterQuest> { new() { QuestIDTag = quest.Quest, QuestJournalTagContainer = quest.Journal, CustomData = "" } },
                CharInventory = new List<UpdateCharacterInventory>(),
                CharStats = new List<UpdateCharacterStats> { new() { StatIdentifier = stat.Stat, Value = stat.Value } }
            };
            request.SetData(Characters(), new HeaderCustomerGUID { CustomerGUID = customerGUID }, new EconomyOptions());
            return request;
        }

        private CharactersRepository Characters() =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(Source) }));

        private CharacterOwnersRepository Owners() =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(Source) }));

        // A character in OwningZone with an empty bag, claimed by a zone server; returns the owner token.
        private async Task<(Guid CustomerGUID, Guid Token)> SeedOwnedCharacter()
        {
            Guid customerGUID = await SeedCharacter();
            ClaimCharacterResult claim = await Owners().ClaimByName(customerGUID, CharName, OwningZone);
            Assert.True(claim.Success, claim.ErrorMessage);
            return (customerGUID, claim.OwnerToken);
        }

        // A character in OwningZone with an empty bag and no owner.
        private async Task<Guid> SeedCharacter()
        {
            Guid customerGUID = Guid.NewGuid();
            await using (var connection = new NpgsqlConnection(_databases.ConnectionString(Source)))
            {
                await connection.ExecuteAsync(
                    @"WITH C AS (
                          INSERT INTO Characters (CustomerGUID, CharName, X, Y, Z, ClassID)
                          VALUES (@CustomerGUID, @CharName, 0, 0, 0, 0)
                          RETURNING CharacterID),
                      I AS (
                          INSERT INTO CharInventory (CustomerGUID, CharacterID, InventoryName, InventorySize)
                          SELECT @CustomerGUID, CharacterID, 'Bag', 16 FROM C)
                      INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID)
                      SELECT @CustomerGUID, CharacterID, @OwningZone FROM C",
                    new { CustomerGUID = customerGUID, CharName, OwningZone });
            }

            return customerGUID;
        }

        private async Task EndHandover(Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(Source));
            await connection.ExecuteAsync(@"UPDATE CharacterOwners SET PreviousOwnerUntil = NOW() - INTERVAL '1 second'
                WHERE CustomerGUID = @CustomerGUID", new { CustomerGUID = customerGUID });
        }

        private async Task<int?> Stat(Guid customerGUID, string stat)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(Source));
            return await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT Value FROM CharStats WHERE CustomerGUID = @CustomerGUID AND StatIdentifier = @Stat",
                new { CustomerGUID = customerGUID, Stat = stat });
        }

        private async Task<string> Journal(Guid customerGUID, string quest)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(Source));
            return await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT QuestJournalTagContainer FROM CharQuests WHERE CustomerGUID = @CustomerGUID AND QuestIDTag = @Quest",
                new { CustomerGUID = customerGUID, Quest = quest });
        }

        private async Task<int?> AbilityLevel(Guid customerGUID, string ability)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(Source));
            return await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT CurrentAbilityLevel FROM CharAbilities WHERE CustomerGUID = @CustomerGUID AND AbilityIDTag = @Ability",
                new { CustomerGUID = customerGUID, Ability = ability });
        }
    }
}
