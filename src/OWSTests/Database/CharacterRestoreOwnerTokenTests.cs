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
    /// ExpectedRevision for the restore's revision bump to refuse. Each owned save re-checks the token under the
    /// character row lock, so one already past the controller's check when the restore commits is refused too.
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
        private const string BackupValue = "Backup";
        private const string LiveValue = "Live";
        private const string LateClass = "LateClass";

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

        // The save has passed the controller's check and is paused there while the restore commits.
        [PostgresTheory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("class")]
        [InlineData("customdata")]
        public async Task StandaloneSave_PastTheControllerCheckWhenRestoreCommits_IsRefusedAndTheRestoredRowKept(string kind)
        {
            Restored restored = await SeedLoggedOutCharacter("Paused" + kind);
            Assert.False(await Owners().IsStaleOwnerWrite(restored.CustomerGUID, restored.Name, restored.Token));

            Assert.Null(await Restore(restored));

            Assert.Equal("stale_owner", await Save(kind, restored, restored.Token));
            await AssertLiveMatchesBackup(restored);
        }

        // Both queue on the character row, the restore first. The save reaches the database before the restore commits.
        [PostgresTheory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("class")]
        [InlineData("customdata")]
        [InlineData("wallet")]
        public async Task Save_WaitingOnTheRowLockWhileRestoreCommits_IsRefusedAndTheRestoredRowKept(string kind)
        {
            Restored restored = await SeedLoggedOutCharacter("Queued" + kind);

            await using var lockConnection = new NpgsqlConnection(LiveConnectionString);
            await lockConnection.OpenAsync();
            await using NpgsqlTransaction lockTx = await lockConnection.BeginTransactionAsync();
            await lockConnection.ExecuteAsync(@"SELECT CharacterID FROM Characters
                WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID FOR UPDATE", restored.Key, lockTx);

            Task<string> restore = Restore(restored);
            await WaitUntilWaitingOnRowLock(1, restore);
            Task<string> save = Save(kind, restored, restored.Token);
            await WaitUntilWaitingOnRowLock(2, save);

            await lockTx.RollbackAsync();

            Assert.Null(await restore);
            Assert.Equal("stale_owner", await save);
            await AssertLiveMatchesBackup(restored);
        }

        [PostgresTheory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("class")]
        [InlineData("customdata")]
        public async Task StandaloneSave_FromTheCurrentOwner_Lands(string kind)
        {
            Restored character = await SeedLoggedOutCharacter("Current" + kind);
            string before = await StoredValue(kind, character);

            Assert.Null(await Save(kind, character, character.Token));

            Assert.NotEqual(before, await StoredValue(kind, character));
        }

        [PostgresTheory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("class")]
        [InlineData("customdata")]
        public async Task StandaloneSave_FromTheReplacedOwnerDuringAHandover_Lands(string kind)
        {
            Restored character = await SeedLoggedOutCharacter("Replaced" + kind);
            ClaimCharacterResult replaced = await Owners().ClaimByName(character.CustomerGUID, character.Name, MapInstanceID);
            Assert.True((await Owners().ClaimByName(character.CustomerGUID, character.Name, MapInstanceID + 1)).HandoverSeconds > 0);
            string before = await StoredValue(kind, character);

            Assert.Null(await Save(kind, character, replaced.OwnerToken));

            Assert.NotEqual(before, await StoredValue(kind, character));
        }

        [PostgresTheory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("class")]
        [InlineData("customdata")]
        public async Task StandaloneSave_TokenlessOnAnUnclaimedCharacter_StillLands(string kind)
        {
            Restored character = await SeedLoggedOutCharacter("Unclaimed" + kind);
            await using var live = new NpgsqlConnection(LiveConnectionString);
            await live.ExecuteAsync("DELETE FROM CharacterOwners WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID", character.Key);
            string before = await StoredValue(kind, character);

            Assert.Null(await Save(kind, character, null));

            Assert.NotEqual(before, await StoredValue(kind, character));
        }

        [PostgresTheory]
        [InlineData("stats")]
        [InlineData("quests")]
        public async Task StandaloneSave_WhenAnotherZoneHoldsTheCharacter_IsRefusedStaleZone(string kind)
        {
            Restored character = await SeedLoggedOutCharacter("OtherZone" + kind);
            await using var live = new NpgsqlConnection(LiveConnectionString);
            await live.ExecuteAsync(@"INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID)
                VALUES (@CustomerGUID, @CharacterID, @MapInstanceID)",
                new { character.CustomerGUID, character.CharacterID, MapInstanceID });
            string before = await StoredValue(kind, character);

            string refusal = kind == "stats"
                ? await Characters().UpdateCharacterStats(character.CustomerGUID, character.Name, LateStats, character.Token, MapInstanceID + 1)
                : await Characters().UpdateCharacterQuests(character.CustomerGUID, character.Name, LateQuests, character.Token, MapInstanceID + 1);

            Assert.Equal("stale_zone", refusal);
            Assert.Equal(before, await StoredValue(kind, character));
        }

        // A join reserves the destination's CharOnMapInstance row while the source's stays. The source still owns the
        // character through the spin-up wait, so its saves land until AddCharacterToMapInstanceByCharName hands it over.
        [PostgresTheory]
        [InlineData("stats")]
        [InlineData("quests")]
        public async Task StandaloneSave_FromTheSourceZoneWhileTheDestinationIsReserved_LandsUntilTheHandoff(string kind)
        {
            Restored character = await SeedLoggedOutCharacter("Reserved" + kind);
            int destination = MapInstanceID + 1;
            await using var live = new NpgsqlConnection(LiveConnectionString);
            await live.ExecuteAsync(@"INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID)
                VALUES (@CustomerGUID, @CharacterID, @MapInstanceID), (@CustomerGUID, @CharacterID, @Destination)",
                new { character.CustomerGUID, character.CharacterID, MapInstanceID, Destination = destination });
            string before = await StoredValue(kind, character);

            Assert.Null(await SaveFromZone(kind, character, MapInstanceID));
            string saved = await StoredValue(kind, character);
            Assert.NotEqual(before, saved);

            await Characters().AddCharacterToMapInstanceByCharName(character.CustomerGUID, character.Name, destination);
            await live.ExecuteAsync(@"UPDATE CharStats SET Value = @BackupLevel WHERE CustomerGUID = @CustomerGUID
                AND CharacterID = @CharacterID AND StatIdentifier = 'BaseLevel';
                UPDATE CharQuests SET QuestJournalTagContainer = @BackupValue WHERE CustomerGUID = @CustomerGUID
                AND CharacterID = @CharacterID AND QuestIDTag = 'Quest1'",
                new { character.CustomerGUID, character.CharacterID, BackupLevel, BackupValue });
            string afterHandoff = await StoredValue(kind, character);

            Assert.Equal("stale_zone", await SaveFromZone(kind, character, MapInstanceID));
            Assert.Equal(afterHandoff, await StoredValue(kind, character));
        }

        private Task<string> SaveFromZone(string kind, Restored character, int zoneInstanceID) => kind == "stats"
            ? Characters().UpdateCharacterStats(character.CustomerGUID, character.Name, LateStats, character.Token, zoneInstanceID)
            : Characters().UpdateCharacterQuests(character.CustomerGUID, character.Name, LateQuests, character.Token, zoneInstanceID);

        [PostgresFact]
        public async Task StandaloneSave_ForAnUnknownCharacter_WritesNothing()
        {
            Guid customerGUID = Guid.NewGuid();

            Assert.Equal("character_not_found", await Characters().UpdateCharacterStats(customerGUID, "Nobody", LateStats, null));
            Assert.Equal("character_not_found", await Characters().AddOrUpdateOwnedCustomCharacterData(customerGUID,
                new AddOrUpdateCustomCharacterData { CharacterName = "Nobody", CustomFieldName = "Field1", FieldValue = "Late" }, null));
        }

        // A player's own write through the public API carries no owner token, and is not a late save from a zone.
        [PostgresFact]
        public async Task PublicApiCustomDataWrite_AfterRestore_StillLands()
        {
            Restored restored = await SeedLoggedOutCharacter("PublicWrite");
            Assert.Null(await Restore(restored));

            await Characters().AddOrUpdateCustomCharacterData(restored.CustomerGUID,
                new AddOrUpdateCustomCharacterData { CharacterName = restored.Name, CustomFieldName = "LoginSlotIndex", FieldValue = "2" });

            await using var live = new NpgsqlConnection(LiveConnectionString);
            Assert.Equal("2", await live.QuerySingleAsync<string>(@"SELECT FieldValue FROM CustomCharacterData
                WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID AND CustomFieldName = 'LoginSlotIndex'", restored.Key));
        }

        // What a save arriving after the commit meets, whichever write it is headed for.
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
            Assert.Equal(BackupValue, await StoredValue("quests", restored));
            Assert.Equal(BackupLevel.ToString(), await StoredValue("abilities", restored));
            Assert.Equal(BackupValue, await StoredValue("customdata", restored));
            Assert.Equal(restored.BackupClassID.ToString(), await StoredValue("class", restored));
        }

        private static readonly List<UpdateCharacterStats> LateStats = new() { new() { StatIdentifier = "BaseLevel", Value = 99 } };
        private static readonly List<UpdateCharacterQuest> LateQuests = new()
        {
            new() { QuestIDTag = "Quest1", QuestJournalTagContainer = "Late", CustomData = "Late" }
        };
        private const string LateAbilities = @"[{""abilityIdTag"":""Ability1"",""currentAbilityLevel"":99,""actualAbilityLevel"":99,""customData"":""Late""}]";

        // The late write of each kind, on the row the restore put back. Null when it was written, else why not.
        private async Task<string> Save(string kind, Restored restored, Guid? token)
        {
            CharactersRepository characters = Characters();
            switch (kind)
            {
                case "stats":
                    return await characters.UpdateCharacterStats(restored.CustomerGUID, restored.Name, LateStats, token);
                case "quests":
                    return await characters.UpdateCharacterQuests(restored.CustomerGUID, restored.Name, LateQuests, token);
                case "abilities":
                    return await characters.UpdateCharacterAbilities(restored.CustomerGUID, restored.Name, LateAbilities, token);
                case "class":
                    return await characters.UpdateCharacterClass(restored.CustomerGUID, restored.Name, LateClass, token);
                case "customdata":
                    return await characters.AddOrUpdateOwnedCustomCharacterData(restored.CustomerGUID,
                        new AddOrUpdateCustomCharacterData { CharacterName = restored.Name, CustomFieldName = "Field1", FieldValue = "Late" }, token);
                case "wallet":
                    return (await characters.UpdateCharacterCurrency(restored.CustomerGUID, restored.Name, 12345, token)).ReasonCode;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private async Task<string> StoredValue(string kind, Restored restored)
        {
            string sql = kind switch
            {
                "stats" => "SELECT Value::TEXT FROM CharStats WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID AND StatIdentifier = 'BaseLevel'",
                "quests" => "SELECT QuestJournalTagContainer FROM CharQuests WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID AND QuestIDTag = 'Quest1'",
                "abilities" => "SELECT CurrentAbilityLevel::TEXT FROM CharAbilities WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID AND AbilityIDTag = 'Ability1'",
                "class" => "SELECT ClassID::TEXT FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                "customdata" => "SELECT FieldValue FROM CustomCharacterData WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID AND CustomFieldName = 'Field1'",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            await using var live = new NpgsqlConnection(LiveConnectionString);
            return await live.QuerySingleAsync<string>(sql, restored.Key);
        }

        // Each test class has its own databases and runs its tests one at a time, so only these queries wait here.
        private async Task WaitUntilWaitingOnRowLock(int waiters, Task latest)
        {
            await using var connection = new NpgsqlConnection(LiveConnectionString);
            for (int attempt = 0; attempt < 200; attempt++)
            {
                Assert.False(latest.IsCompleted, "The write finished while the character row lock was held.");
                int waiting = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*)::INT FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%FOR UPDATE%'");
                if (waiting >= waiters)
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException($"{waiters} writes never waited on the character row lock.");
        }

        private string LiveConnectionString => _databases.ConnectionString(SchemaSource.Migrated);

        private StorageOptions Storage() => new() { OWSDBBackend = "postgres", OWSDBConnectionString = LiveConnectionString };

        private CharactersRepository Characters() => new(Microsoft.Extensions.Options.Options.Create(Storage()));

        private CharacterOwnersRepository Owners() => new(Microsoft.Extensions.Options.Options.Create(Storage()));

        private static List<UpdateCharacterInventory> Bag(string item) =>
            new() { new UpdateCharacterInventory { ItemIDTag = item, Quantity = 1, InSlotNumber = 0 } };

        private sealed record Restored(Guid CustomerGUID, int CharacterID, string Name, Guid Token, string StagingConnectionString, int BackupClassID)
        {
            public object Key => new { CustomerGUID, CharacterID };
        }

        private Task<string> Restore(Restored restored) =>
            new CharacterRestoreRepository(LiveConnectionString, restored.StagingConnectionString).Restore(restored.CustomerGUID, restored.Name, Dump, "admin");

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
            int characterID = await SeedCharacter(live, customerGUID, name, null, LiveGold, LiveLevel, "LiveMap", LiveItem, LiveValue);
            await SeedCharacter(staging, customerGUID, name, characterID, BackupGold, BackupLevel, "BackupMap", BackupItem, BackupValue);
            await staging.ExecuteAsync("CREATE TABLE staging_meta (dump TEXT NOT NULL); INSERT INTO staging_meta VALUES (@Dump)", new { Dump });
            await live.ExecuteAsync(@"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                VALUES (@CustomerGUID, @LateClass, 'TestMap', 0, 0, 0)", new { CustomerGUID = customerGUID, LateClass });
            int backupClassID = await staging.QuerySingleAsync<int>(
                "SELECT ClassID FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { CustomerGUID = customerGUID, CharacterID = characterID });

            ClaimCharacterResult claim = await Owners().ClaimByName(customerGUID, name, MapInstanceID);
            Assert.True(claim.Success, claim.ErrorMessage);
            await Characters().PlayerLogout(customerGUID, name);
            await Owners().EndOwnerSession(customerGUID, name, claim.OwnerToken);

            return new Restored(customerGUID, characterID, name, claim.OwnerToken, stagingConnectionString, backupClassID);
        }

        private static async Task<int> SeedCharacter(NpgsqlConnection connection, Guid customerGUID, string name, int? characterID,
            int gold, int level, string map, string item, string value)
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
            var rows = new { CustomerGUID = customerGUID, CharacterID = id, Level = level, Value = value };
            await connection.ExecuteAsync(@"INSERT INTO CharQuests (CustomerGUID, CharacterID, QuestIDTag, QuestJournalTagContainer, CustomData)
                VALUES (@CustomerGUID, @CharacterID, 'Quest1', @Value, @Value)", rows);
            await connection.ExecuteAsync(@"INSERT INTO CharAbilities (CustomerGUID, CharacterID, AbilityIDTag, CurrentAbilityLevel, ActualAbilityLevel, CustomData)
                VALUES (@CustomerGUID, @CharacterID, 'Ability1', @Level, @Level, @Value)", rows);
            await connection.ExecuteAsync(@"INSERT INTO CustomCharacterData (CustomerGUID, CharacterID, CustomFieldName, FieldValue)
                VALUES (@CustomerGUID, @CharacterID, 'Field1', @Value)", rows);
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
