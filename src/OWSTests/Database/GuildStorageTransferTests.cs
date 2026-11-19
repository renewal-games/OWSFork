using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Grpc.Core;
using GuildServiceApp.Protos;
using Npgsql;
using OWSCharacterPersistence.Requests.Characters;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Grpc;
using OWSShared.Implementations;
using OWSShared.Options;
using Xunit;
using static OWSTests.Guild.GuildStorageTestDoubles;

namespace OWSTests.Database
{
    /// <summary>
    /// Code issue #188, end to end: GuildService on the real GuildStorageRepository and the migrated schema. Guild G has
    /// members A and B; O is in guild H; N is in no guild. Every character has its own bag.
    /// </summary>
    [Collection(OWSTests.Grpc.SessionAuthEnvironmentCollection.Name)]
    public class GuildStorageTransferTests : IClassFixture<PostgresTestDatabases>, IDisposable
    {
        private const string Potion = "Item.Potion";

        private readonly PostgresTestDatabases _databases;
        private readonly string _previous = Environment.GetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable);

        public GuildStorageTransferTests(PostgresTestDatabases databases)
        {
            _databases = databases;
            Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, "true");
        }

        public void Dispose() => Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, _previous);

        [PostgresFact]
        public async Task NegativeWithdraw_IsRejected_AndStorageStaysAtTen()
        {
            var world = await World.Create(_databases);
            await world.Store(Potion, 10);

            var ex = await Assert.ThrowsAsync<RpcException>(() => world.Send("A", deposit: false, Potion, -5));
            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

            // The repository refuses it too, whoever calls it.
            GuildStorageTransferResult direct = await world.Repository.Transfer(world.Customer, "A", world.G, false,
                new[] { new GuildStorageTransferItem { ItemIDTag = Potion, Quantity = -5, CustomData = "" } });
            Assert.False(direct.Success);
            Assert.Equal("bad_request", direct.ReasonCode);

            Assert.Equal(10, await world.Stored(Potion));
            Assert.Equal(0, await world.InBag("A", Potion));
        }

        [PostgresTheory]
        [InlineData("O", true)]
        [InlineData("O", false)]
        [InlineData("N", true)]
        [InlineData("N", false)]
        public async Task CharacterOutsideTheGuild_IsPermissionDenied_AndNothingMoves(string outsider, bool deposit)
        {
            var world = await World.Create(_databases);
            await world.Store(Potion, 10);
            await world.PutInBag(outsider, Potion, 7, slot: 0);

            var ex = await Assert.ThrowsAsync<RpcException>(() => world.Send(outsider, deposit, Potion, 5));

            Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
            Assert.Equal(10, await world.Stored(Potion));
            Assert.Equal(7, await world.InBag(outsider, Potion));
        }

        [PostgresFact]
        public async Task DepositOfItemsTheCharacterDoesNotHold_IsRejected_AndNothingMoves()
        {
            var world = await World.Create(_databases);
            await world.PutInBag("A", Potion, 3, slot: 0);

            var notOwned = await Assert.ThrowsAsync<RpcException>(() => world.Send("A", deposit: true, "Item.Sword", 1));
            var tooMany = await Assert.ThrowsAsync<RpcException>(() => world.Send("A", deposit: true, Potion, 5));

            Assert.Equal(StatusCode.FailedPrecondition, notOwned.StatusCode);
            Assert.Equal(StatusCode.FailedPrecondition, tooMany.StatusCode);
            Assert.Equal(0, await world.Stored("Item.Sword"));
            Assert.Equal(0, await world.Stored(Potion));
            Assert.Equal(3, await world.InBag("A", Potion));
        }

        [PostgresFact]
        public async Task WithdrawBeyondTheStoredBalance_IsRejected_AndNothingMoves()
        {
            var world = await World.Create(_databases);
            await world.Store(Potion, 10);

            var ex = await Assert.ThrowsAsync<RpcException>(() => world.Send("A", deposit: false, Potion, 11));

            Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
            Assert.Equal(10, await world.Stored(Potion));
            Assert.Equal(0, await world.InBag("A", Potion));
        }

        [PostgresFact]
        public async Task WithdrawUpdate_ReachesOnlyTheGuildsRealMembers_EvenWhenRequestListsAnOutsider()
        {
            var world = await World.Create(_databases);
            await world.Store(Potion, 10);
            using var a = Register(world.Customer, "A");
            using var b = Register(world.Customer, "B");
            using var o = Register(world.Customer, "O");
            using var n = Register(world.Customer, "N");

            await world.Send("A", deposit: false, Potion, 4, claimedMembers: new[] { "O", "N" });

            Assert.Empty(o.Written);
            Assert.Empty(n.Written);
            foreach (var member in new[] { a, b })
            {
                GuildToSend update = Assert.Single(member.Written);
                Assert.Equal(6, Assert.Single(update.GuildBank.GuildCurrentStoredItems).GuildItemQuantity);
                Assert.Equal(new[] { "A", "B" }, update.GuildMembers.Select(m => m.CharName));
            }

            Assert.Equal(6, await world.Stored(Potion));
            Assert.Equal(4, await world.InBag("A", Potion));
        }

        [PostgresFact]
        public async Task DepositAndWithdraw_MoveItemsBetweenBagAndPersistedStorage()
        {
            var world = await World.Create(_databases);
            await world.PutInBag("A", Potion, 3, slot: 0);
            await world.PutInBag("A", Potion, 4, slot: 2);
            await world.PutInBag("A", "Item.Herb", 1, slot: 1);
            long revisionBefore = await world.Revision("A");

            await world.Send("A", deposit: true, Potion, 5);

            // Debited in slot order: slot 0 emptied (row gone), slot 2 left with 2.
            Assert.Equal(new[] { (1, "Item.Herb", 1), (2, Potion, 2) }, await world.Bag("A"));
            Assert.Equal(5, await world.Stored(Potion));
            Assert.True(await world.Revision("A") > revisionBefore);

            // Storage is in the database now, so a fresh repository (a restarted service) still sees it.
            var reloaded = await new GuildStorageRepository(world.Options).GetStoredItems(world.Customer, world.G);
            Assert.Equal(5, Assert.Single(reloaded).Quantity);

            // Another member withdraws it into the lowest free slot of their own bag.
            await world.PutInBag("B", "Item.Herb", 1, slot: 0);
            await world.Send("B", deposit: false, Potion, 5);

            Assert.Equal(new[] { (0, "Item.Herb", 1), (1, Potion, 5) }, await world.Bag("B"));
            Assert.Equal(0, await world.Stored(Potion));
            Assert.Empty(await world.Repository.GetStoredItems(world.Customer, world.G));
        }

        /// <summary>
        /// The zone server still holds the pre-transfer bag and saves it afterwards, by each bag-saving path, revision-less
        /// and with the revision it last saw. With RequireExpectedRevision on (the only setting OWSGuild opens storage
        /// under) every such save is refused, so a deposit is not re-minted and a withdrawal is not erased.
        /// </summary>
        [PostgresTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task PreTransferBagSnapshot_CannotOverwriteTheTransfer(bool deposit)
        {
            var world = await World.Create(_databases);
            if (deposit)
            {
                await world.PutInBag("A", Potion, 7, slot: 0);
            }
            else
            {
                await world.Store(Potion, 10);
            }
            long before = await world.Revision("A");
            var snapshot = (await world.Bag("A"))
                .Select(s => new UpdateCharacterInventory { InSlotNumber = s.Item1, ItemIDTag = s.Item2, Quantity = s.Item3, CustomData = "" })
                .ToList();

            await world.Send("A", deposit, Potion, deposit ? 5 : 4);
            int bagAfter = deposit ? 2 : 4;
            int storedAfter = deposit ? 5 : 6;
            Assert.Equal(bagAfter, await world.InBag("A", Potion));
            Assert.Equal(storedAfter, await world.Stored(Potion));

            var required = new EconomyOptions { RequireExpectedRevision = true };
            foreach (long? sentRevision in new long?[] { null, before })
            {
                UpdateCharacterInventoryResponse inventorySave = await world.InventorySave("A", snapshot, sentRevision, required).Handle();
                UpdateCharacterDataResponse dataSave = await world.DataSave("A", snapshot, sentRevision, required).Handle();

                string expected = sentRevision == null ? "revision_required" : "stale_revision";
                Assert.Equal(expected, inventorySave.ReasonCode);
                Assert.Equal(expected, dataSave.ReasonCode);
                Assert.Equal(bagAfter, await world.InBag("A", Potion));
                Assert.Equal(storedAfter, await world.Stored(Potion));
            }

            // A zone server that resyncs to the bumped revision saves normally again.
            long current = await world.Revision("A");
            Assert.True(current > before);
            var resynced = (await world.Bag("A"))
                .Select(s => new UpdateCharacterInventory { InSlotNumber = s.Item1, ItemIDTag = s.Item2, Quantity = s.Item3, CustomData = "" })
                .ToList();
            UpdateCharacterInventoryResponse resyncedSave = await world.InventorySave("A", resynced, current, required).Handle();
            Assert.True(resyncedSave.Success, resyncedSave.ReasonCode);
            Assert.Equal(bagAfter, await world.InBag("A", Potion));
        }

        [PostgresFact]
        public async Task WithdrawIntoAFullBag_IsRejected_AndNothingMoves()
        {
            var world = await World.Create(_databases);
            await world.SetBagSize("A", 3);
            await world.PutInBag("A", "Item.Herb", 1, slot: 0);
            await world.PutInBag("A", "Item.Herb", 1, slot: 1);
            await world.PutInBag("A", "Item.Herb", 1, slot: 2);
            await world.PutInBag("A", "Item.Sword", 1, slot: 1000); // equipped: not a bag slot, and no room for one
            await world.Store(Potion, 10);
            var bagBefore = await world.Bag("A");

            var ex = await Assert.ThrowsAsync<RpcException>(() => world.Send("A", deposit: false, Potion, 1));

            Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
            Assert.Equal(10, await world.Stored(Potion));
            Assert.Equal(bagBefore, await world.Bag("A"));
        }

        [PostgresFact]
        public async Task WithdrawThatOnlyPartlyFits_MovesNothing()
        {
            var world = await World.Create(_databases);
            await world.SetBagSize("A", 2);
            await world.PutInBag("A", "Item.Herb", 1, slot: 0);
            await world.Store("Item.Elixir", 5);
            await world.Store(Potion, 10);

            GuildToSend message = StorageMessage(world.Customer, world.G, false, "Item.Elixir", 2);
            message.GuildBank.GuildStorageItems.Add(new GuildStorageItem { GuildItemId = Potion, GuildItemQuantity = 3, GuildItemCustomData = "" });
            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                CreateService(world.Repository, "A").SendGuildMsg(message, new CallContext(world.Customer)));

            Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
            Assert.Equal(5, await world.Stored("Item.Elixir"));
            Assert.Equal(10, await world.Stored(Potion));
            Assert.Equal(new[] { (0, "Item.Herb", 1) }, await world.Bag("A"));
        }

        [PostgresFact]
        public async Task DepositNeverTakesEquippedRows()
        {
            var world = await World.Create(_databases);
            await world.PutInBag("A", "Item.Sword", 1, slot: 1000);

            var ex = await Assert.ThrowsAsync<RpcException>(() => world.Send("A", deposit: true, "Item.Sword", 1));

            Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
            Assert.Equal(new[] { (1000, "Item.Sword", 1) }, await world.Bag("A"));
            Assert.Equal(0, await world.Stored("Item.Sword"));
        }

        /// <summary>One customer's characters, guilds and bags in the shared migrated database.</summary>
        private sealed class World
        {
            private readonly string _connectionString;
            private readonly Dictionary<string, int> _inventoryIds = new();

            private World(string connectionString)
            {
                _connectionString = connectionString;
                Options = Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBConnectionString = connectionString });
                Repository = new GuildStorageRepository(Options);
            }

            public Guid Customer { get; } = Guid.NewGuid();
            public Guid G { get; } = Guid.NewGuid();
            public Guid H { get; } = Guid.NewGuid();
            public int GuildIdG { get; private set; }
            public Microsoft.Extensions.Options.IOptions<StorageOptions> Options { get; }
            public GuildStorageRepository Repository { get; }

            public static async Task<World> Create(PostgresTestDatabases databases)
            {
                var world = new World(databases.ConnectionString(SchemaSource.Migrated));
                await using var connection = new NpgsqlConnection(world._connectionString);
                var args = new { world.Customer, world.G, world.H, Suffix = world.Customer.ToString("N") };

                int classId = await connection.QuerySingleAsync<int>(@"
                    INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                    VALUES (@Customer, 'GuildStorageClass', 'TestMap', 0, 0, 0) RETURNING ClassID", args);
                await connection.ExecuteAsync(@"
                    INSERT INTO Characters (CustomerGUID, CharName, ClassID, X, Y, Z)
                    VALUES (@Customer, 'A', @ClassID, 0, 0, 0), (@Customer, 'B', @ClassID, 0, 0, 0),
                           (@Customer, 'O', @ClassID, 0, 0, 0), (@Customer, 'N', @ClassID, 0, 0, 0)",
                    new { world.Customer, ClassID = classId });
                await connection.ExecuteAsync(@"
                    INSERT INTO CharInventory (CustomerGUID, CharacterID, InventoryName, InventorySize)
                    SELECT CustomerGUID, CharacterID, 'Bag', 16 FROM Characters WHERE CustomerGUID = @Customer", args);

                world.GuildIdG = await connection.QuerySingleAsync<int>(@"
                    INSERT INTO Guild (CustomerGUID, GuildGuid, GuildName, GuildDate)
                    VALUES (@Customer, @G, 'G-' || @Suffix, NOW()) RETURNING GuildID", args);
                int guildIdH = await connection.QuerySingleAsync<int>(@"
                    INSERT INTO Guild (CustomerGUID, GuildGuid, GuildName, GuildDate)
                    VALUES (@Customer, @H, 'H-' || @Suffix, NOW()) RETURNING GuildID", args);
                await connection.ExecuteAsync(@"
                    INSERT INTO GuildMember (CustomerGUID, GuildID, CharacterID, GuildRank, GuildJoinedDate)
                    SELECT CustomerGUID, CASE WHEN CharName = 'O' THEN @H ELSE @G END, CharacterID, 1, NOW()
                    FROM Characters WHERE CustomerGUID = @Customer AND CharName <> 'N'",
                    new { world.Customer, G = world.GuildIdG, H = guildIdH });

                foreach (var row in await connection.QueryAsync<(string CharName, int CharInventoryID)>(@"
                    SELECT C.CharName, CI.CharInventoryID FROM Characters C
                    INNER JOIN CharInventory CI ON CI.CustomerGUID = C.CustomerGUID AND CI.CharacterID = C.CharacterID
                    WHERE C.CustomerGUID = @Customer", args))
                {
                    world._inventoryIds[row.CharName] = row.CharInventoryID;
                }

                return world;
            }

            public Task Send(string sessionCharacter, bool deposit, string itemId, int quantity, string[] claimedMembers = null) =>
                CreateService(Repository, sessionCharacter).SendGuildMsg(
                    StorageMessage(Customer, G, deposit, itemId, quantity, claimedMembers ?? Array.Empty<string>()),
                    new CallContext(Customer));

            public async Task Store(string itemId, int quantity)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(@"
                    INSERT INTO GuildStorage (CustomerGUID, GuildID, ItemIDTag, Quantity, CustomData)
                    VALUES (@Customer, @GuildID, @ItemIDTag, @Quantity, '')",
                    new { Customer, GuildID = GuildIdG, ItemIDTag = itemId, Quantity = quantity });
            }

            public async Task PutInBag(string character, string itemId, int quantity, int slot)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(@"
                    INSERT INTO CharInventoryItems (CustomerGUID, CharInventoryID, ItemIDTag, Quantity, InSlotNumber, CustomData)
                    VALUES (@Customer, @CharInventoryID, @ItemIDTag, @Quantity, @Slot, '')",
                    new { Customer, CharInventoryID = _inventoryIds[character], ItemIDTag = itemId, Quantity = quantity, Slot = slot });
            }

            public async Task<int> Stored(string itemId)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return await connection.ExecuteScalarAsync<int>(@"
                    SELECT COALESCE(SUM(Quantity), 0)::INT FROM GuildStorage
                    WHERE CustomerGUID = @Customer AND GuildID = @GuildID AND ItemIDTag = @ItemIDTag",
                    new { Customer, GuildID = GuildIdG, ItemIDTag = itemId });
            }

            public async Task<int> InBag(string character, string itemId) =>
                (await Bag(character)).Where(s => s.Item2 == itemId).Sum(s => s.Item3);

            public async Task<(int, string, int)[]> Bag(string character)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return (await connection.QueryAsync<(int, string, int)>(@"
                    SELECT InSlotNumber, ItemIDTag, Quantity FROM CharInventoryItems
                    WHERE CustomerGUID = @Customer AND CharInventoryID = @CharInventoryID ORDER BY InSlotNumber",
                    new { Customer, CharInventoryID = _inventoryIds[character] })).ToArray();
            }

            public async Task SetBagSize(string character, int size)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(
                    "UPDATE CharInventory SET InventorySize = @Size WHERE CustomerGUID = @Customer AND CharInventoryID = @CharInventoryID",
                    new { Customer, CharInventoryID = _inventoryIds[character], Size = size });
            }

            private CharactersRepository Characters() =>
                new CharactersRepository(Microsoft.Extensions.Options.Options.Create(
                    new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _connectionString }));

            /// <summary>The zone server's api/Characters/UpdateCharacterInventory save.</summary>
            public UpdateCharacterInventoryRequest InventorySave(string character, List<UpdateCharacterInventory> bag, long? expectedRevision,
                EconomyOptions economy)
            {
                var request = new UpdateCharacterInventoryRequest { CharacterName = character, Inventory = bag, ExpectedRevision = expectedRevision };
                request.SetData(Characters(), new HeaderCustomerGUID { CustomerGUID = Customer }, economy);
                return request;
            }

            /// <summary>The zone server's periodic api/Characters/UpdateCharacterData autosave.</summary>
            public UpdateCharacterDataRequest DataSave(string character, List<UpdateCharacterInventory> bag, long? expectedRevision,
                EconomyOptions economy)
            {
                var request = new UpdateCharacterDataRequest
                {
                    CharacterName = character,
                    CharQuests = new List<UpdateCharacterQuest>(),
                    CharInventory = bag,
                    CharStats = new List<UpdateCharacterStats>(),
                    ExpectedRevision = expectedRevision
                };
                request.SetData(Characters(), new HeaderCustomerGUID { CustomerGUID = Customer }, economy);
                return request;
            }

            public async Task<long> Revision(string character)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return await connection.ExecuteScalarAsync<long>(
                    "SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @Customer AND CharName = @CharName",
                    new { Customer, CharName = character });
            }
        }
    }
}
