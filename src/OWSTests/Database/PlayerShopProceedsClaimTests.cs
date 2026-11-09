using System;
using System.Collections.Generic;
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
    // Issue #169: proceeds from a purchase that is still 'paid' (not yet delivered or refunded) must not be
    // claimable, or a later ResolveUndelivered refunds the buyer while the owner keeps the sale.
    public class PlayerShopProceedsClaimTests : IClassFixture<PostgresTestDatabases>
    {
        private const int BuyerStartingGold = 100;
        private const int UnitPrice = 10;
        private const int TaxRateBps = 500; // ceil(10 * 5%) = 1

        private readonly PostgresTestDatabases _databases;

        public PlayerShopProceedsClaimTests(PostgresTestDatabases databases) => _databases = databases;

        [PostgresFact]
        public async Task ClaimBeforeResolveUndelivered_ConservesGold()
        {
            string connectionString = _databases.ConnectionString(SchemaSource.Migrated);
            await using var conn = new NpgsqlConnection(connectionString);
            Scenario s = await PurchaseOneSword(conn, connectionString);

            ClaimEscrowResult claim = await ClaimGold(conn, s);
            Assert.True(claim.Success, claim.ReasonCode);
            Assert.Equal(0, claim.GoldClaimed);

            ResolveUndeliveredResult resolve = await s.Shops.ResolveUndelivered(s.CustomerGUID, s.PurchaseOperationId);
            Assert.True(resolve.Success, resolve.ReasonCode);
            Assert.Equal(UnitPrice + 1, resolve.GoldRefunded);

            Assert.Equal(BuyerStartingGold, await Gold(conn, s.CustomerGUID, s.BuyerID));
            Assert.Equal(0, await Gold(conn, s.CustomerGUID, s.OwnerID));
            Assert.Equal(0, await ProceedsPending(conn, s));
            Assert.Equal(0, await ProceedsClaimed(conn, s));
        }

        [PostgresFact]
        public async Task ProceedsBecomeClaimableOnceDelivered()
        {
            string connectionString = _databases.ConnectionString(SchemaSource.Migrated);
            await using var conn = new NpgsqlConnection(connectionString);
            Scenario s = await PurchaseOneSword(conn, connectionString);

            ClaimEscrowResult early = await ClaimGold(conn, s);
            Assert.True(early.Success, early.ReasonCode);
            Assert.Equal(0, early.GoldClaimed);
            Assert.Equal("partially_claimed", early.SettlementStatus);

            SuccessAndErrorMessage delivered = await s.Shops.ConfirmDelivery(s.CustomerGUID, new ConfirmDeliveryInput
            {
                OperationId = s.PurchaseOperationId,
                BuyerCharacterID = s.BuyerID,
                ExpectedRevision = await Revision(conn, s.CustomerGUID, s.BuyerID),
                PostDeliveryInventory = new List<UpdateCharacterInventory>()
            });
            Assert.True(delivered.Success, delivered.ErrorMessage);

            ClaimEscrowResult claim = await ClaimGold(conn, s);
            Assert.True(claim.Success, claim.ReasonCode);
            Assert.Equal(UnitPrice, claim.GoldClaimed);
            Assert.Equal(UnitPrice, await Gold(conn, s.CustomerGUID, s.OwnerID));
            Assert.Equal(0, await ProceedsPending(conn, s));
        }

        [PostgresFact]
        public async Task ResolveUndelivered_RefusesWhenProceedsWereAlreadyClaimed()
        {
            // Rows left by a claim made before the fix: the proceeds of a still-'paid' purchase are in the
            // owner's wallet. Refunding now would mint gold, so the refund is refused and the purchase stays
            // 'paid' (the zone can still deliver it).
            string connectionString = _databases.ConnectionString(SchemaSource.Migrated);
            await using var conn = new NpgsqlConnection(connectionString);
            Scenario s = await PurchaseOneSword(conn, connectionString);
            await conn.ExecuteAsync(@"UPDATE PlayerShops
                SET ProceedsGoldClaimed = ProceedsGoldClaimed + ProceedsGoldPending, ProceedsGoldPending = 0
                WHERE PlayerShopID = @PlayerShopID", new { s.PlayerShopID });
            int buyerGold = await Gold(conn, s.CustomerGUID, s.BuyerID);
            long buyerRevision = await Revision(conn, s.CustomerGUID, s.BuyerID);

            ResolveUndeliveredResult resolve = await s.Shops.ResolveUndelivered(s.CustomerGUID, s.PurchaseOperationId);
            Assert.False(resolve.Success);
            Assert.Equal("proceeds_claimed", resolve.ReasonCode);

            Assert.Equal(buyerGold, await Gold(conn, s.CustomerGUID, s.BuyerID));
            Assert.Equal(buyerRevision, await Revision(conn, s.CustomerGUID, s.BuyerID));
            Assert.Equal("paid", await conn.ExecuteScalarAsync<string>(
                "SELECT State FROM PlayerShopPurchases WHERE OperationId = @OperationId",
                new { OperationId = s.PurchaseOperationId }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT QuantityRemainingForSale FROM PlayerShopItems WHERE PlayerShopID = @PlayerShopID",
                new { s.PlayerShopID }));
        }

        private sealed class Scenario
        {
            public PlayerShopsRepository Shops;
            public Guid CustomerGUID;
            public int OwnerID;
            public int BuyerID;
            public long PlayerShopID;
            public Guid PurchaseOperationId;
        }

        private static async Task<Scenario> PurchaseOneSword(NpgsqlConnection conn, string connectionString)
        {
            var shops = new PlayerShopsRepository(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                OWSDBBackend = "postgres", OWSDBConnectionString = connectionString
            }));
            Guid customerGUID = Guid.NewGuid();
            int ownerID = await SeedCharacter(conn, customerGUID, "ProceedsOwner", 0, withSword: true);
            int buyerID = await SeedCharacter(conn, customerGUID, "ProceedsBuyer", BuyerStartingGold, withSword: false);

            PlayerShopResult created = await shops.CreateShop(customerGUID, new CreateShopInput
            {
                OperationId = Guid.NewGuid(), OwnerCharacterID = ownerID, ShopName = "Proceeds Shop",
                ZoneName = "TestMap", ExpectedRevision = 0, OpeningFeeGold = 0,
                Listings = new List<PlayerShopListingInput>
                {
                    new() { ListingIndex = 0, ItemIDTag = "Sword", Quantity = 1, UnitPriceGold = UnitPrice, TaxRateBps = TaxRateBps }
                },
                PostEscrowInventory = new List<UpdateCharacterInventory>()
            });
            Assert.True(created.Success, created.ReasonCode);

            long itemID = await conn.ExecuteScalarAsync<long>(
                "SELECT PlayerShopItemID FROM PlayerShopItems WHERE PlayerShopID = @PlayerShopID",
                new { created.PlayerShopID });
            Guid purchaseOperationId = Guid.NewGuid();
            PurchaseResult purchase = await shops.Purchase(customerGUID, new PurchaseInput
            {
                OperationId = purchaseOperationId, PlayerShopID = created.PlayerShopID, PlayerShopItemID = itemID,
                Quantity = 1, BuyerCharacterID = buyerID, ExpectedUnitPrice = UnitPrice, BuyerExpectedRevision = 0
            });
            Assert.True(purchase.Success, purchase.ReasonCode);
            Assert.Equal(UnitPrice, purchase.ProceedsCredited);
            Assert.Equal(1, purchase.TaxPaid);

            return new Scenario
            {
                Shops = shops, CustomerGUID = customerGUID, OwnerID = ownerID, BuyerID = buyerID,
                PlayerShopID = created.PlayerShopID, PurchaseOperationId = purchaseOperationId
            };
        }

        private static async Task<ClaimEscrowResult> ClaimGold(NpgsqlConnection conn, Scenario s) =>
            await s.Shops.ClaimEscrow(s.CustomerGUID, new ClaimEscrowInput
            {
                OperationId = Guid.NewGuid(), PlayerShopID = s.PlayerShopID, OwnerCharacterID = s.OwnerID,
                ClaimGold = true, ClaimXp = true, ExpectedRevision = await Revision(conn, s.CustomerGUID, s.OwnerID),
                PostClaimInventory = new List<UpdateCharacterInventory>()
            });

        private static async Task<int> SeedCharacter(NpgsqlConnection conn, Guid customerGUID, string name, int gold, bool withSword)
        {
            Guid userGUID = Guid.NewGuid();
            await conn.ExecuteAsync(@"INSERT INTO Users
                (UserGUID, CustomerGUID, FirstName, LastName, Email, PasswordHash, Role)
                VALUES (@UserGUID, @CustomerGUID, 'Test', 'User', @Email, 'x', 'Player')",
                new { UserGUID = userGUID, CustomerGUID = customerGUID, Email = $"{userGUID:N}@example.com" });
            int classID = await conn.ExecuteScalarAsync<int>(@"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                VALUES (@CustomerGUID, @ClassName, 'TestMap', 0, 0, 0) RETURNING ClassID",
                new { CustomerGUID = customerGUID, ClassName = "Class" + name });
            int id = await conn.ExecuteScalarAsync<int>(@"INSERT INTO Characters
                (CustomerGUID, UserGUID, CharName, X, Y, Z, ClassID, Gold)
                VALUES (@CustomerGUID, @UserGUID, @Name, 0, 0, 0, @ClassID, @Gold)
                RETURNING CharacterID",
                new { CustomerGUID = customerGUID, UserGUID = userGUID, Name = name + customerGUID.ToString("N").Substring(0, 8), ClassID = classID, Gold = gold });
            int inventoryID = await conn.ExecuteScalarAsync<int>(@"INSERT INTO CharInventory
                (CustomerGUID, CharacterID, InventoryName, InventorySize) VALUES (@CustomerGUID, @CharacterID, 'Bag', 16)
                RETURNING CharInventoryID", new { CustomerGUID = customerGUID, CharacterID = id });
            if (withSword)
            {
                await conn.ExecuteAsync(@"INSERT INTO CharInventoryItems
                    (CustomerGUID, CharInventoryID, ItemIDTag, Quantity, InSlotNumber)
                    VALUES (@CustomerGUID, @CharInventoryID, 'Sword', 1, 0)",
                    new { CustomerGUID = customerGUID, CharInventoryID = inventoryID });
            }
            return id;
        }

        private static Task<int> Gold(NpgsqlConnection conn, Guid customerGUID, int characterID) =>
            conn.ExecuteScalarAsync<int>("SELECT Gold FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID });

        private static Task<long> Revision(NpgsqlConnection conn, Guid customerGUID, int characterID) =>
            conn.ExecuteScalarAsync<long>("SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID });

        private static Task<int> ProceedsPending(NpgsqlConnection conn, Scenario s) =>
            conn.ExecuteScalarAsync<int>("SELECT ProceedsGoldPending FROM PlayerShops WHERE PlayerShopID = @PlayerShopID", new { s.PlayerShopID });

        private static Task<int> ProceedsClaimed(NpgsqlConnection conn, Scenario s) =>
            conn.ExecuteScalarAsync<int>("SELECT ProceedsGoldClaimed FROM PlayerShops WHERE PlayerShopID = @PlayerShopID", new { s.PlayerShopID });
    }
}
