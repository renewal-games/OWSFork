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
    /// A restore revokes the owner token, so a save from the session that last owned the character cannot land on the
    /// restored state. Logging out leaves that token valid, and the zone server's bag and wallet saves send no
    /// ExpectedRevision for the restore's revision bump to refuse.
    /// </summary>
    public class CharacterRestoreOwnerTokenTests : IClassFixture<PostgresTestDatabases>
    {
        private const int MapInstanceID = 1;
        private const string Dump = "openworldserver-20261104T120000Z.dump";
        private const int BackupGold = 100;
        private const int BackupLevel = 5;
        private const int LiveGold = 999;
        private const int LiveLevel = 50;
        private const string BackupItem = "BackupSword";
        private const string LiveItem = "LiveAxe";

        private readonly PostgresTestDatabases _databases;

        public CharacterRestoreOwnerTokenTests(PostgresTestDatabases databases) => _databases = databases;

        [PostgresFact]
        public async Task LateSaves_WithTheLoggedOutOwnersToken_AreRefusedAndTheRestoredStateKept()
        {
            Restored restored = await SeedLoggedOutCharacter("LateSaver");
            Guid oldToken = restored.Token;

            // The controller's check passes this token until the restore commits.
            Assert.False(await Owners().IsStaleOwnerWrite(restored.CustomerGUID, restored.Name, oldToken));
            long revisionBefore = await Revision(restored);

            Assert.Null(await Restore(restored));

            Assert.Equal(revisionBefore + 1, await Revision(restored));
            await AssertOwnerWritesRefused(restored, oldToken);
            await AssertLiveMatchesBackup(restored);
        }

        // These saves have no re-check of their own, so the controller's check is the one that has to refuse them.
        [PostgresFact]
        public async Task ControllerCheck_RefusesTheOldToken_AfterRestore()
        {
            Restored restored = await SeedLoggedOutCharacter("Upserter");

            Assert.Null(await Restore(restored));

            Assert.True(await Owners().IsStaleOwnerWrite(restored.CustomerGUID, restored.Name, restored.Token));
            Assert.True(await Owners().IsStaleOwnerWrite(restored.CustomerGUID, restored.Name, restored.Token, allowPreviousOwner: false));
            Assert.True(await Owners().IsStaleOwnerWrite(restored.CustomerGUID, restored.CharacterID, restored.Token));
        }

        [PostgresFact]
        public async Task LateSaves_FromAHandoverStillRunning_AreRefused()
        {
            Restored restored = await SeedLoggedOutCharacter("Handover");
            ClaimCharacterResult replaced = await Owners().ClaimByName(restored.CustomerGUID, restored.Name, MapInstanceID);
            ClaimCharacterResult current = await Owners().ClaimByName(restored.CustomerGUID, restored.Name, MapInstanceID + 1);
            Assert.True(current.HandoverSeconds > 0);
            Assert.False(await Owners().IsStaleOwnerWrite(restored.CustomerGUID, restored.Name, replaced.OwnerToken));

            Assert.Null(await Restore(restored));

            await AssertOwnerWritesRefused(restored, replaced.OwnerToken);
            await AssertOwnerWritesRefused(restored, current.OwnerToken);
            await AssertLiveMatchesBackup(restored);
        }

        [PostgresFact]
        public async Task NextLogin_AfterRestore_ClaimsAtOnceAndItsSavesLand()
        {
            Restored restored = await SeedLoggedOutCharacter("NextLogin");
            Assert.Null(await Restore(restored));

            ClaimCharacterResult claim = await Owners().ClaimByName(restored.CustomerGUID, restored.Name, MapInstanceID);

            Assert.True(claim.Success, claim.ErrorMessage);
            Assert.NotEqual(restored.Token, claim.OwnerToken);
            Assert.Equal(0, claim.HandoverSeconds);

            UpdateCharacterInventoryResponse bag = await Characters().UpdateCharacterInventory(restored.CustomerGUID, restored.Name,
                Bag("NewLoot"), claim.OwnerToken);
            UpdateCharacterCurrencyResponse wallet = await Characters().UpdateCharacterCurrency(restored.CustomerGUID, restored.Name, 7, claim.OwnerToken);

            Assert.True(bag.Success, bag.ReasonCode);
            Assert.True(wallet.Success, wallet.ReasonCode);
            await using var live = new NpgsqlConnection(LiveConnectionString);
            Assert.Equal(new[] { "NewLoot" }, await BagItems(live, restored));
            Assert.Equal(7, await GoldOf(live, restored));
        }

        // A copy still running somewhere is told to leave by the next CheckOwners, and no longer counts as confirmed.
        [PostgresFact]
        public async Task CheckOwners_ReportsTheOldToken_AfterRestore()
        {
            Restored restored = await SeedLoggedOutCharacter("StillRunning");
            Assert.Null(await Restore(restored));

            List<StaleCharacterOwner> stale = await Owners().GetStaleOwners(restored.CustomerGUID,
                new[] { new CharacterOwnerCheck { CharacterName = restored.Name, OwnerToken = restored.Token } });

            Assert.Equal(restored.Token, Assert.Single(stale).OwnerToken);
            await using var live = new NpgsqlConnection(LiveConnectionString);
            Assert.Null(await live.QuerySingleAsync<DateTime?>(
                "SELECT LastConfirmedAt FROM CharacterOwners WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", restored.Key));
        }

        [PostgresFact]
        public async Task RefusedRestore_LeavesTheOwnerTokenValid()
        {
            Restored restored = await SeedLoggedOutCharacter("StillOnline");
            await using var live = new NpgsqlConnection(LiveConnectionString);
            await live.ExecuteAsync(@"INSERT INTO LivePositions (CustomerGUID, CharacterName, MapInstanceID, X, Y, Z, UpdatedAt)
                VALUES (@CustomerGUID, @Name, @MapInstanceID, 0, 0, 0, NOW())",
                new { restored.CustomerGUID, restored.Name, MapInstanceID });

            Assert.Equal("online", await Restore(restored));

            Assert.False(await Owners().IsStaleOwnerWrite(restored.CustomerGUID, restored.Name, restored.Token));
            UpdateCharacterCurrencyResponse wallet = await Characters().UpdateCharacterCurrency(restored.CustomerGUID, restored.Name, 7, restored.Token);
            Assert.True(wallet.Success, wallet.ReasonCode);
        }

        private async Task AssertOwnerWritesRefused(Restored restored, Guid token)
        {
            UpdateCharacterInventoryResponse bag = await Characters().UpdateCharacterInventory(restored.CustomerGUID, restored.Name,
                Bag("LateLoot"), token);
            Assert.False(bag.Success);
            Assert.Equal("stale_owner", bag.ReasonCode);

            UpdateCharacterCurrencyResponse wallet = await Characters().UpdateCharacterCurrency(restored.CustomerGUID, restored.Name, 12345, token);
            Assert.False(wallet.Success);
            Assert.Equal("stale_owner", wallet.ReasonCode);

            UpdateCharacterDataResponse autosave = await Characters().UpdateCharacterData(restored.CustomerGUID, restored.Name,
                new List<UpdateCharacterQuest>(),
                Bag("LateLoot"),
                new List<UpdateCharacterStats> { new() { StatIdentifier = "BaseLevel", Value = 99 } },
                token);
            Assert.False(autosave.Success);
            Assert.Equal("stale_owner", autosave.ReasonCode);

            Assert.False(await Characters().UpdatePosition(restored.CustomerGUID, restored.Name, "LateMap", 1, 2, 3, 0, 0, 0, token));
        }

        private async Task AssertLiveMatchesBackup(Restored restored)
        {
            await using var live = new NpgsqlConnection(LiveConnectionString);
            Assert.Equal(new[] { BackupItem }, await BagItems(live, restored));
            Assert.Equal(BackupGold, await GoldOf(live, restored));
            Assert.Equal(BackupLevel, await live.QuerySingleAsync<int>(
                "SELECT Value FROM CharStats WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID AND StatIdentifier = 'BaseLevel'",
                restored.Key));
            Assert.Equal("BackupMap", await live.QuerySingleAsync<string>(
                "SELECT MapName FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", restored.Key));
        }

        private string LiveConnectionString => _databases.ConnectionString(SchemaSource.Migrated);

        private StorageOptions Storage() => new() { OWSDBBackend = "postgres", OWSDBConnectionString = LiveConnectionString };

        private CharactersRepository Characters() => new(Microsoft.Extensions.Options.Options.Create(Storage()));

        private CharacterOwnersRepository Owners() => new(Microsoft.Extensions.Options.Options.Create(Storage()));

        private static List<UpdateCharacterInventory> Bag(string item) =>
            new() { new UpdateCharacterInventory { ItemIDTag = item, Quantity = 1, InSlotNumber = 0 } };

        private sealed record Restored(Guid CustomerGUID, int CharacterID, string Name, Guid Token, string StagingConnectionString)
        {
            public object Key => new { CustomerGUID, CharacterID };
        }

        private Task<string> Restore(Restored restored) =>
            new CharacterRestoreRepository(LiveConnectionString, restored.StagingConnectionString).Restore(restored.CustomerGUID, restored.Name, Dump);

        private async Task<long> Revision(Restored restored)
        {
            await using var live = new NpgsqlConnection(LiveConnectionString);
            return await live.QuerySingleAsync<long>(
                "SELECT EconomyRevision FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", restored.Key);
        }

        private static Task<int> GoldOf(NpgsqlConnection connection, Restored restored) =>
            connection.QuerySingleAsync<int>(
                "SELECT Gold FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", restored.Key);

        private static async Task<string[]> BagItems(NpgsqlConnection connection, Restored restored) =>
            (await connection.QueryAsync<string>(@"SELECT CII.ItemIDTag FROM CharInventoryItems CII
                JOIN CharInventory CI ON CI.CustomerGUID = CII.CustomerGUID AND CI.CharInventoryID = CII.CharInventoryID
                WHERE CI.CustomerGUID = @CustomerGUID AND CI.CharacterID = @CharacterID ORDER BY CII.ItemIDTag", restored.Key)).ToArray();

        // A character with a backup in staging, that claimed, played on past the backup, and logged out: no CharOnMapInstance
        // row, LastConfirmedAt cleared, and the token it played with still the current owner's.
        private async Task<Restored> SeedLoggedOutCharacter(string name)
        {
            string stagingConnectionString = await _databases.CreateDatabaseAsync(SchemaSource.Migrated);
            Guid customerGUID = Guid.NewGuid();

            await using var live = new NpgsqlConnection(LiveConnectionString);
            await using var staging = new NpgsqlConnection(stagingConnectionString);
            int characterID = await SeedCharacter(live, customerGUID, name, null, LiveGold, LiveLevel, "LiveMap", LiveItem);
            await SeedCharacter(staging, customerGUID, name, characterID, BackupGold, BackupLevel, "BackupMap", BackupItem);
            await staging.ExecuteAsync("CREATE TABLE staging_meta (dump TEXT NOT NULL); INSERT INTO staging_meta VALUES (@Dump)", new { Dump });

            ClaimCharacterResult claim = await Owners().ClaimByName(customerGUID, name, MapInstanceID);
            Assert.True(claim.Success, claim.ErrorMessage);
            await Characters().PlayerLogout(customerGUID, name);
            await Owners().EndOwnerSession(customerGUID, name, claim.OwnerToken);

            return new Restored(customerGUID, characterID, name, claim.OwnerToken, stagingConnectionString);
        }

        private static async Task<int> SeedCharacter(NpgsqlConnection connection, Guid customerGUID, string name, int? characterID,
            int gold, int level, string map, string item)
        {
            Guid userGUID = Guid.NewGuid();
            await connection.ExecuteAsync(@"INSERT INTO Users
                (UserGUID, CustomerGUID, FirstName, LastName, Email, PasswordHash, Role)
                VALUES (@UserGUID, @CustomerGUID, 'Test', 'Owner', @Email, 'x', 'Player')",
                new { UserGUID = userGUID, CustomerGUID = customerGUID, Email = $"{userGUID:N}@example.com" });
            int classID = await connection.ExecuteScalarAsync<int>(@"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                VALUES (@CustomerGUID, 'TestClass', 'TestMap', 0, 0, 0) RETURNING ClassID", new { CustomerGUID = customerGUID });
            int id = await connection.ExecuteScalarAsync<int>(@"INSERT INTO Characters
                (CustomerGUID, CharacterID, UserGUID, CharName, MapName, X, Y, Z, ClassID, Gold)
                VALUES (@CustomerGUID, COALESCE(@CharacterID, nextval('characters_characterid_seq')), @UserGUID, @Name, @Map, 0, 0, 0, @ClassID, @Gold)
                RETURNING CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID, UserGUID = userGUID, Name = name, Map = map, ClassID = classID, Gold = gold });
            await connection.ExecuteAsync(@"INSERT INTO CharStats (CustomerGUID, CharacterID, StatIdentifier, Value)
                VALUES (@CustomerGUID, @CharacterID, 'BaseLevel', @Level)",
                new { CustomerGUID = customerGUID, CharacterID = id, Level = level });
            int inventoryID = await connection.ExecuteScalarAsync<int>(@"INSERT INTO CharInventory
                (CustomerGUID, CharacterID, InventoryName, InventorySize) VALUES (@CustomerGUID, @CharacterID, 'Bag', 16)
                RETURNING CharInventoryID", new { CustomerGUID = customerGUID, CharacterID = id });
            await connection.ExecuteAsync(@"INSERT INTO CharInventoryItems
                (CustomerGUID, CharInventoryID, ItemIDTag, Quantity, InSlotNumber)
                VALUES (@CustomerGUID, @CharInventoryID, @Item, 1, 0)",
                new { CustomerGUID = customerGUID, CharInventoryID = inventoryID, Item = item });
            return id;
        }
    }
}
