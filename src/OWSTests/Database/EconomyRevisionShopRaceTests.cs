using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSCharacterPersistence.Requests.Characters;
using OWSCharacterPersistence.Services;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Implementations;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// Issue #215: a zone server's bag or wallet autosave computed before a shop purchase committed lands after it.
    /// Sent with the revision it was computed from, it is refused stale_revision and the purchase stands. Sent with
    /// none, it is counted so the RequireExpectedRevision rollout is visible.
    /// </summary>
    public class EconomyRevisionShopRaceTests : IClassFixture<PostgresTestDatabases>
    {
        private const long StartingRevision = 4;
        private const int BuyerStartingGold = 100;
        private const int UnitPrice = 10;
        private const int TaxRateBps = 500; // ceil(10 * 5%) = 1
        private const int BuyerGoldAfterPurchase = BuyerStartingGold - UnitPrice - 1;

        private readonly PostgresTestDatabases _databases;

        public EconomyRevisionShopRaceTests(PostgresTestDatabases databases) => _databases = databases;

        [PostgresFact]
        public async Task PrePurchaseBagWithItsRevision_IsRefusedAndTheBoughtItemStays()
        {
            Scenario s = await BuyAndDeliverOneSword();

            UpdateCharacterInventoryResponse result = await Inventory(s, PrePurchaseBag(), StartingRevision, new EconomyOptions()).Handle();

            Assert.False(result.Success);
            Assert.Equal("stale_revision", result.ReasonCode);
            Assert.Equal(s.RevisionAfterDelivery, result.NewEconomyRevision);
            Assert.Equal(new[] { "Potion", "Sword" }, await Items(s));
            Assert.Equal(s.RevisionAfterDelivery, await Revision(s));
        }

        [PostgresFact]
        public async Task PrePurchaseWalletWithItsRevision_IsRefusedAsAFailureAndThePriceStaysPaid()
        {
            Scenario s = await BuyAndDeliverOneSword();

            UpdateCharacterCurrencyResponse result = await Currency(s, BuyerStartingGold, StartingRevision, new EconomyOptions()).Handle();

            // Success=true here let a caller that checks only Success adopt NewEconomyRevision and write the same
            // stale wallet again on its next save, this time past the guard.
            Assert.False(result.Success);
            Assert.Equal("stale_revision", result.ReasonCode);
            Assert.Equal("stale_revision", result.ErrorMessage);
            Assert.Equal(s.RevisionAfterDelivery, result.NewEconomyRevision);
            Assert.Equal(BuyerGoldAfterPurchase, result.AuthoritativeGold);
            Assert.Equal(BuyerGoldAfterPurchase, await Gold(s));
            Assert.Equal(s.RevisionAfterDelivery, await Revision(s));
        }

        [PostgresFact]
        public async Task SavesWithoutARevision_AreCounted()
        {
            Scenario s = await BuyAndDeliverOneSword();
            var monitor = new RevisionlessSaveMonitor(Serilog.Core.Logger.None);

            Assert.False((await Inventory(s, PrePurchaseBag(), null, Required, monitor).Handle()).Success);
            Assert.False((await Currency(s, BuyerStartingGold, null, Required, monitor).Handle()).Success);
            Assert.Equal(2, monitor.Total);
            Assert.Equal(new[] { "Potion", "Sword" }, await Items(s));
            Assert.Equal(BuyerGoldAfterPurchase, await Gold(s));

            // A save that carries its revision is on the protocol and is not counted.
            Assert.True((await Currency(s, BuyerGoldAfterPurchase, s.RevisionAfterDelivery, Required, monitor).Handle()).Success);
            Assert.Equal(2, monitor.Total);
        }

        private static readonly EconomyOptions Required = new EconomyOptions { RequireExpectedRevision = true };

        private sealed class Scenario
        {
            public Guid CustomerGUID;
            public int BuyerID;
            public string BuyerName;
            public long RevisionAfterDelivery;
        }

        // The buyer starts at revision 4 with a potion, buys a sword (revision 5) and the zone delivers it into the bag
        // (revision 6). The zone's autosave snapshot from before the purchase is the potion alone, 100 gold, revision 4.
        private async Task<Scenario> BuyAndDeliverOneSword()
        {
            string connectionString = _databases.ConnectionString(SchemaSource.Migrated);
            await using var conn = new NpgsqlConnection(connectionString);
            var shops = new PlayerShopsRepository(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                OWSDBBackend = "postgres", OWSDBConnectionString = connectionString
            }));
            Guid customerGUID = Guid.NewGuid();
            (int ownerID, _) = await SeedCharacter(conn, customerGUID, "RaceOwner", 0, 0, "Sword");
            (int buyerID, string buyerName) = await SeedCharacter(conn, customerGUID, "RaceBuyer", BuyerStartingGold, StartingRevision, "Potion");

            PlayerShopResult created = await shops.CreateShop(customerGUID, new CreateShopInput
            {
                OperationId = Guid.NewGuid(), OwnerCharacterID = ownerID, ShopName = "Race Shop",
                ZoneName = "TestMap", ExpectedRevision = 0, OpeningFeeGold = 0,
                Listings = new List<PlayerShopListingInput>
                {
                    new() { ListingIndex = 0, ItemIDTag = "Sword", Quantity = 1, UnitPriceGold = UnitPrice, TaxRateBps = TaxRateBps }
                },
                PostEscrowInventory = new List<UpdateCharacterInventory>()
            });
            Assert.True(created.Success, created.ReasonCode);

            long itemID = await conn.ExecuteScalarAsync<long>(
                "SELECT PlayerShopItemID FROM PlayerShopItems WHERE PlayerShopID = @PlayerShopID", new { created.PlayerShopID });
            Guid purchaseOperationId = Guid.NewGuid();
            PurchaseResult purchase = await shops.Purchase(customerGUID, new PurchaseInput
            {
                OperationId = purchaseOperationId, PlayerShopID = created.PlayerShopID, PlayerShopItemID = itemID,
                Quantity = 1, BuyerCharacterID = buyerID, ExpectedUnitPrice = UnitPrice, BuyerExpectedRevision = StartingRevision
            });
            Assert.True(purchase.Success, purchase.ReasonCode);
            Assert.Equal(StartingRevision + 1, purchase.NewBuyerRevision);

            SuccessAndErrorMessage delivered = await shops.ConfirmDelivery(customerGUID, new ConfirmDeliveryInput
            {
                OperationId = purchaseOperationId,
                BuyerCharacterID = buyerID,
                ExpectedRevision = purchase.NewBuyerRevision,
                PostDeliveryInventory = PrePurchaseBag().Append(Item("Sword", 1)).ToList()
            });
            Assert.True(delivered.Success, delivered.ErrorMessage);

            var scenario = new Scenario { CustomerGUID = customerGUID, BuyerID = buyerID, BuyerName = buyerName };
            scenario.RevisionAfterDelivery = await Revision(scenario);
            Assert.Equal(StartingRevision + 2, scenario.RevisionAfterDelivery);
            Assert.Equal(new[] { "Potion", "Sword" }, await Items(scenario));
            Assert.Equal(BuyerGoldAfterPurchase, await Gold(scenario));
            return scenario;
        }

        private static List<UpdateCharacterInventory> PrePurchaseBag() => new() { Item("Potion", 0) };

        private static UpdateCharacterInventory Item(string tag, int slot) =>
            new UpdateCharacterInventory { ItemIDTag = tag, Quantity = 1, InSlotNumber = slot, CustomData = "" };

        private CharactersRepository Characters() =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(SchemaSource.Migrated)
            }));

        private UpdateCharacterInventoryRequest Inventory(Scenario s, List<UpdateCharacterInventory> bag, long? expectedRevision,
            EconomyOptions options, RevisionlessSaveMonitor monitor = null)
        {
            var request = new UpdateCharacterInventoryRequest { CharacterName = s.BuyerName, Inventory = bag, ExpectedRevision = expectedRevision };
            request.SetData(Characters(), new HeaderCustomerGUID { CustomerGUID = s.CustomerGUID }, options,
                monitor ?? new RevisionlessSaveMonitor(Serilog.Core.Logger.None));
            return request;
        }

        private UpdateCharacterCurrencyRequest Currency(Scenario s, int gold, long? expectedRevision,
            EconomyOptions options, RevisionlessSaveMonitor monitor = null)
        {
            var request = new UpdateCharacterCurrencyRequest { CharacterName = s.BuyerName, Gold = gold, ExpectedRevision = expectedRevision };
            request.SetData(Characters(), new HeaderCustomerGUID { CustomerGUID = s.CustomerGUID }, options,
                monitor ?? new RevisionlessSaveMonitor(Serilog.Core.Logger.None));
            return request;
        }

        private static async Task<(int CharacterID, string Name)> SeedCharacter(NpgsqlConnection conn, Guid customerGUID, string name,
            int gold, long revision, string itemIDTag)
        {
            string charName = name + customerGUID.ToString("N").Substring(0, 8);
            Guid userGUID = Guid.NewGuid();
            await conn.ExecuteAsync(@"INSERT INTO Users
                (UserGUID, CustomerGUID, FirstName, LastName, Email, PasswordHash, Role)
                VALUES (@UserGUID, @CustomerGUID, 'Test', 'User', @Email, 'x', 'Player')",
                new { UserGUID = userGUID, CustomerGUID = customerGUID, Email = $"{userGUID:N}@example.com" });
            int classID = await conn.ExecuteScalarAsync<int>(@"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                VALUES (@CustomerGUID, @ClassName, 'TestMap', 0, 0, 0) RETURNING ClassID",
                new { CustomerGUID = customerGUID, ClassName = "Class" + name });
            int id = await conn.ExecuteScalarAsync<int>(@"INSERT INTO Characters
                (CustomerGUID, UserGUID, CharName, X, Y, Z, ClassID, Gold, EconomyRevision)
                VALUES (@CustomerGUID, @UserGUID, @Name, 0, 0, 0, @ClassID, @Gold, @Revision)
                RETURNING CharacterID",
                new { CustomerGUID = customerGUID, UserGUID = userGUID, Name = charName, ClassID = classID, Gold = gold, Revision = revision });
            int inventoryID = await conn.ExecuteScalarAsync<int>(@"INSERT INTO CharInventory
                (CustomerGUID, CharacterID, InventoryName, InventorySize) VALUES (@CustomerGUID, @CharacterID, 'Bag', 16)
                RETURNING CharInventoryID", new { CustomerGUID = customerGUID, CharacterID = id });
            await conn.ExecuteAsync(@"INSERT INTO CharInventoryItems
                (CustomerGUID, CharInventoryID, ItemIDTag, Quantity, InSlotNumber, CustomData)
                VALUES (@CustomerGUID, @CharInventoryID, @ItemIDTag, 1, 0, '')",
                new { CustomerGUID = customerGUID, CharInventoryID = inventoryID, ItemIDTag = itemIDTag });
            return (id, charName);
        }

        private async Task<string[]> Items(Scenario s)
        {
            await using var conn = new NpgsqlConnection(_databases.ConnectionString(SchemaSource.Migrated));
            return (await conn.QueryAsync<string>(@"SELECT i.ItemIDTag FROM CharInventoryItems i
                JOIN CharInventory c ON c.CustomerGUID = i.CustomerGUID AND c.CharInventoryID = i.CharInventoryID
                WHERE c.CustomerGUID = @CustomerGUID AND c.CharacterID = @CharacterID ORDER BY i.ItemIDTag",
                new { s.CustomerGUID, CharacterID = s.BuyerID })).ToArray();
        }

        private async Task<int> Gold(Scenario s)
        {
            await using var conn = new NpgsqlConnection(_databases.ConnectionString(SchemaSource.Migrated));
            return await conn.ExecuteScalarAsync<int>("SELECT Gold FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { s.CustomerGUID, CharacterID = s.BuyerID });
        }

        private async Task<long> Revision(Scenario s)
        {
            await using var conn = new NpgsqlConnection(_databases.ConnectionString(SchemaSource.Migrated));
            return await conn.ExecuteScalarAsync<long>("SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { s.CustomerGUID, CharacterID = s.BuyerID });
        }
    }
}
