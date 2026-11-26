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
    public class PartyConcurrencyTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        // Repository connections carry this name, so the lock-wait checks ignore other tests sharing the database.
        private readonly string _applicationName = "party-concurrency-" + Guid.NewGuid().ToString("N");

        // The shared migrated database unless a test gives itself one.
        private string _connectionString;

        public PartyConcurrencyTests(PostgresTestDatabases databases) => _databases = databases;

        private string Db => _connectionString ??= _databases.ConnectionString(SchemaSource.Migrated);

        // Seven of eight slots taken, two leader-sent adds for different characters. A share lock on PartyMember lets
        // both read the member count but stops both inserts. Without the party lock both read seven and both insert, so
        // the party ends with nine. With it the second add waits for the first to commit and then sees the party full.
        [PostgresFact]
        public async Task ConcurrentAddsForTheLastSlot_AdmitOnlyOne()
        {
            Guid customer = Guid.NewGuid();
            await SeedCharacters(customer, null, "Leader", "M1", "M2", "M3", "M4", "M5", "M6", "TargetA", "TargetB");
            Guid partyGuid = await CreateParty(customer, "Leader");
            await Execute(@"INSERT INTO PartyMember (CustomerGUID, PartyID, CharacterID, PartyLeader, PartyRole)
                SELECT C.CustomerGUID, P.PartyID, C.CharacterID, FALSE, 0
                FROM Characters C JOIN Party P ON P.CustomerGUID = C.CustomerGUID AND P.PartyGuid = @PartyGuid
                WHERE C.CustomerGUID = @Customer AND C.CharName IN ('M1', 'M2', 'M3', 'M4', 'M5', 'M6')",
                new { Customer = customer, PartyGuid = partyGuid });
            Assert.Equal(7, await MemberCount(customer, partyGuid));
            Assert.Equal(8, await Scalar<int>("SELECT MaxMembers FROM Party WHERE CustomerGUID = @Customer AND PartyGuid = @PartyGuid",
                new { Customer = customer, PartyGuid = partyGuid }));
            await AssertReadCommitted();

            CharactersRepository characters = Characters();
            await using var gate = new NpgsqlConnection(Db);
            await gate.OpenAsync();
            await using NpgsqlTransaction gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("LOCK TABLE PartyMember IN SHARE MODE", transaction: gateTx);

            Task<PartyToSend> addA = characters.CreatePartyOrAddMember(customer, AddRequest(partyGuid, "Leader", "TargetA"));
            Task<PartyToSend> addB = characters.CreatePartyOrAddMember(customer, AddRequest(partyGuid, "Leader", "TargetB"));
            await WaitForLockWaiters(2, addA, addB);
            await gateTx.CommitAsync();

            Exception[] failures = await Outcomes(addA, addB);
            Assert.Single(failures, failure => failure == null);
            Exception rejected = Assert.Single(failures, failure => failure != null);
            Assert.IsType<InvalidOperationException>(rejected);
            Assert.Equal("Party is full.", rejected.Message);

            Assert.Equal(8, await MemberCount(customer, partyGuid));
            string[] grouped = (await Query<string>(@"SELECT C.CharName FROM PartyMember PM
                JOIN Characters C ON C.CustomerGUID = PM.CustomerGUID AND C.CharacterID = PM.CharacterID
                WHERE PM.CustomerGUID = @Customer AND C.CharName IN ('TargetA', 'TargetB')", new { Customer = customer })).ToArray();
            Assert.Single(grouped);
        }

        [PostgresFact]
        public async Task MutationRecovery_WaitsForTheCommitAndReportsItToBothParticipants()
        {
            Guid customer = Guid.NewGuid();
            Guid mutation = Guid.NewGuid();
            await SeedCharacters(customer, null, "Leader", "Target");
            Guid partyGuid = await CreateParty(customer, "Leader");
            CharactersRepository characters = Characters();

            await using var gate = new NpgsqlConnection(Db);
            await gate.OpenAsync();
            await using NpgsqlTransaction gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("LOCK TABLE PartyMember IN SHARE MODE", transaction: gateTx);

            PartyToSend request = AddRequest(partyGuid, "Leader", "Target");
            Task<PartyToSend> add = characters.CreatePartyOrAddMember(customer, request, mutation);
            await WaitForLockWaiters(1, add);
            Task<bool> recover = characters.ResolvePartyMutation(customer, mutation, "Target");
            await WaitForLockWaiters(2, add, recover);
            await gateTx.CommitAsync();

            await add;
            Assert.True(await recover);
            Assert.True(await characters.ResolvePartyMutation(customer, mutation, "Leader"));
            Assert.Null(await characters.CreatePartyOrAddMember(customer, request, mutation));
            Assert.Equal(2, await MemberCount(customer, partyGuid));
        }

        [PostgresFact]
        public async Task MutationRecovery_CancelsAnAddThatHasNotReachedTheDatabase()
        {
            Guid customer = Guid.NewGuid();
            Guid mutation = Guid.NewGuid();
            await SeedCharacters(customer, null, "Leader", "Target");
            Guid partyGuid = await CreateParty(customer, "Leader");
            CharactersRepository characters = Characters();

            Assert.False(await characters.ResolvePartyMutation(customer, mutation, "Target"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                characters.CreatePartyOrAddMember(customer, AddRequest(partyGuid, "Leader", "Target"), mutation));

            Assert.Equal("Party mutation was canceled.", error.Message);
            Assert.False(await characters.ResolvePartyMutation(customer, mutation, "Leader"));
            Assert.Equal(1, await MemberCount(customer, partyGuid));
        }

        [PostgresFact]
        public async Task ReplacementRegistration_RejectsADelayedCreateAndCannotBeSupersededByARetry()
        {
            Guid customer = Guid.NewGuid();
            Guid former = Guid.NewGuid();
            Guid current = Guid.NewGuid();
            await SeedCharacters(customer, null, "Leader");
            CharactersRepository characters = Characters();
            Assert.True(await characters.RegisterPartyMutationParticipant(customer, "Leader", former, default) > 0);
            long order = await characters.RegisterPartyMutationParticipant(customer, "Leader", current, default);
            Assert.True(order > 0);
            Assert.Equal(0L, await characters.RegisterPartyMutationParticipant(customer, "Leader", former, default));
            Assert.Equal(order, await characters.RegisterPartyMutationParticipant(customer, "Leader", current, default));

            var request = new PartyToSend
            {
                CustomerGuid = customer.ToString(),
                PartyAction = PartyAction.MessageTypeCreate,
                PartyInfo = new PartyInfo(),
                PartyMembers = { new PartyMemberInfo { CharName = "Leader", PartyLeader = true } }
            };

            Guid formerMutation = Guid.NewGuid();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                characters.CreatePartyOrAddMember(customer, request, formerMutation, former));
            Assert.Equal("A party mutation participant has a newer registration.", error.Message);
            Assert.False(await characters.ResolvePartyMutation(customer, formerMutation, "Leader"));
            Assert.Empty((await characters.GetInitialPartySettings(customer, "Leader")).PartyMembers);
            Assert.Single((await characters.CreatePartyOrAddMember(customer, request, Guid.NewGuid(), current)).PartyMembers);
        }

        [PostgresFact]
        public async Task ReplacementRegistration_WaitsForAnInFlightWriteBeforeReadingTheRoster()
        {
            Guid customer = Guid.NewGuid();
            Guid actor = Guid.NewGuid();
            Guid invitee = Guid.NewGuid();
            await SeedCharacters(customer, null, "Leader", "Target");
            Guid partyGuid = await CreateParty(customer, "Leader");
            CharactersRepository characters = Characters();
            await characters.RegisterPartyMutationParticipant(customer, "Leader", actor, default);
            await characters.RegisterPartyMutationParticipant(customer, "Target", invitee, default);

            await using var gate = new NpgsqlConnection(Db);
            await gate.OpenAsync();
            await using NpgsqlTransaction gateTx = await gate.BeginTransactionAsync();
            await gate.ExecuteAsync("LOCK TABLE PartyMember IN SHARE MODE", transaction: gateTx);

            var add = characters.CreatePartyOrAddMember(customer, AddRequest(partyGuid, "Leader", "Target"),
                Guid.NewGuid(), actor, invitee);
            await WaitForLockWaiters(1, add);
            var replacement = characters.RegisterPartyMutationParticipant(customer, "Target", Guid.NewGuid(), default);
            await WaitForLockWaiters(2, add, replacement);
            await gateTx.CommitAsync();

            await add;
            Assert.True(await replacement > 0);
            PartyToSend roster = await characters.GetInitialPartySettings(customer, "Target");
            Assert.Equal(partyGuid.ToString(), roster.PartyInfo.PartyGuid);
            Assert.Equal(2, roster.PartyMembers.Count);
        }

        [PostgresFact]
        public async Task ReplacementInviteeRegistration_RejectsADelayedAdd()
        {
            Guid customer = Guid.NewGuid();
            Guid actor = Guid.NewGuid();
            Guid formerInvitee = Guid.NewGuid();
            await SeedCharacters(customer, null, "Leader", "Target");
            Guid partyGuid = await CreateParty(customer, "Leader");
            CharactersRepository characters = Characters();
            await characters.RegisterPartyMutationParticipant(customer, "Leader", actor, default);
            await characters.RegisterPartyMutationParticipant(customer, "Target", formerInvitee, default);
            await characters.RegisterPartyMutationParticipant(customer, "Target", Guid.NewGuid(), default);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                characters.CreatePartyOrAddMember(customer, AddRequest(partyGuid, "Leader", "Target"),
                    Guid.NewGuid(), actor, formerInvitee));
            Assert.Equal("A party mutation participant has a newer registration.", error.Message);
            Assert.Equal(1, await MemberCount(customer, partyGuid));
        }

        // An add holds the party lock and has inserted its member but not committed when the leader disbands. Without
        // the lock the disband deletes the members it can see, then deletes the party once the add commits, leaving the
        // added member in a party that no longer exists. With it the disband waits and removes that member too.
        [PostgresFact]
        public async Task Disband_WaitsForAnAddInFlight_AndLeavesNoMemberBehind()
        {
            Guid customer = Guid.NewGuid();
            await SeedCharacters(customer, null, "Leader", "Member", "Target");
            Guid partyGuid = await CreateParty(customer, "Leader", "Member");

            await using var addInFlight = new NpgsqlConnection(Db);
            await addInFlight.OpenAsync();
            await using NpgsqlTransaction addTx = await addInFlight.BeginTransactionAsync();
            await LockPartyAndInsertMember(addInFlight, addTx, customer, partyGuid, "Target");

            var disbandRequest = new PartyToSend
            {
                CustomerGuid = customer.ToString(),
                PartyAction = PartyAction.MessageTypeDismiss,
                PartyInfo = new PartyInfo { PartyGuid = partyGuid.ToString() },
                PartyMembers = { new PartyMemberInfo { CharName = "Leader", PartyLeader = true } }
            };
            Task<PartyToSend> disband = Characters().LeaveParty(customer, disbandRequest);
            await WaitForLockWaiters(1, disband);
            await addTx.CommitAsync();
            await disband;

            Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM Party WHERE CustomerGUID = @Customer", new { Customer = customer }));
            Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM PartyMember WHERE CustomerGUID = @Customer", new { Customer = customer }));
        }

        // The sole member deletes their character while an add into that party is in flight. Without the lock the
        // removal sees no one left and deletes the party once the add commits, stranding the new member. With it the
        // removal waits, then sees the new member and hands them the lead.
        [PostgresFact]
        public async Task RemoveCharacter_WaitsForAnAddInFlight_AndHandsOverLeadership()
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
                CREATE TABLE IF NOT EXISTS CharOnMapInstance (CustomerGUID UUID, CharacterID INT);
                CREATE TABLE IF NOT EXISTS CustomCharacterData (CustomerGUID UUID, CharacterID INT);
                CREATE TABLE IF NOT EXISTS CharQuests (CustomerGUID UUID, CharacterID INT);", null);

            Guid customer = Guid.NewGuid();
            UsersRepository users = Users();
            const string email = "party-remove@example.com";
            Assert.True((await users.RegisterUser(customer, email, "pw", "Party", "User")).Success);
            PlayerLoginAndCreateSession login = await users.LoginAndCreateSession(customer, email, "pw");
            Assert.True(login.Authenticated, login.ErrorMessage);
            await SeedCharacters(customer, email, "Leader", "Target");
            Guid partyGuid = await CreateParty(customer, "Leader");

            await using var addInFlight = new NpgsqlConnection(Db);
            await addInFlight.OpenAsync();
            await using NpgsqlTransaction addTx = await addInFlight.BeginTransactionAsync();
            await LockPartyAndInsertMember(addInFlight, addTx, customer, partyGuid, "Target");

            Task<SuccessAndErrorMessage> remove = users.RemoveCharacter(customer, login.UserSessionGuid.Value, "Leader");
            await WaitForLockWaiters(1, remove);
            await addTx.CommitAsync();
            SuccessAndErrorMessage removed = await remove;
            Assert.True(removed.Success, removed.ErrorMessage);

            Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM Characters WHERE CustomerGUID = @Customer AND CharName = 'Leader'",
                new { Customer = customer }));
            Assert.Equal(1, await MemberCount(customer, partyGuid));
            Assert.Equal("Target", await Scalar<string>(@"SELECT C.CharName FROM PartyMember PM
                JOIN Party P ON P.CustomerGUID = PM.CustomerGUID AND P.PartyID = PM.PartyID
                JOIN Characters C ON C.CustomerGUID = PM.CustomerGUID AND C.CharacterID = PM.CharacterID
                WHERE PM.CustomerGUID = @Customer AND P.PartyGuid = @PartyGuid AND PM.PartyLeader",
                new { Customer = customer, PartyGuid = partyGuid }));
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

        // Stands in for an add that has passed its checks and inserted its member but not committed.
        private static async Task LockPartyAndInsertMember(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid customer, Guid partyGuid, string charName)
        {
            await connection.ExecuteAsync("SELECT PartyID FROM Party WHERE CustomerGUID = @Customer AND PartyGuid = @PartyGuid FOR UPDATE",
                new { Customer = customer, PartyGuid = partyGuid }, transaction);
            Assert.Equal(1, await connection.ExecuteAsync(@"INSERT INTO PartyMember (CustomerGUID, PartyID, CharacterID, PartyLeader, PartyRole)
                SELECT C.CustomerGUID, P.PartyID, C.CharacterID, FALSE, 0
                FROM Characters C JOIN Party P ON P.CustomerGUID = C.CustomerGUID AND P.PartyGuid = @PartyGuid
                WHERE C.CustomerGUID = @Customer AND C.CharName = @CharName",
                new { Customer = customer, PartyGuid = partyGuid, CharName = charName }, transaction));
        }

        private async Task SeedCharacters(Guid customer, string ownerEmail, params string[] names)
        {
            int classId = await Scalar<int>(@"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                VALUES (@Customer, 'PartyClass', 'TestMap', 0, 0, 0) RETURNING ClassID", new { Customer = customer });
            foreach (string name in names)
            {
                await Execute(@"INSERT INTO Characters (CustomerGUID, UserGUID, CharName, ClassID, X, Y, Z)
                    VALUES (@Customer, (SELECT UserGUID FROM Users WHERE CustomerGUID = @Customer AND Email = @Email), @Name, @ClassID, 0, 0, 0)",
                    new { Customer = customer, Email = ownerEmail, Name = name, ClassID = classId });
            }
        }

        // The race is a READ COMMITTED one: the repository opens its transactions at the server default.
        private async Task AssertReadCommitted()
        {
            await using var connection = new NpgsqlConnection(RepositoryConnectionString);
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            Assert.Equal("read committed", await connection.ExecuteScalarAsync<string>("SHOW transaction_isolation", transaction: transaction));
        }

        // Every operation must be parked on a lock before the gate opens, or the test would prove nothing about the race.
        private async Task WaitForLockWaiters(int expected, params Task[] operations)
        {
            await using var connection = new NpgsqlConnection(Db);
            for (int attempt = 0; attempt < 200; attempt++)
            {
                foreach (Task operation in operations.Where(operation => operation.IsCompleted))
                {
                    await operation;
                    Assert.Fail("A party write finished while the gate still held its lock.");
                }

                int waiting = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*)::INT FROM pg_stat_activity
                    WHERE datname = current_database() AND application_name = @ApplicationName AND wait_event_type = 'Lock'",
                    new { ApplicationName = _applicationName });
                if (waiting >= expected)
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException($"Expected {expected} party write(s) to wait on a lock.");
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

        private Task<int> MemberCount(Guid customer, Guid partyGuid) => Scalar<int>(@"SELECT COUNT(*)::INT FROM PartyMember PM
            JOIN Party P ON P.CustomerGUID = PM.CustomerGUID AND P.PartyID = PM.PartyID
            WHERE PM.CustomerGUID = @Customer AND P.PartyGuid = @PartyGuid", new { Customer = customer, PartyGuid = partyGuid });

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
