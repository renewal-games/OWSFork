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
using PartyServiceApp.Protos;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// PurgeStressBots against party writes running at the same time. The purge locks the Party row of every party a bot
    /// is in before it reads that party's membership, the lock every party writer takes first, and decides which parties
    /// to delete only after the bots' memberships are gone. Each test fixes the overlap with a row lock held by a gate
    /// connection, on independent connections at READ COMMITTED, so the result does not depend on timing. The purge
    /// exists only in the migrated schema.
    /// </summary>
    public class StressBotPurgePartyTests : IClassFixture<PostgresTestDatabases>
    {
        private const string RealEmail = "purge-party-real@example.com";

        private readonly PostgresTestDatabases _databases;

        // The purge and repository connections carry this name, so the lock-wait checks count only them.
        private readonly string _applicationName = "purge-party-" + Guid.NewGuid().ToString("N");

        // The shared migrated database unless a test gives itself one.
        private string _connectionString;

        public StressBotPurgePartyTests(PostgresTestDatabases databases) => _databases = databases;

        private string Db => _connectionString ??= _databases.ConnectionString(SchemaSource.Migrated);

        // A bot leads a party with no one else in it. The purge has read the bots' parties and is held at a later step
        // when the bot leader adds a real character. Before the fix the add committed, the purge made the new member
        // leader, removed the bot and then deleted the party from its earlier reading, leaving the real character in a
        // party that no longer exists. With it the add waits on the party lock and, once the purge commits, finds the
        // party gone.
        [PostgresFact]
        public async Task AddDuringPurge_IsRefused_AndLeavesNoMemberWithoutAParty()
        {
            Guid customer = Guid.NewGuid();
            await RegisterBot(Users(), customer, 1);
            int classId = await SeedClass(customer);
            await SeedCharacter(customer, BotEmail(1), "SSBot1", classId);
            await SeedCharacter(customer, null, "Real", classId);
            Guid partyGuid = await CreateParty(customer, "SSBot1");
            await PutOnMap(customer, "SSBot1");
            await AssertReadCommitted();

            await using var gate = new NpgsqlConnection(Db);
            await gate.OpenAsync();
            await using NpgsqlTransaction gateTx = await gate.BeginTransactionAsync();
            await LockMapRow(gate, gateTx, customer, "SSBot1");

            // Parked at its CharOnMapInstance step: past the point where it reads which parties the bots are in, before
            // the leader handover and the party delete.
            Task<List<(string What, long Removed)>> purge = Purge();
            await WaitForLockWaiters(1, purge);

            Task<PartyToSend> add = Characters().CreatePartyOrAddMember(customer, AddRequest(partyGuid, "SSBot1", "Real"));
            await WaitForAddToFinishOrWait(add);
            await gateTx.CommitAsync();
            await purge;
            Exception addFailure = (await Outcomes(add))[0];

            Assert.Equal(0, await Scalar<int>(@"SELECT COUNT(*)::INT FROM PartyMember PM
                WHERE PM.CustomerGUID = @Customer
                  AND NOT EXISTS (SELECT 1 FROM Party P WHERE P.CustomerGUID = PM.CustomerGUID AND P.PartyID = PM.PartyID)",
                new { Customer = customer }));
            Assert.Equal(0, await Scalar<int>("SELECT COUNT(*)::INT FROM PartyMember WHERE CustomerGUID = @Customer", new { Customer = customer }));
            Assert.Equal(0, await Scalar<int>("SELECT COUNT(*)::INT FROM Party WHERE CustomerGUID = @Customer", new { Customer = customer }));
            Assert.Equal(1, await Scalar<int>("SELECT COUNT(*)::INT FROM Characters WHERE CustomerGUID = @Customer", new { Customer = customer }));
            InvalidOperationException rejected = Assert.IsType<InvalidOperationException>(addFailure);
            Assert.Equal("Party was not found.", rejected.Message);
        }

        // A bot leads a party with two real members. The first real member's character deletion has removed its
        // membership but not committed when the purge runs. Before the fix the purge chose that member as the new
        // leader from what it could see, then skipped its row once the deletion committed, so the remaining member was
        // left in a party with no leader. With it the purge waits on the party lock the deletion holds, then hands the
        // lead to the member who is still there.
        [PostgresFact]
        public async Task MemberDeletionDuringPurge_LeavesTheSurvivingPartyALeader()
        {
            // A fresh migrated schema lacks some tables RemoveCharacter deletes from (CreateCharactersTableModificationScript
            // drops them and nothing recreates them), so give this test its own database with empty stand-ins.
            _connectionString = await _databases.CreateDatabaseAsync(SchemaSource.Migrated);
            await Execute(@"
                CREATE TABLE IF NOT EXISTS CharAbilityBars (CustomerGUID UUID, CharAbilityBarID INT, CharacterID INT);
                CREATE TABLE IF NOT EXISTS CharAbilityBarAbilities (CustomerGUID UUID, CharAbilityBarID INT);
                CREATE TABLE IF NOT EXISTS CharHasAbilities (CustomerGUID UUID, CharacterID INT);
                CREATE TABLE IF NOT EXISTS CharAbilities (CustomerGUID UUID, CharacterID INT);
                CREATE TABLE IF NOT EXISTS ChatGroupUsers (CustomerGUID UUID, CharacterID INT);
                CREATE TABLE IF NOT EXISTS PlayerGroupCharacters (CustomerGUID UUID, CharacterID INT);
                CREATE TABLE IF NOT EXISTS PlayerGroupMember (CustomerGUID UUID, CharacterID INT);
                CREATE TABLE IF NOT EXISTS CharOnMapInstance (CustomerGUID UUID, CharacterID INT, MapInstanceID INT);
                CREATE TABLE IF NOT EXISTS CustomCharacterData (CustomerGUID UUID, CharacterID INT);
                CREATE TABLE IF NOT EXISTS CharQuests (CustomerGUID UUID, CharacterID INT);", null);

            Guid customer = Guid.NewGuid();
            UsersRepository users = Users();
            Assert.True((await users.RegisterUser(customer, RealEmail, "pw", "Party", "User")).Success);
            PlayerLoginAndCreateSession login = await users.LoginAndCreateSession(customer, RealEmail, "pw");
            Assert.True(login.Authenticated, login.ErrorMessage);
            await RegisterBot(users, customer, 1);
            int classId = await SeedClass(customer);
            await SeedCharacter(customer, BotEmail(1), "SSBot1", classId);
            // RealA has the lower CharacterID, so it is the one the purge would hand the lead to.
            await SeedCharacter(customer, RealEmail, "RealA", classId);
            await SeedCharacter(customer, RealEmail, "RealB", classId);
            Guid partyGuid = await CreateParty(customer, "SSBot1", "RealA", "RealB");
            await PutOnMap(customer, "RealA");
            await AssertReadCommitted();

            await using var gate = new NpgsqlConnection(Db);
            await gate.OpenAsync();
            await using NpgsqlTransaction gateTx = await gate.BeginTransactionAsync();
            await LockMapRow(gate, gateTx, customer, "RealA");

            // Parked at its CharOnMapInstance delete, after it has removed RealA's membership.
            Task<SuccessAndErrorMessage> remove = users.RemoveCharacter(customer, login.UserSessionGuid.Value, "RealA");
            await WaitForLockWaiters(1, remove);

            Task<List<(string What, long Removed)>> purge = Purge();
            await WaitForLockWaiters(2, remove, purge);
            await gateTx.CommitAsync();
            SuccessAndErrorMessage removed = await remove;
            Assert.True(removed.Success, removed.ErrorMessage);
            await purge;

            Assert.Equal(1, await Scalar<int>("SELECT COUNT(*)::INT FROM Party WHERE CustomerGUID = @Customer AND PartyGuid = @PartyGuid",
                new { Customer = customer, PartyGuid = partyGuid }));
            string[] members = (await Query<string>(@"SELECT C.CharName || ':' || PM.PartyLeader::TEXT FROM PartyMember PM
                JOIN Party P ON P.CustomerGUID = PM.CustomerGUID AND P.PartyID = PM.PartyID
                JOIN Characters C ON C.CustomerGUID = PM.CustomerGUID AND C.CharacterID = PM.CharacterID
                WHERE PM.CustomerGUID = @Customer AND P.PartyGuid = @PartyGuid",
                new { Customer = customer, PartyGuid = partyGuid })).ToArray();
            Assert.Equal(new[] { "RealB:true" }, members);
        }

        private string RepositoryConnectionString => new NpgsqlConnectionStringBuilder(Db)
        {
            ApplicationName = _applicationName
        }.ConnectionString;

        private CharactersRepository Characters() =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = RepositoryConnectionString }));

        private UsersRepository Users() =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = RepositoryConnectionString }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UsersRepository>.Instance);

        private async Task<List<(string What, long Removed)>> Purge()
        {
            await using var connection = new NpgsqlConnection(RepositoryConnectionString);
            return (await connection.QueryAsync<(string What, long Removed)>("SELECT What, Removed FROM PurgeStressBots()")).ToList();
        }

        private static string BotEmail(int n) => $"ssbot{n}@stressbots.invalid";

        private static async Task RegisterBot(UsersRepository users, Guid customer, int n) =>
            Assert.True((await users.RegisterUser(customer, BotEmail(n), "pw", "Stress", "Bot")).Success);

        private static PartyToSend AddRequest(Guid partyGuid, string leader, string target) => new()
        {
            PartyAction = PartyAction.MessageTypeAdd,
            PartyInfo = new PartyInfo { PartyGuid = partyGuid.ToString() },
            PartyMembers =
            {
                new PartyMemberInfo { CharName = leader, PartyLeader = true },
                new PartyMemberInfo { CharName = target }
            }
        };

        private async Task<Guid> CreateParty(Guid customer, string leader, params string[] members)
        {
            var request = new PartyToSend
            {
                CustomerGuid = customer.ToString(),
                PartyAction = PartyAction.MessageTypeCreate,
                PartyInfo = new PartyInfo()
            };
            request.PartyMembers.Add(new PartyMemberInfo { CharName = leader, PartyLeader = true });
            request.PartyMembers.Add(members.Select(member => new PartyMemberInfo { CharName = member }));

            PartyToSend created = await Characters().CreatePartyOrAddMember(customer, request);
            return Guid.Parse(created.PartyInfo.PartyGuid);
        }

        private Task<int> SeedClass(Guid customer) => Scalar<int>(@"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
            VALUES (@Customer, 'PurgeClass', 'TestMap', 0, 0, 0) RETURNING ClassID", new { Customer = customer });

        private Task SeedCharacter(Guid customer, string ownerEmail, string name, int classId) => Execute(
            @"INSERT INTO Characters (CustomerGUID, UserGUID, CharName, ClassID, X, Y, Z)
            VALUES (@Customer, (SELECT UserGUID FROM Users WHERE CustomerGUID = @Customer AND Email = @Email), @Name, @ClassID, 0, 0, 0)",
            new { Customer = customer, Email = ownerEmail, Name = name, ClassID = classId });

        // A row in a table both the purge and RemoveCharacter delete from, for the gate to hold.
        private Task PutOnMap(Guid customer, string charName) => Execute(
            @"INSERT INTO CharOnMapInstance (CustomerGUID, CharacterID, MapInstanceID)
            SELECT CustomerGUID, CharacterID, 1 FROM Characters WHERE CustomerGUID = @Customer AND CharName = @CharName",
            new { Customer = customer, CharName = charName });

        private static async Task LockMapRow(NpgsqlConnection gate, NpgsqlTransaction gateTx, Guid customer, string charName) =>
            Assert.Equal(1, (await gate.QueryAsync<int>(@"SELECT M.CharacterID FROM CharOnMapInstance M
                JOIN Characters C ON C.CustomerGUID = M.CustomerGUID AND C.CharacterID = M.CharacterID
                WHERE M.CustomerGUID = @Customer AND C.CharName = @CharName
                FOR UPDATE OF M", new { Customer = customer, CharName = charName }, gateTx)).Count());

        // The race is a READ COMMITTED one: the purge and the repository run at the server default.
        private async Task AssertReadCommitted()
        {
            await using var connection = new NpgsqlConnection(RepositoryConnectionString);
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            Assert.Equal("read committed", await connection.ExecuteScalarAsync<string>("SHOW transaction_isolation", transaction: transaction));
        }

        private Task<int> LockWaiters(NpgsqlConnection connection) => connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)::INT FROM pg_stat_activity
            WHERE datname = current_database() AND application_name = @ApplicationName AND wait_event_type = 'Lock'",
            new { ApplicationName = _applicationName });

        // Every operation must be parked on a lock before the gate opens, or the test would prove nothing about the race.
        private async Task WaitForLockWaiters(int expected, params Task[] operations)
        {
            await using var connection = new NpgsqlConnection(Db);
            for (int attempt = 0; attempt < 200; attempt++)
            {
                foreach (Task operation in operations.Where(operation => operation.IsCompleted))
                {
                    await operation;
                    Assert.Fail("An operation finished while the gate still held its lock.");
                }

                if (await LockWaiters(connection) >= expected)
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException($"Expected {expected} operation(s) to wait on a lock.");
        }

        // Without the party lock the add has nothing to wait for and commits; with it the add waits behind the purge,
        // which is the one other waiter. Either way the purge has not moved past the gate yet.
        private async Task WaitForAddToFinishOrWait(Task add)
        {
            await using var connection = new NpgsqlConnection(Db);
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (add.IsCompleted || await LockWaiters(connection) >= 2)
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("The party add neither finished nor waited on a lock.");
        }

        private static async Task<Exception[]> Outcomes(params Task[] operations)
        {
            var failures = new List<Exception>();
            foreach (Task operation in operations)
            {
                try
                {
                    await operation;
                    failures.Add(null);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
            return failures.ToArray();
        }

        private async Task Execute(string sql, object parameters)
        {
            await using var connection = new NpgsqlConnection(Db);
            await connection.ExecuteAsync(sql, parameters);
        }

        private async Task<T> Scalar<T>(string sql, object parameters)
        {
            await using var connection = new NpgsqlConnection(Db);
            return await connection.QuerySingleAsync<T>(sql, parameters);
        }

        private async Task<IEnumerable<T>> Query<T>(string sql, object parameters)
        {
            await using var connection = new NpgsqlConnection(Db);
            return await connection.QueryAsync<T>(sql, parameters);
        }
    }
}
