using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// The inventory save re-checks the owner token inside its own transaction, after locking the character row. The
    /// controller's check runs earlier on another connection, so a claim that commits between the two used to let the
    /// replaced zone server overwrite the new owner's bag. CharacterOwners exists only in the migrated schema.
    /// </summary>
    public class InventoryOwnerTokenTests : IClassFixture<PostgresTestDatabases>
    {
        private const int MapInstanceID = 1;

        private readonly PostgresTestDatabases _databases;

        public InventoryOwnerTokenTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task OldOwnerSave_AfterClaimPastHandover_IsRefusedAndBagKept(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacterWithItem(source, customerGUID, "Handoff", "NewOwnersSword");
            Guid oldToken = (await Owners(source).ClaimByName(customerGUID, "Handoff", MapInstanceID)).OwnerToken;

            // The controller's check passes for the old zone server...
            Assert.False(await Owners(source).IsStaleOwnerWrite(customerGUID, "Handoff", oldToken));

            // ...then the character moves on and the handover window runs out before the save reaches the database.
            ClaimCharacterResult claim = await Owners(source).ClaimByName(customerGUID, "Handoff", MapInstanceID + 1);
            Assert.True(claim.Success);
            await Execute(source, @"UPDATE CharacterOwners SET PreviousOwnerUntil = NOW() - INTERVAL '1 second'
                WHERE CustomerGUID = @CustomerGUID", new { CustomerGUID = customerGUID });

            UpdateCharacterInventoryResponse result = await Characters(source).UpdateCharacterInventory(customerGUID, "Handoff",
                Bag("OldOwnersJunk"), oldToken);

            Assert.False(result.Success);
            Assert.Equal("stale_owner", result.ReasonCode);
            Assert.Equal(new[] { "NewOwnersSword" }, await BagItems(source, customerGUID, "Handoff"));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task OldOwnerSave_WaitingOnTheRowLockWhileAClaimCommits_IsRefused(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacterWithItem(source, customerGUID, "Racer", "NewOwnersSword");
            Guid oldToken = (await Owners(source).ClaimByName(customerGUID, "Racer", MapInstanceID)).OwnerToken;

            // A claim in flight: it holds the character row lock, as SetOwner does, and replaces the owner outright.
            await using var claimConnection = new NpgsqlConnection(_databases.ConnectionString(source));
            await claimConnection.OpenAsync();
            await using NpgsqlTransaction claimTx = await claimConnection.BeginTransactionAsync();
            await claimConnection.ExecuteAsync(@"SELECT CharacterID FROM Characters
                WHERE CustomerGUID = @CustomerGUID AND CharName = 'Racer' FOR UPDATE", new { CustomerGUID = customerGUID }, claimTx);
            await claimConnection.ExecuteAsync(@"UPDATE CharacterOwners SET OwnerToken = @NewToken, PreviousOwnerToken = NULL,
                PreviousOwnerUntil = NULL WHERE CustomerGUID = @CustomerGUID",
                new { CustomerGUID = customerGUID, NewToken = Guid.NewGuid() }, claimTx);

            Task<UpdateCharacterInventoryResponse> save = Characters(source).UpdateCharacterInventory(customerGUID, "Racer",
                Bag("OldOwnersJunk"), oldToken);
            await WaitUntilBlockedOnLock(source, save);

            await claimTx.CommitAsync();
            UpdateCharacterInventoryResponse result = await save;

            Assert.False(result.Success);
            Assert.Equal("stale_owner", result.ReasonCode);
            Assert.Equal(new[] { "NewOwnersSword" }, await BagItems(source, customerGUID, "Racer"));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task ReplacedOwnerSave_DuringHandover_StillLands(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacterWithItem(source, customerGUID, "Traveller", "Sword");
            Guid oldToken = (await Owners(source).ClaimByName(customerGUID, "Traveller", MapInstanceID)).OwnerToken;
            ClaimCharacterResult claim = await Owners(source).ClaimByName(customerGUID, "Traveller", MapInstanceID + 1);
            Assert.True(claim.HandoverSeconds > 0);

            UpdateCharacterInventoryResponse oldSave = await Characters(source).UpdateCharacterInventory(customerGUID, "Traveller",
                Bag("LastSecondLoot"), oldToken);
            Assert.True(oldSave.Success, oldSave.ReasonCode);
            Assert.Equal(new[] { "LastSecondLoot" }, await BagItems(source, customerGUID, "Traveller"));

            UpdateCharacterInventoryResponse newSave = await Characters(source).UpdateCharacterInventory(customerGUID, "Traveller",
                Bag("Shield"), claim.OwnerToken);
            Assert.True(newSave.Success, newSave.ReasonCode);
            Assert.Equal(new[] { "Shield" }, await BagItems(source, customerGUID, "Traveller"));
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task UnclaimedCharacter_TokenlessSaveStillLands(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacterWithItem(source, customerGUID, "Legacy", "Sword");

            UpdateCharacterInventoryResponse result = await Characters(source).UpdateCharacterInventory(customerGUID, "Legacy",
                Bag("Shield"), null);

            Assert.True(result.Success, result.ReasonCode);
            Assert.Equal(new[] { "Shield" }, await BagItems(source, customerGUID, "Legacy"));
        }

        private static List<UpdateCharacterInventory> Bag(string item) =>
            new() { new UpdateCharacterInventory { ItemIDTag = item, Quantity = 1, InSlotNumber = 0 } };

        private StorageOptions Storage(SchemaSource source) =>
            new() { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) };

        private CharactersRepository Characters(SchemaSource source) => new(Microsoft.Extensions.Options.Options.Create(Storage(source)));

        private CharacterOwnersRepository Owners(SchemaSource source) => new(Microsoft.Extensions.Options.Options.Create(Storage(source)));

        private async Task Execute(SchemaSource source, string sql, object parameters)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            await connection.ExecuteAsync(sql, parameters);
        }

        // The save must reach the row lock before the claim commits, or the test proves nothing about the race.
        private async Task WaitUntilBlockedOnLock(SchemaSource source, Task save)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            for (int attempt = 0; attempt < 200; attempt++)
            {
                Assert.False(save.IsCompleted, "The save finished while the claim still held the character row lock.");
                bool blocked = await connection.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%FOR UPDATE%')");
                if (blocked)
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("The save never blocked on the character row lock.");
        }

        private async Task<string[]> BagItems(SchemaSource source, Guid customerGUID, string charName)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            IEnumerable<string> items = await connection.QueryAsync<string>(@"SELECT CII.ItemIDTag FROM CharInventoryItems CII
                JOIN CharInventory CI ON CI.CustomerGUID = CII.CustomerGUID AND CI.CharInventoryID = CII.CharInventoryID
                JOIN Characters C ON C.CustomerGUID = CI.CustomerGUID AND C.CharacterID = CI.CharacterID
                WHERE C.CustomerGUID = @CustomerGUID AND C.CharName = @CharName ORDER BY CII.ItemIDTag",
                new { CustomerGUID = customerGUID, CharName = charName });
            return items.ToArray();
        }

        private async Task SeedCharacterWithItem(SchemaSource source, Guid customerGUID, string charName, string item)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            int classID = await connection.QuerySingleAsync<int>(
                @"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                  VALUES (@CustomerGUID, 'TestClass', 'TestMap', 0, 0, 0)
                  RETURNING ClassID",
                new { CustomerGUID = customerGUID });
            int characterID = await connection.QuerySingleAsync<int>(
                @"INSERT INTO Characters (CustomerGUID, CharName, X, Y, Z, ClassID)
                  VALUES (@CustomerGUID, @CharName, 0, 0, 0, @ClassID)
                  RETURNING CharacterID",
                new { CustomerGUID = customerGUID, CharName = charName, ClassID = classID });
            int inventoryID = await connection.QuerySingleAsync<int>(
                @"INSERT INTO CharInventory (CustomerGUID, CharacterID, InventoryName, InventorySize)
                  VALUES (@CustomerGUID, @CharacterID, 'Bag', 16)
                  RETURNING CharInventoryID",
                new { CustomerGUID = customerGUID, CharacterID = characterID });
            await connection.ExecuteAsync(
                @"INSERT INTO CharInventoryItems (CustomerGUID, CharInventoryID, ItemIDTag, Quantity, InSlotNumber)
                  VALUES (@CustomerGUID, @CharInventoryID, @Item, 1, 0)",
                new { CustomerGUID = customerGUID, CharInventoryID = inventoryID, Item = item });
        }
    }
}
