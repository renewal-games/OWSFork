using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    public class ChatMembershipTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;
        public ChatMembershipTests(PostgresTestDatabases databases) => _databases = databases;

        [PostgresTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MembersAreScopedToCurrentSendersGroupAndCustomer(bool guild)
        {
            string connectionString = _databases.ConnectionString(SchemaSource.Migrated);
            var repository = new ChatMembershipRepository(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBConnectionString = connectionString }));
            await using var connection = new NpgsqlConnection(connectionString);
            Guid customer = Guid.NewGuid();
            Guid otherCustomer = Guid.NewGuid();
            await SeedCharacters(customer);
            await SeedCharacters(otherCustomer);

            // Reuse group IDs in another tenant to catch missing tenant predicates on any join.
            await connection.ExecuteAsync(@"
                INSERT INTO PartyMember (CustomerGUID, PartyID, CharacterID, PartyLeader)
                    SELECT CustomerGUID, CASE WHEN CharName = 'D' THEN 2 ELSE 1 END, CharacterID, FALSE
                    FROM Characters WHERE CustomerGUID IN (@Customer, @OtherCustomer) AND CharName <> 'C';
                INSERT INTO GuildMember (CustomerGUID, GuildID, CharacterID, GuildRank, GuildJoinedDate)
                    SELECT CustomerGUID, CASE WHEN CharName = 'D' THEN 2 ELSE 1 END, CharacterID, 0, NOW()
                    FROM Characters WHERE CustomerGUID IN (@Customer, @OtherCustomer) AND CharName <> 'C';",
                new { Customer = customer, OtherCustomer = otherCustomer });

            Assert.Equal(new[] { "A", "B" }, (await Members(customer, "a")).OrderBy(n => n));
            Assert.Equal(new[] { "D" }, await Members(customer, "D"));
            Assert.Empty(await Members(customer, "C"));
            Assert.Empty(await Members(customer, "missing' OR '1'='1"));

            await connection.ExecuteAsync(@"
                DELETE FROM PartyMember WHERE CustomerGUID = @Customer AND CharacterID IN
                    (SELECT CharacterID FROM Characters WHERE CustomerGUID = @Customer AND CharName = 'B');
                DELETE FROM GuildMember WHERE CustomerGUID = @Customer AND CharacterID IN
                    (SELECT CharacterID FROM Characters WHERE CustomerGUID = @Customer AND CharName = 'B');",
                new { Customer = customer });
            Assert.Equal(new[] { "A" }, await Members(customer, "A"));
            Assert.Empty(await Members(customer, "B"));

            Task<System.Collections.Generic.IEnumerable<string>> Members(Guid tenant, string name) => guild
                ? repository.GetGuildMembers(tenant, name) : repository.GetPartyMembers(tenant, name);

            async Task SeedCharacters(Guid tenant)
            {
                int classId = await connection.QuerySingleAsync<int>(@"
                    INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                    VALUES (@Customer, 'ChatClass', 'TestMap', 0, 0, 0) RETURNING ClassID", new { Customer = tenant });
                await connection.ExecuteAsync(@"
                    INSERT INTO Characters (CustomerGUID, CharName, ClassID, X, Y, Z)
                    VALUES (@Customer, 'A', @ClassID, 0, 0, 0), (@Customer, 'B', @ClassID, 0, 0, 0),
                           (@Customer, 'C', @ClassID, 0, 0, 0), (@Customer, 'D', @ClassID, 0, 0, 0)",
                    new { Customer = tenant, ClassID = classId });
            }
        }
    }
}
