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
    /// api/Characters/UpdateCharacterData saves quests, inventory and stats in one transaction: a failure in any
    /// section, a missing section, or a save from a zone or owner token that no longer owns the character writes nothing.
    /// Runs on the migrated schema only, because setup.sql alone has no CharQuests, CharStats or CharacterOwners tables.
    /// </summary>
    public class UpdateCharacterDataTests : IClassFixture<PostgresTestDatabases>
    {
        private const string CharName = "SaveTester";
        private const int OwningZone = 101;
        private const int PreviousZone = 100;

        private readonly PostgresTestDatabases _databases;

        public UpdateCharacterDataTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task SavesAllThreeSections(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);

            SuccessAndErrorMessage result = await Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), OwningZone).Handle();

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("", result.ErrorMessage ?? "");
            Assert.Equal(new[] { "Quest.First", "Quest.Second" }, await Quests(source, customerGUID));
            Assert.Equal(new[] { "NewSword" }, await Items(source, customerGUID));
            Assert.Equal(7, await Stat(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task FailingQuestWritesNothing(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);

            // CharQuests.QuestIDTag is VARCHAR(50), so the second quest fails after the first was written.
            SuccessAndErrorMessage result = await Request(source, customerGUID, NewQuests(new string('Q', 60)), NewInventory(), NewStats(), OwningZone).Handle();

            Assert.False(result.Success);
            await AssertUnchanged(source, customerGUID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task MissingInventoryKeepsBag(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);

            SuccessAndErrorMessage result = await Request(source, customerGUID, NewQuests("Quest.Second"), null, NewStats(), OwningZone).Handle();

            Assert.False(result.Success);
            await AssertUnchanged(source, customerGUID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task MissingQuestsOrStatsWritesNothing(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);

            Assert.False((await Request(source, customerGUID, null, NewInventory(), NewStats(), OwningZone).Handle()).Success);
            Assert.False((await Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), null, OwningZone).Handle()).Success);
            await AssertUnchanged(source, customerGUID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task PreviousZoneAfterHandoffWritesNothing(SchemaSource source)
        {
            // The controller's owner guard still lets the previous owner's token through during the handover window;
            // the zone check is what stops the old zone's save.
            Guid customerGUID = await SeedCharacter(source);

            SuccessAndErrorMessage result = await Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), PreviousZone).Handle();

            Assert.True(result.Success);
            Assert.Equal("stale_zone", result.ErrorMessage);
            await AssertUnchanged(source, customerGUID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task StaleRevisionSkipsBagButSavesQuestsAndStats(SchemaSource source)
        {
            // A shop transaction committed after the zone computed its bag: the bag must not be written back,
            // but quests and stats are not economy state and still save, together.
            Guid customerGUID = await SeedCharacter(source);
            long revision = await Revision(source, customerGUID);

            UpdateCharacterDataResponse result = await Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), OwningZone,
                expectedRevision: revision + 1).Handle();

            Assert.False(result.Success);
            Assert.Equal("stale_revision", result.ReasonCode);
            Assert.Equal(revision, result.NewEconomyRevision);
            Assert.Equal(new[] { "OldShield" }, await Items(source, customerGUID));
            Assert.Equal(new[] { "Quest.First", "Quest.Second" }, await Quests(source, customerGUID));
            Assert.Equal(7, await Stat(source, customerGUID));
            Assert.Equal(revision, await Revision(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task MissingRevisionWhenRequiredSkipsBag(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);

            UpdateCharacterDataResponse result = await Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), OwningZone,
                economyOptions: new EconomyOptions { RequireExpectedRevision = true }).Handle();

            Assert.False(result.Success);
            Assert.Equal("revision_required", result.ReasonCode);
            Assert.Equal(new[] { "OldShield" }, await Items(source, customerGUID));
            Assert.Equal(7, await Stat(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task MatchingRevisionWritesBagAndBumpsRevision(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            long revision = await Revision(source, customerGUID);

            UpdateCharacterDataResponse result = await Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), OwningZone,
                expectedRevision: revision, economyOptions: new EconomyOptions { RequireExpectedRevision = true }).Handle();

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(revision + 1, result.NewEconomyRevision);
            Assert.Equal(revision + 1, await Revision(source, customerGUID));
            Assert.Equal(new[] { "NewSword" }, await Items(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task FailingQuestWithMatchingRevisionLeavesRevision(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            long revision = await Revision(source, customerGUID);

            UpdateCharacterDataResponse result = await Request(source, customerGUID, NewQuests(new string('Q', 60)), NewInventory(), NewStats(), OwningZone,
                expectedRevision: revision).Handle();

            Assert.False(result.Success);
            await AssertUnchanged(source, customerGUID);
            Assert.Equal(revision, await Revision(source, customerGUID));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task OldOwnerAfterClaimPastHandoverWritesNothing(SchemaSource source)
        {
            // The save names the zone the character is on, so only the owner token can refuse it.
            Guid customerGUID = await SeedCharacter(source);
            Guid oldToken = (await Owners(source).ClaimByName(customerGUID, CharName, OwningZone)).OwnerToken;

            // The controller's check passes for the old owner...
            Assert.False(await Owners(source).IsStaleOwnerWrite(customerGUID, CharName, oldToken));

            // ...then a second login claims the character and the handover ends before the save reaches the database.
            Assert.True((await Owners(source).ClaimByName(customerGUID, CharName, OwningZone)).Success);
            await ExpireHandover(source, customerGUID);

            UpdateCharacterDataResponse result = await Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), OwningZone,
                ownerToken: oldToken).Handle();

            Assert.False(result.Success);
            Assert.Equal("stale_owner", result.ReasonCode);
            Assert.Equal("stale_owner", result.ErrorMessage);
            await AssertUnchanged(source, customerGUID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task OldOwnerWaitingOnTheRowLockWhileAClaimCommitsWritesNothing(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            Guid oldToken = (await Owners(source).ClaimByName(customerGUID, CharName, OwningZone)).OwnerToken;

            // A claim in flight: it holds the character row lock, as SetOwner does, and replaces the owner outright.
            await using var claimConnection = new NpgsqlConnection(_databases.ConnectionString(source));
            await claimConnection.OpenAsync();
            await using NpgsqlTransaction claimTx = await claimConnection.BeginTransactionAsync();
            await claimConnection.ExecuteAsync(@"SELECT CharacterID FROM Characters
                WHERE CustomerGUID = @CustomerGUID AND CharName = @CharName FOR UPDATE", new { CustomerGUID = customerGUID, CharName }, claimTx);
            await claimConnection.ExecuteAsync(@"UPDATE CharacterOwners SET OwnerToken = @NewToken, PreviousOwnerToken = NULL,
                PreviousOwnerUntil = NULL WHERE CustomerGUID = @CustomerGUID",
                new { CustomerGUID = customerGUID, NewToken = Guid.NewGuid() }, claimTx);

            Task<UpdateCharacterDataResponse> save = Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), OwningZone,
                ownerToken: oldToken).Handle();
            await WaitUntilBlockedOnLock(source, save);

            await claimTx.CommitAsync();
            UpdateCharacterDataResponse result = await save;

            Assert.False(result.Success);
            Assert.Equal("stale_owner", result.ReasonCode);
            await AssertUnchanged(source, customerGUID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task ReplacedOwnerWaitingOnTheRowLockWhileTheHandoverEndsWritesNothing(SchemaSource source)
        {
            Guid customerGUID = await SeedCharacter(source);
            Guid oldToken = (await Owners(source).ClaimByName(customerGUID, CharName, OwningZone)).OwnerToken;
            Assert.True((await Owners(source).ClaimByName(customerGUID, CharName, OwningZone)).HandoverSeconds > 0);

            // Another transaction (a shop purchase, say) holds the character row when the replaced owner's save arrives.
            await using var lockConnection = new NpgsqlConnection(_databases.ConnectionString(source));
            await lockConnection.OpenAsync();
            await using NpgsqlTransaction lockTx = await lockConnection.BeginTransactionAsync();
            await lockConnection.ExecuteAsync(@"SELECT CharacterID FROM Characters
                WHERE CustomerGUID = @CustomerGUID AND CharName = @CharName FOR UPDATE", new { CustomerGUID = customerGUID, CharName }, lockTx);

            Task<UpdateCharacterDataResponse> save = Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), OwningZone,
                ownerToken: oldToken).Handle();
            await WaitUntilBlockedOnLock(source, save);
            await EndHandoverWhileTheSaveWaits(source, customerGUID);

            await lockTx.RollbackAsync();
            UpdateCharacterDataResponse result = await save;

            Assert.False(result.Success);
            Assert.Equal("stale_owner", result.ReasonCode);
            await AssertUnchanged(source, customerGUID);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task ReplacedOwnerDuringHandoverStillSaves(SchemaSource source)
        {
            // Same policy as the controller: the replaced owner's last save lands while the handover runs.
            Guid customerGUID = await SeedCharacter(source);
            Guid oldToken = (await Owners(source).ClaimByName(customerGUID, CharName, OwningZone)).OwnerToken;
            Assert.True((await Owners(source).ClaimByName(customerGUID, CharName, OwningZone)).HandoverSeconds > 0);

            UpdateCharacterDataResponse result = await Request(source, customerGUID, NewQuests("Quest.Second"), NewInventory(), NewStats(), OwningZone,
                ownerToken: oldToken).Handle();

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(new[] { "NewSword" }, await Items(source, customerGUID));
            Assert.Equal(7, await Stat(source, customerGUID));
        }

        private CharacterOwnersRepository Owners(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }));

        private async Task ExpireHandover(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(@"UPDATE CharacterOwners SET PreviousOwnerUntil = NOW() - INTERVAL '1 second'
                WHERE CustomerGUID = @CustomerGUID", new { CustomerGUID = customerGUID });
        }

        // Ends the running handover now, while the save waits on the row lock, and checks the save's transaction began
        // before that. NOW() inside the save is its transaction start, so this is the case it let through.
        private async Task EndHandoverWhileTheSaveWaits(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            bool saveBeganDuringHandover = await connection.QuerySingleAsync<bool>(@"WITH Ended AS (
                    UPDATE CharacterOwners SET PreviousOwnerUntil = clock_timestamp()
                    WHERE CustomerGUID = @CustomerGUID AND PreviousOwnerUntil > clock_timestamp()
                    RETURNING PreviousOwnerUntil)
                SELECT COALESCE((SELECT xact_start FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock') < (SELECT PreviousOwnerUntil FROM Ended), false)",
                new { CustomerGUID = customerGUID });
            Assert.True(saveBeganDuringHandover, "The save's transaction must begin while the handover is still running.");
        }

        // The save must reach the row lock before the claim commits, or the test proves nothing about the race. Each test
        // class has its own database and its tests run one at a time, so any lock wait here is the save's.
        private async Task WaitUntilBlockedOnLock(SchemaSource source, Task save)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            for (int attempt = 0; attempt < 200; attempt++)
            {
                Assert.False(save.IsCompleted, "The save finished while the claim still held the character row lock.");
                if (await connection.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock')"))
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("The save never blocked on the character row lock.");
        }

        private async Task<long> Revision(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return await connection.QuerySingleAsync<long>(
                "SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharName = @CharName",
                new { CustomerGUID = customerGUID, CharName });
        }

        private UpdateCharacterDataRequest Request(SchemaSource source, Guid customerGUID, IEnumerable<UpdateCharacterQuest> quests,
            IEnumerable<UpdateCharacterInventory> inventory, IEnumerable<UpdateCharacterStats> stats, int? zoneInstanceID,
            long? expectedRevision = null, EconomyOptions economyOptions = null, Guid? ownerToken = null)
        {
            var request = new UpdateCharacterDataRequest
            {
                CharacterName = CharName,
                CharQuests = quests,
                CharInventory = inventory,
                CharStats = stats,
                ZoneInstanceID = zoneInstanceID,
                ExpectedRevision = expectedRevision,
                OwnerToken = ownerToken
            };
            request.SetData(
                new CharactersRepository(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) })),
                new HeaderCustomerGUID { CustomerGUID = customerGUID },
                economyOptions ?? new EconomyOptions());
            return request;
        }

        private static List<UpdateCharacterQuest> NewQuests(string secondQuestIDTag) => new()
        {
            new UpdateCharacterQuest { QuestIDTag = "Quest.First", QuestJournalTagContainer = "(Journal=\"new\")", CustomData = "" },
            new UpdateCharacterQuest { QuestIDTag = secondQuestIDTag, QuestJournalTagContainer = "(Journal=\"new\")", CustomData = "" }
        };

        private static List<UpdateCharacterInventory> NewInventory() => new()
        {
            new UpdateCharacterInventory { ItemIDTag = "NewSword", Quantity = 1, InSlotNumber = 0, CustomData = "" }
        };

        private static List<UpdateCharacterStats> NewStats() => new()
        {
            new UpdateCharacterStats { StatIdentifier = "BaseLevel", Value = 7 }
        };

        private async Task AssertUnchanged(SchemaSource source, Guid customerGUID)
        {
            Assert.Empty(await Quests(source, customerGUID));
            Assert.Equal(new[] { "OldShield" }, await Items(source, customerGUID));
            Assert.Equal(1, await Stat(source, customerGUID));
        }

        // A character in OwningZone with one bag item, BaseLevel 1 and no quests.
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
                      SELECT @CustomerGUID, CharacterID, 'BaseLevel', 1 FROM C),
                  M AS (
                      INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID)
                      SELECT @CustomerGUID, CharacterID, @OwningZone FROM C)
                  INSERT INTO CharInventoryItems (CustomerGUID, CharInventoryID, ItemIDTag, Quantity, InSlotNumber, CustomData)
                  SELECT @CustomerGUID, CharInventoryID, 'OldShield', 1, 0, '' FROM I",
                new { CustomerGUID = customerGUID, CharName, OwningZone });
            return customerGUID;
        }

        private async Task<string[]> Quests(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return (await connection.QueryAsync<string>(
                "SELECT QuestIDTag FROM CharQuests WHERE CustomerGUID = @CustomerGUID ORDER BY QuestIDTag",
                new { CustomerGUID = customerGUID })).ToArray();
        }

        private async Task<string[]> Items(SchemaSource source, Guid customerGUID)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            return (await connection.QueryAsync<string>(
                "SELECT ItemIDTag FROM CharInventoryItems WHERE CustomerGUID = @CustomerGUID ORDER BY InSlotNumber",
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
