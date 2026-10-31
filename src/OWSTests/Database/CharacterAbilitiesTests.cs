using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSCharacterPersistence.Requests.Characters;
using OWSData.Models.Composites;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Implementations;
using OWSShared.Interfaces;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// UpdateCharacterAbilities serializes the abilities in camelCase, so the upsert has to read every key in camelCase,
    /// customData included. It also has to fail for a character that doesn't exist instead of writing nothing and
    /// reporting success. Migrated schema only: setup.sql alone has no CharAbilities table.
    /// </summary>
    public class CharacterAbilitiesTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public CharacterAbilitiesTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task UpdateCharacterAbilities_KeepsCustomData(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacter(source, customerGUID, "AbilityUser");

            SuccessAndErrorMessage result = await Handle(source, customerGUID, "AbilityUser",
                new CharacterAbilityDto { AbilityIdTag = "Ability.Fireball", CurrentAbilityLevel = 2, ActualAbilityLevel = 3, CustomData = "x" });

            Assert.True(result.Success, result.ErrorMessage);
            CharacterAbilityDto stored = Assert.Single(await Repository(source).GetCharacterAbilities(customerGUID, "AbilityUser"));
            Assert.Equal("Ability.Fireball", stored.AbilityIdTag);
            Assert.Equal(2, stored.CurrentAbilityLevel);
            Assert.Equal(3, stored.ActualAbilityLevel);
            Assert.Equal("x", stored.CustomData);

            // The update path of the upsert has to carry it too.
            result = await Handle(source, customerGUID, "AbilityUser",
                new CharacterAbilityDto { AbilityIdTag = "Ability.Fireball", CurrentAbilityLevel = 4, ActualAbilityLevel = 4, CustomData = "y" });

            Assert.True(result.Success, result.ErrorMessage);
            stored = Assert.Single(await Repository(source).GetCharacterAbilities(customerGUID, "AbilityUser"));
            Assert.Equal(4, stored.CurrentAbilityLevel);
            Assert.Equal("y", stored.CustomData);
        }

        [PostgresTheory]
        [InlineData(SchemaSource.Migrated)]
        public async Task UpdateCharacterAbilities_FailsForUnknownCharacter(SchemaSource source)
        {
            Guid customerGUID = Guid.NewGuid();

            SuccessAndErrorMessage result = await Handle(source, customerGUID, "NoSuchCharacter",
                new CharacterAbilityDto { AbilityIdTag = "Ability.Fireball", CurrentAbilityLevel = 1, ActualAbilityLevel = 1, CustomData = "x" });

            Assert.False(result.Success);
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM CharAbilities WHERE CustomerGUID = @CustomerGUID", new { CustomerGUID = customerGUID }));
        }

        private async Task<SuccessAndErrorMessage> Handle(SchemaSource source, Guid customerGUID, string charName, params CharacterAbilityDto[] abilities)
        {
            var request = new UpdateCharacterAbilitiesRequest { CharacterName = charName, CharacterAbilities = abilities.ToList() };
            request.SetData(Repository(source), new HeaderCustomerGUID { CustomerGUID = customerGUID });
            return await request.Handle();
        }

        private CharactersRepository Repository(SchemaSource source) =>
            new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = _databases.ConnectionString(source) }));

        private async Task SeedCharacter(SchemaSource source, Guid customerGUID, string charName)
        {
            await using var connection = new NpgsqlConnection(_databases.ConnectionString(source));
            int classID = await connection.QuerySingleAsync<int>(
                @"INSERT INTO Class (CustomerGUID, ClassName, StartingMapName, X, Y, Z)
                  VALUES (@CustomerGUID, 'TestClass', 'TestMap', 0, 0, 0)
                  RETURNING ClassID",
                new { CustomerGUID = customerGUID });
            await connection.ExecuteAsync(
                @"INSERT INTO Characters (CustomerGUID, CharName, X, Y, Z, ClassID)
                  VALUES (@CustomerGUID, @CharName, 0, 0, 0, @ClassID)",
                new { CustomerGUID = customerGUID, CharName = charName, ClassID = classID });
        }
    }
}
