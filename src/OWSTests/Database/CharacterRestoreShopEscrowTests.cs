using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    public class CharacterRestoreShopEscrowTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public CharacterRestoreShopEscrowTests(PostgresTestDatabases databases) => _databases = databases;

        [PostgresFact]
        public async Task Restore_RefusesShopCreatedAfterBackup_WithoutChangingInventoryOrListing()
        {
            string liveConnectionString = _databases.ConnectionString(SchemaSource.Migrated);
            string stagingConnectionString = await _databases.CreateDatabaseAsync(SchemaSource.Migrated);
            Guid customerGUID = Guid.NewGuid();
            Guid userGUID = Guid.NewGuid();
            const string name = "EscrowOwner";
            const string dump = "openworldserver-20261103T120000Z.dump";

            await using var live = new NpgsqlConnection(liveConnectionString);
            await using var staging = new NpgsqlConnection(stagingConnectionString);
            int characterID = await SeedCharacter(live, customerGUID, userGUID, name, null);
            await SeedCharacter(staging, customerGUID, userGUID, name, characterID);
            await staging.ExecuteAsync("CREATE TABLE staging_meta (dump TEXT NOT NULL); INSERT INTO staging_meta VALUES (@Dump)", new { Dump = dump });

            PlayerShopResult created = await CreateShop(liveConnectionString, customerGUID, characterID);
            Assert.Equal(0, await BagCount(live, customerGUID, characterID));

            var restore = new CharacterRestoreRepository(liveConnectionString, stagingConnectionString);
            var comparison = await restore.GetComparison(customerGUID, name);
            Assert.Contains(comparison.Live.ShopsHoldingEscrow, s => s.PlayerShopID == created.PlayerShopID && s.ShopName == "Test Shop");

            string refusal = await restore.Restore(customerGUID, name, dump, "admin");
            Assert.StartsWith("shop_escrow:", refusal);
            Assert.Contains("Test Shop", refusal);
            Assert.Equal(0, await BagCount(live, customerGUID, characterID));
            Assert.Equal(1, await live.ExecuteScalarAsync<int>(
                "SELECT QuantityRemainingForSale FROM PlayerShopItems WHERE PlayerShopID = @PlayerShopID",
                new { created.PlayerShopID }));
            Assert.Equal(1, await live.ExecuteScalarAsync<int>(
                "SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID }));
        }

        [PostgresFact]
        public async Task Restore_AllowsShopAlreadyInBackup_WithoutChangingListing()
        {
            string liveConnectionString = _databases.ConnectionString(SchemaSource.Migrated);
            string stagingConnectionString = await _databases.CreateDatabaseAsync(SchemaSource.Migrated);
            Guid customerGUID = Guid.NewGuid();
            Guid userGUID = Guid.NewGuid();
            const string name = "EarlierShopOwner";
            const string dump = "openworldserver-20261103T130000Z.dump";

            await using var live = new NpgsqlConnection(liveConnectionString);
            await using var staging = new NpgsqlConnection(stagingConnectionString);
            int characterID = await SeedCharacter(live, customerGUID, userGUID, name, null);
            await SeedCharacter(staging, customerGUID, userGUID, name, characterID);
            PlayerShopResult created = await CreateShop(liveConnectionString, customerGUID, characterID);

            // This backup was taken after CreateShop: the item is already out of the bag,
            // and the same shop and listing are present in the staged database.
            await staging.ExecuteAsync(@"DELETE FROM CharInventoryItems CII USING CharInventory CI
                WHERE CII.CustomerGUID = CI.CustomerGUID AND CII.CharInventoryID = CI.CharInventoryID
                  AND CI.CustomerGUID = @CustomerGUID AND CI.CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID });
            await staging.ExecuteAsync(@"INSERT INTO PlayerShops
                (PlayerShopID, CustomerGUID, OwnerCharacterID, OwnerUserGUID, OwnerCharName, ShopName, ZoneName)
                VALUES (@PlayerShopID, @CustomerGUID, @CharacterID, @UserGUID, @Name, 'Test Shop', 'TestMap')",
                new { created.PlayerShopID, CustomerGUID = customerGUID, CharacterID = characterID, UserGUID = userGUID, Name = name });
            await staging.ExecuteAsync(@"INSERT INTO PlayerShopItems
                (PlayerShopID, ListingIndex, ItemIDTag, QuantityListed, QuantityRemainingForSale, UnitPriceGold)
                VALUES (@PlayerShopID, 0, 'Sword', 1, 1, 10)", new { created.PlayerShopID });
            await staging.ExecuteAsync("CREATE TABLE staging_meta (dump TEXT NOT NULL); INSERT INTO staging_meta VALUES (@Dump)", new { Dump = dump });

            var restore = new CharacterRestoreRepository(liveConnectionString, stagingConnectionString);
            var comparison = await restore.GetComparison(customerGUID, name);
            Assert.Empty(comparison.Live.ShopsHoldingEscrow);
            Assert.Null(await restore.Restore(customerGUID, name, dump, "admin"));
            Assert.Equal(0, await BagCount(live, customerGUID, characterID));
            Assert.Equal(1, await live.ExecuteScalarAsync<int>(
                "SELECT QuantityRemainingForSale FROM PlayerShopItems WHERE PlayerShopID = @PlayerShopID",
                new { created.PlayerShopID }));
        }

        [PostgresFact]
        public async Task Restore_RollsBackCharacterWhenRestoredEventInsertFails()
        {
            string liveConnectionString = await _databases.CreateDatabaseAsync(SchemaSource.Migrated);
            string stagingConnectionString = await _databases.CreateDatabaseAsync(SchemaSource.Migrated);
            Guid customerGUID = Guid.NewGuid();
            Guid userGUID = Guid.NewGuid();
            const string name = "AuditOwner";
            const string dump = "openworldserver-20261103T140000Z.dump";

            await using var live = new NpgsqlConnection(liveConnectionString);
            await using var staging = new NpgsqlConnection(stagingConnectionString);
            int characterID = await SeedCharacter(live, customerGUID, userGUID, name, null);
            await SeedCharacter(staging, customerGUID, userGUID, name, characterID);
            await staging.ExecuteAsync("UPDATE Characters SET Gold = 77 WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID });
            await staging.ExecuteAsync("CREATE TABLE staging_meta (dump TEXT NOT NULL); INSERT INTO staging_meta VALUES (@Dump)",
                new { Dump = dump });
            await live.ExecuteAsync(@"CREATE FUNCTION reject_restore_event() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'event insert failed'; END; $$");
            await live.ExecuteAsync(@"CREATE TRIGGER reject_restore_event BEFORE INSERT ON GameEvents
                FOR EACH ROW EXECUTE FUNCTION reject_restore_event()");

            var restore = new CharacterRestoreRepository(liveConnectionString, stagingConnectionString);
            await Assert.ThrowsAsync<PostgresException>(() => restore.Restore(customerGUID, name, dump, "admin"));
            Assert.Equal(0, await live.ExecuteScalarAsync<int>(
                "SELECT Gold FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID }));
            Assert.Equal(0, await live.ExecuteScalarAsync<int>(
                "SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID }));
            Assert.Equal(0, await live.ExecuteScalarAsync<int>("SELECT COUNT(*)::INT FROM GameEvents WHERE CustomerGUID = @CustomerGUID",
                new { CustomerGUID = customerGUID }));

            await live.ExecuteAsync("DROP TRIGGER reject_restore_event ON GameEvents");
            Assert.Null(await restore.Restore(customerGUID, name, dump, "admin"));
            Assert.Equal(77, await live.ExecuteScalarAsync<int>(
                "SELECT Gold FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID }));
            Assert.Equal(1, await live.ExecuteScalarAsync<int>(
                "SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID }));
            Assert.Equal(1, await live.ExecuteScalarAsync<int>(@"SELECT COUNT(*)::INT FROM GameEvents
                WHERE CustomerGUID = @CustomerGUID AND CharacterName = @Name AND Event = 'restored'
                  AND Fields ->> 'backup' = @Dump AND Fields ->> 'by' = 'admin'",
                new { CustomerGUID = customerGUID, Name = name, Dump = dump }));
        }

        private static async Task<PlayerShopResult> CreateShop(string connectionString, Guid customerGUID, int characterID)
        {
            var shops = new PlayerShopsRepository(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                OWSDBBackend = "postgres", OWSDBConnectionString = connectionString
            }));
            PlayerShopResult created = await shops.CreateShop(customerGUID, new CreateShopInput
            {
                OperationId = Guid.NewGuid(), OwnerCharacterID = characterID, ShopName = "Test Shop",
                ZoneName = "TestMap", ExpectedRevision = 0, OpeningFeeGold = 0,
                Listings = new List<PlayerShopListingInput>
                {
                    new() { ListingIndex = 0, ItemIDTag = "Sword", Quantity = 1, UnitPriceGold = 10, TaxRateBps = 500 }
                },
                PostEscrowInventory = new List<OWSData.Models.StoredProcs.UpdateCharacterInventory>()
            });
            Assert.True(created.Success, created.ReasonCode);
            return created;
        }

        private static async Task<int> SeedCharacter(NpgsqlConnection connection, Guid customerGUID, Guid userGUID,
            string name, int? characterID)
        {
            await connection.ExecuteAsync(@"INSERT INTO Users
                (UserGUID, CustomerGUID, FirstName, LastName, Email, PasswordHash, Role)
                VALUES (@UserGUID, @CustomerGUID, 'Test', 'Owner', @Email, 'x', 'Player')",
                new { UserGUID = userGUID, CustomerGUID = customerGUID, Email = $"{userGUID:N}@example.com" });
            int classID = await connection.ExecuteScalarAsync<int>(@"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                VALUES (@CustomerGUID, 'TestClass', 'TestMap', 0, 0, 0) RETURNING ClassID", new { CustomerGUID = customerGUID });
            int id = await connection.ExecuteScalarAsync<int>(@"INSERT INTO Characters
                (CustomerGUID, CharacterID, UserGUID, CharName, X, Y, Z, ClassID, Gold)
                VALUES (@CustomerGUID, COALESCE(@CharacterID, nextval('characters_characterid_seq')), @UserGUID, @Name, 0, 0, 0, @ClassID, 0)
                RETURNING CharacterID", new { CustomerGUID = customerGUID, CharacterID = characterID, UserGUID = userGUID, Name = name, ClassID = classID });
            int inventoryID = await connection.ExecuteScalarAsync<int>(@"INSERT INTO CharInventory
                (CustomerGUID, CharacterID, InventoryName, InventorySize) VALUES (@CustomerGUID, @CharacterID, 'Bag', 16)
                RETURNING CharInventoryID", new { CustomerGUID = customerGUID, CharacterID = id });
            await connection.ExecuteAsync(@"INSERT INTO CharInventoryItems
                (CustomerGUID, CharInventoryID, ItemIDTag, Quantity, InSlotNumber)
                VALUES (@CustomerGUID, @CharInventoryID, 'Sword', 1, 0)",
                new { CustomerGUID = customerGUID, CharInventoryID = inventoryID });
            return id;
        }

        private static Task<int> BagCount(NpgsqlConnection connection, Guid customerGUID, int characterID) =>
            connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*)::INT FROM CharInventoryItems CII
                JOIN CharInventory CI ON CI.CustomerGUID = CII.CustomerGUID AND CI.CharInventoryID = CII.CharInventoryID
                WHERE CI.CustomerGUID = @CustomerGUID AND CI.CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID });
    }
}
