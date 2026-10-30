using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.SQL;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// GetSaveDataByCharName fills CharCurrency from Characters.Gold. It used to run the inventory query, which returns
    /// nothing for a character with gold and no items. The test runs the query directly: on Postgres the repository
    /// method still fails earlier, on GetCharGuidByCharName's T-SQL Convert().
    /// </summary>
    public class CharacterSaveDataTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public CharacterSaveDataTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task CurrencyQuery_ReturnsGoldForCharacterWithoutInventory(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacter(source, customerGUID, "GoldOnly", gold: 250);
            var parameters = new { CustomerGUID = customerGUID, CharName = "GoldOnly" };

            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            CharacterCurrency currency = Assert.Single(await connection.QueryAsync<CharacterCurrency>(GenericQueries.GetCharCurrencyByCharName, parameters));

            Assert.Equal(250, currency.Gold);
            Assert.Empty(await connection.QueryAsync<CharacterInventory>(PostgresQueries.GetCharInventoryByCharName, parameters));
        }

        private async Task SeedCharacter(SchemaSource source, Guid customerGUID, string charName, int gold)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            int classID = await connection.QuerySingleAsync<int>(
                @"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                  VALUES (@CustomerGUID, 'TestClass', 'TestMap', 0, 0, 0)
                  RETURNING ClassID",
                new { CustomerGUID = customerGUID });
            await connection.ExecuteAsync(
                @"INSERT INTO Characters (CustomerGUID, CharName, X, Y, Z, ClassID, Gold)
                  VALUES (@CustomerGUID, @CharName, 0, 0, 0, @ClassID, @Gold)",
                new { CustomerGUID = customerGUID, CharName = charName, ClassID = classID, Gold = gold });
        }
    }
}
