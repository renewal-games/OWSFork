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
    /// Bag saves replace the whole bag and are handled in the order they reach the Characters row lock, not the order
    /// the zone server sent them. A save that carries a SaveSeq is written only when that SaveSeq is above
    /// Characters.InventorySaveSeq, so an older save arriving after a newer one is answered "superseded" and leaves
    /// the newer bag in place. A save without a SaveSeq is last-write-wins as before. Runs on the migrated schema
    /// only: InventorySaveSeq, CharQuests, CharStats and CharacterOwners are not in setup.sql.
    /// </summary>
    public class InventorySaveSeqTests : IClassFixture<PostgresTestDatabases>
    {
        private const string CharName = "SeqTester";

        private readonly PostgresTestDatabases _databases;

        public InventorySaveSeqTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task OlderSaveAfterNewerOne_IsSupersededAndBagKept(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);

            UpdateCharacterInventoryResponse newer = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("FinalBag"), null, saveSeq: 2);
            UpdateCharacterInventoryResponse older = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("OlderBag"), null, saveSeq: 1);

            Assert.True(newer.Success, newer.ReasonCode);
            Assert.Equal("", newer.ReasonCode);
            Assert.Equal(2, newer.InventorySaveSeq);

            // Not an error: the zone server treats a non-empty ErrorMessage or Success = false as a failed save.
            Assert.True(older.Success);
            Assert.Equal("superseded", older.ReasonCode);
            Assert.Equal("", older.ErrorMessage);
            Assert.Equal(2, older.InventorySaveSeq);
            Assert.Equal(new[] { "FinalBag" }, await Items(source, customerGUID));
            Assert.Equal(2, await StoredSaveSeq(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task OlderSaveWaitingOnTheRowLockWhileANewerOneCommits_IsSuperseded(SchemaSource source)
        {
            // The older save reaches the lock while the newer one's transaction holds it. The SaveSeq it is compared
            // with has to be read after the lock is granted, or it would still see the value from before the newer save.
            Guid customerGUID = await SeedCharacter(source);

            await using var newerConnection = new NpgsqlConnection(_databases.ConnectionString(source));
            await newerConnection.OpenAsync();
            await using NpgsqlTransaction newerTx = await newerConnection.BeginTransactionAsync();
            await newerConnection.ExecuteAsync(@"SELECT CharacterID FROM Characters
                WHERE CustomerGUID = @CustomerGUID AND CharName = @CharName FOR UPDATE", new { CustomerGUID = customerGUID, CharName }, newerTx);
            await newerConnection.ExecuteAsync(@"UPDATE CharInventoryItems SET ItemIDTag = 'FinalBag' WHERE CustomerGUID = @CustomerGUID",
                new { CustomerGUID = customerGUID }, newerTx);
            await newerConnection.ExecuteAsync(@"UPDATE Characters SET InventorySaveSeq = 2
                WHERE CustomerGUID = @CustomerGUID AND CharName = @CharName", new { CustomerGUID = customerGUID, CharName }, newerTx);

            Task<UpdateCharacterInventoryResponse> older = Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("OlderBag"), null, saveSeq: 1);
            await WaitUntilBlockedOnLock(source, older);

            await newerTx.CommitAsync();
            UpdateCharacterInventoryResponse result = await older;

            Assert.True(result.Success);
            Assert.Equal("superseded", result.ReasonCode);
            Assert.Equal(new[] { "FinalBag" }, await Items(source, customerGUID));
            Assert.Equal(2, await StoredSaveSeq(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task SameSaveSeqSentAgain_IsSuperseded(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            Assert.True((await Characters(source).UpdateCharacterInventory(customerGUID, CharName, Bag("FinalBag"), null, saveSeq: 5)).Success);

            UpdateCharacterInventoryResponse again = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("ResentBag"), null, saveSeq: 5);

            Assert.True(again.Success);
            Assert.Equal("superseded", again.ReasonCode);
            Assert.Equal(new[] { "FinalBag" }, await Items(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task NewerSave_ReplacesBagAndStoresItsSaveSeq(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            Assert.True((await Characters(source).UpdateCharacterInventory(customerGUID, CharName, Bag("FirstBag"), null, saveSeq: 5)).Success);

            UpdateCharacterInventoryResponse newer = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("SecondBag"), null, saveSeq: 6);

            Assert.True(newer.Success, newer.ReasonCode);
            Assert.Equal("", newer.ReasonCode);
            Assert.Equal(6, newer.InventorySaveSeq);
            Assert.Equal(new[] { "SecondBag" }, await Items(source, customerGUID));
            Assert.Equal(6, await StoredSaveSeq(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task SaveWithoutSaveSeq_StillOverwritesAndLeavesTheStoredOne(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            Assert.True((await Characters(source).UpdateCharacterInventory(customerGUID, CharName, Bag("SeqBag"), null, saveSeq: 5)).Success);

            UpdateCharacterInventoryResponse legacy = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("LegacyBag"), null);

            Assert.True(legacy.Success, legacy.ReasonCode);
            Assert.Equal("", legacy.ReasonCode);
            Assert.Equal(0, legacy.InventorySaveSeq);
            Assert.Equal(new[] { "LegacyBag" }, await Items(source, customerGUID));
            Assert.Equal(5, await StoredSaveSeq(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task SaveRefusedForStaleRevision_DoesNotStoreItsSaveSeq(SchemaSource source)
        {
            // The refused save wrote nothing, so its SaveSeq must stay usable: the caller resends it once it has the revision.
            Guid customerGUID = await SeedCharacter(source);
            long revision = await Revision(source, customerGUID);

            UpdateCharacterInventoryResponse refused = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("NewBag"), null, expectedRevision: revision + 1, saveSeq: 7);

            Assert.False(refused.Success);
            Assert.Equal("stale_revision", refused.ReasonCode);
            Assert.Equal(new[] { "OldShield" }, await Items(source, customerGUID));
            Assert.Equal(0, await StoredSaveSeq(source, customerGUID));

            UpdateCharacterInventoryResponse resent = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("NewBag"), null, expectedRevision: revision, saveSeq: 7);

            Assert.True(resent.Success, resent.ReasonCode);
            Assert.Equal("", resent.ReasonCode);
            Assert.Equal(new[] { "NewBag" }, await Items(source, customerGUID));
            Assert.Equal(7, await StoredSaveSeq(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task SupersededSave_DoesNotBumpTheEconomyRevision(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            long revision = await Revision(source, customerGUID);
            Assert.True((await Characters(source).UpdateCharacterInventory(customerGUID, CharName, Bag("FinalBag"), null, saveSeq: 2)).Success);

            UpdateCharacterInventoryResponse older = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("OlderBag"), null, expectedRevision: revision, saveSeq: 1);

            Assert.Equal("superseded", older.ReasonCode);
            Assert.Equal(revision, older.NewEconomyRevision);
            Assert.Equal(revision, await Revision(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task InventoryRequest_PassesSaveSeqAndTreatsZeroOrNegativeAsNone(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);

            UpdateCharacterInventoryResponse newer = await InventoryRequest(source, customerGUID, Bag("FinalBag"), saveSeq: 2).Handle();
            UpdateCharacterInventoryResponse older = await InventoryRequest(source, customerGUID, Bag("OlderBag"), saveSeq: 1).Handle();

            Assert.True(newer.Success, newer.ReasonCode);
            Assert.True(older.Success);
            Assert.Equal("superseded", older.ReasonCode);
            Assert.Equal(new[] { "FinalBag" }, await Items(source, customerGUID));

            // 0 is what an unset number serializes to, and the column's default: neither it nor a negative is an order.
            UpdateCharacterInventoryResponse zero = await InventoryRequest(source, customerGUID, Bag("ZeroBag"), saveSeq: 0).Handle();
            Assert.Equal("", zero.ReasonCode);
            Assert.Equal(new[] { "ZeroBag" }, await Items(source, customerGUID));

            UpdateCharacterInventoryResponse negative = await InventoryRequest(source, customerGUID, Bag("NegativeBag"), saveSeq: -1).Handle();
            Assert.Equal("", negative.ReasonCode);
            Assert.Equal(new[] { "NegativeBag" }, await Items(source, customerGUID));
            Assert.Equal(2, await StoredSaveSeq(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task CharacterData_OlderSaveSkipsOnlyTheBag(SchemaSource source)
        {
            // InventorySaveSeq orders bags. The older save's quests and stats still commit, as with a bag the
            // economy revision guard refuses, and the reply is a success so the caller does not retry it.
            Guid customerGUID = await SeedCharacter(source);

            UpdateCharacterDataResponse newer = await DataRequest(source, customerGUID, "Quest.Newer", Bag("FinalBag"), level: 7, saveSeq: 2).Handle();
            UpdateCharacterDataResponse older = await DataRequest(source, customerGUID, "Quest.Older", Bag("OlderBag"), level: 5, saveSeq: 1).Handle();

            Assert.True(newer.Success, newer.ErrorMessage);
            Assert.Equal("", newer.ReasonCode);
            Assert.Equal(2, newer.InventorySaveSeq);

            Assert.True(older.Success);
            Assert.Equal("superseded", older.ReasonCode);
            Assert.Equal("", older.ErrorMessage ?? "");
            Assert.Equal(2, older.InventorySaveSeq);
            Assert.Equal(new[] { "FinalBag" }, await Items(source, customerGUID));
            Assert.Equal(new[] { "Quest.Newer", "Quest.Older" }, await Quests(source, customerGUID));
            Assert.Equal(5, await Stat(source, customerGUID));
            Assert.Equal(2, await StoredSaveSeq(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task CharacterData_SharesTheOrderWithTheStandaloneBagSave(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            Assert.True((await Characters(source).UpdateCharacterInventory(customerGUID, CharName, Bag("FinalBag"), null, saveSeq: 3)).Success);

            UpdateCharacterDataResponse olderAutosave = await DataRequest(source, customerGUID, "Quest.Older", Bag("OlderBag"), level: 5, saveSeq: 2).Handle();
            Assert.Equal("superseded", olderAutosave.ReasonCode);
            Assert.Equal(new[] { "FinalBag" }, await Items(source, customerGUID));

            UpdateCharacterDataResponse newerAutosave = await DataRequest(source, customerGUID, "Quest.Older", Bag("NewerBag"), level: 5, saveSeq: 4).Handle();
            Assert.True(newerAutosave.Success, newerAutosave.ErrorMessage);
            Assert.Equal("", newerAutosave.ReasonCode);
            Assert.Equal(new[] { "NewerBag" }, await Items(source, customerGUID));
            Assert.Equal(4, await StoredSaveSeq(source, customerGUID));

            UpdateCharacterInventoryResponse olderBagSave = await Characters(source).UpdateCharacterInventory(customerGUID, CharName,
                Bag("OlderBag"), null, saveSeq: 3);
            Assert.Equal("superseded", olderBagSave.ReasonCode);
            Assert.Equal(new[] { "NewerBag" }, await Items(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task CharacterData_BagRefusedForStaleRevision_DoesNotStoreItsSaveSeq(SchemaSource source)
        {
            // Quests and stats commit here, so the SaveSeq must not commit with them: the bag was not written.
            Guid customerGUID = await SeedCharacter(source);
            long revision = await Revision(source, customerGUID);

            UpdateCharacterDataResponse refused = await DataRequest(source, customerGUID, "Quest.First", Bag("NewBag"), level: 7,
                saveSeq: 9, expectedRevision: revision + 1).Handle();

            Assert.False(refused.Success);
            Assert.Equal("stale_revision", refused.ReasonCode);
            Assert.Equal(0, refused.InventorySaveSeq);
            Assert.Equal(7, await Stat(source, customerGUID));
            Assert.Equal(new[] { "OldShield" }, await Items(source, customerGUID));
            Assert.Equal(0, await StoredSaveSeq(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task CharacterData_WithoutSaveSeq_StillOverwrites(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            Assert.True((await DataRequest(source, customerGUID, "Quest.First", Bag("SeqBag"), level: 7, saveSeq: 5).Handle()).Success);

            UpdateCharacterDataResponse legacy = await DataRequest(source, customerGUID, "Quest.First", Bag("LegacyBag"), level: 7, saveSeq: null).Handle();

            Assert.True(legacy.Success, legacy.ErrorMessage);
            Assert.Equal("", legacy.ReasonCode);
            Assert.Equal(new[] { "LegacyBag" }, await Items(source, customerGUID));
            Assert.Equal(5, await StoredSaveSeq(source, customerGUID));
        }

        private StorageOptions Storage(SchemaSource source) =>
            new() { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) };

        private CharactersRepository Characters(SchemaSource source) => new(Microsoft.Extensions.Options.Options.Create(Storage(source)));

        private UpdateCharacterInventoryRequest InventoryRequest(SchemaSource source, Guid customerGUID,
            IEnumerable<UpdateCharacterInventory> inventory, long? saveSeq)
        {
            var request = new UpdateCharacterInventoryRequest
            {
                CharacterName = CharName,
                Inventory = inventory,
                SaveSeq = saveSeq
            };
            request.SetData(Characters(source), new HeaderCustomerGUID { CustomerGUID = customerGUID }, new EconomyOptions());
            return request;
        }

        private UpdateCharacterDataRequest DataRequest(SchemaSource source, Guid customerGUID, string questIDTag,
            IEnumerable<UpdateCharacterInventory> inventory, int level, long? saveSeq, long? expectedRevision = null)
        {
            var request = new UpdateCharacterDataRequest
            {
                CharacterName = CharName,
                CharQuests = new List<UpdateCharacterQuest>
                {
                    new() { QuestIDTag = questIDTag, QuestJournalTagContainer = "(Journal=\"new\")", CustomData = "" }
                },
                CharInventory = inventory,
                CharStats = new List<UpdateCharacterStats> { new() { StatIdentifier = "BaseLevel", Value = level } },
                ExpectedRevision = expectedRevision,
                SaveSeq = saveSeq
            };
            request.SetData(Characters(source), new HeaderCustomerGUID { CustomerGUID = customerGUID }, new EconomyOptions());
            return request;
        }

        private static List<UpdateCharacterInventory> Bag(string item) =>
            new() { new UpdateCharacterInventory { ItemIDTag = item, Quantity = 1, InSlotNumber = 0, CustomData = "" } };

        // The save must reach the row lock before the other transaction commits, or the test proves nothing about the
        // race. Each test class has its own database and its tests run one at a time, so any lock wait here is the save's.
        private async Task WaitUntilBlockedOnLock(SchemaSource source, Task save)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            for (int attempt = 0; attempt < 200; attempt++)
            {
                Assert.False(save.IsCompleted, "The save finished while the other transaction still held the character row lock.");
                if (await connection.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock')"))
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("The save never blocked on the character row lock.");
        }

        // A character with one bag item, BaseLevel 1, no quests and no owner.
        private async Task<Guid> SeedCharacter(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(
                @"WITH C AS (
                      INSERT INTO Characters (CustomerGUID, CharName, X, Y, Z, ClassID)
                      VALUES (@CustomerGUID, @CharName, 0, 0, 0, 0)
                      RETURNING CharacterID),
                  I AS (
                      INSERT INTO CharInventory (CustomerGUID, CharacterID, InventoryName, InventorySize)
                      SELECT @CustomerGUID, CharacterID, 'Bag', 16 FROM C
                      RETURNING CharInventoryID),
                  S AS (
                      INSERT INTO CharStats (CustomerGUID, CharacterID, StatIdentifier, Value)
                      SELECT @CustomerGUID, CharacterID, 'BaseLevel', 1 FROM C)
                  INSERT INTO CharInventoryItems (CustomerGUID, CharInventoryID, ItemIDTag, Quantity, InSlotNumber, CustomData)
                  SELECT @CustomerGUID, CharInventoryID, 'OldShield', 1, 0, '' FROM I",
                new { CustomerGUID = customerGUID, CharName });
            return customerGUID;
        }

        private async Task<long> StoredSaveSeq(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleAsync<long>(
                "SELECT InventorySaveSeq FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharName = @CharName",
                new { CustomerGUID = customerGUID, CharName });
        }

        private async Task<long> Revision(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleAsync<long>(
                "SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharName = @CharName",
                new { CustomerGUID = customerGUID, CharName });
        }

        private async Task<string[]> Items(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return (await connection.QueryAsync<string>(
                "SELECT ItemIDTag FROM CharInventoryItems WHERE CustomerGUID = @CustomerGUID ORDER BY InSlotNumber",
                new { CustomerGUID = customerGUID })).ToArray();
        }

        private async Task<string[]> Quests(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return (await connection.QueryAsync<string>(
                "SELECT QuestIDTag FROM CharQuests WHERE CustomerGUID = @CustomerGUID ORDER BY QuestIDTag",
                new { CustomerGUID = customerGUID })).ToArray();
        }

        private async Task<int> Stat(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleAsync<int>(
                "SELECT Value FROM CharStats WHERE CustomerGUID = @CustomerGUID AND StatIdentifier = 'BaseLevel'",
                new { CustomerGUID = customerGUID });
        }
    }
}
