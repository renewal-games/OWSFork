using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using OWSCharacterPersistence.Requests.Characters;
using OWSData.Models.Composites;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Implementations;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Database
{
    /// <summary>
    /// UpdateAllPlayerPositions: a non-finite or out-of-world coordinate costs only its own entry, and a save carrying a
    /// token that no longer owns the character writes nothing, even when the claim lands while the save is in flight.
    /// CharacterOwners comes from a migration, so these run against the migrated schema.
    /// </summary>
    public class PositionSaveTests : IClassFixture<PostgresTestDatabases>
    {
        private readonly PostgresTestDatabases _databases;

        public PositionSaveTests(PostgresTestDatabases databases)
        {
            _databases = databases;
        }

        private string ConnectionString => _databases.ConnectionString(SchemaSource.Migrated);

        [PostgresTheory]
        [InlineData("NaN:0:0")]
        [InlineData("0:-Infinity:0")]
        [InlineData("0:0:Infinity")]
        [InlineData("1e13:0:0")]
        public async Task Batch_SavesTheValidPlayerAndSkipsTheBadOne(string badLocation)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacter(customerGUID, "Good");
            await SeedCharacter(customerGUID, "Bad");

            SuccessAndErrorMessage result = await Save(customerGUID, $"Bad:{badLocation}:0:0:0|Good:10.5:20:30:0:90:0");

            Assert.True(result.Success);
            Assert.Contains("Bad", result.ErrorMessage);
            Assert.DoesNotContain("Good", result.ErrorMessage);
            Assert.Equal((10.5, 20.0, 31.0), await Location(customerGUID, "Good"));
            Assert.Equal((0.0, 0.0, 0.0), await Location(customerGUID, "Bad"));
        }

        [PostgresTheory]
        [InlineData("0:NaN:0")]
        [InlineData("0:0:Infinity")]
        public async Task Batch_SkipsNonFiniteRotation(string badRotation)
        {
            Guid customerGUID = Guid.NewGuid();
            await SeedCharacter(customerGUID, "Bad");

            SuccessAndErrorMessage result = await Save(customerGUID, $"Bad:1:2:3:{badRotation}");

            Assert.Contains("Bad", result.ErrorMessage);
            Assert.Equal((0.0, 0.0, 0.0), await Location(customerGUID, "Bad"));
        }

        [PostgresTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CurrentOwner_Saves(bool handoverRunning)
        {
            Guid customerGUID = Guid.NewGuid();
            int characterID = await SeedCharacter(customerGUID, "Owned");
            Guid owner = Guid.NewGuid();
            await SeedOwner(customerGUID, characterID, owner, Guid.NewGuid(), handoverRunning ? "20 seconds" : "-1 seconds");

            SuccessAndErrorMessage result = await Save(customerGUID, $"Owned:1:2:3:0:0:0:{owner}");

            Assert.Equal(string.Empty, result.ErrorMessage);
            Assert.Equal((1.0, 2.0, 4.0), await Location(customerGUID, "Owned"));
            Assert.True(await LastAccessBumped(customerGUID, "Owned"));
        }

        [PostgresFact]
        public async Task PreviousOwner_SavesWhileTheHandoverRuns()
        {
            Guid customerGUID = Guid.NewGuid();
            int characterID = await SeedCharacter(customerGUID, "Owned");
            Guid previous = Guid.NewGuid();
            await SeedOwner(customerGUID, characterID, Guid.NewGuid(), previous, "20 seconds");

            SuccessAndErrorMessage result = await Save(customerGUID, $"Owned:1:2:3:0:0:0:{previous}");

            Assert.Equal(string.Empty, result.ErrorMessage);
            Assert.Equal((1.0, 2.0, 4.0), await Location(customerGUID, "Owned"));
        }

        [PostgresTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task StaleOwner_UpdatesNothing(bool previousOwnerAfterHandover)
        {
            Guid customerGUID = Guid.NewGuid();
            int characterID = await SeedCharacter(customerGUID, "Owned");
            Guid previous = Guid.NewGuid();
            await SeedOwner(customerGUID, characterID, Guid.NewGuid(), previous, "-1 seconds");
            Guid writer = previousOwnerAfterHandover ? previous : Guid.NewGuid();

            SuccessAndErrorMessage result = await Save(customerGUID, $"Owned:1:2:3:0:0:0:{writer}");

            Assert.Contains("Owned", result.ErrorMessage);
            Assert.Equal((0.0, 0.0, 0.0), await Location(customerGUID, "Owned"));
            Assert.False(await LastAccessBumped(customerGUID, "Owned"));
        }

        [PostgresFact]
        public async Task ClaimCommittingDuringTheSave_UpdatesNothing()
        {
            Guid customerGUID = Guid.NewGuid();
            int characterID = await SeedCharacter(customerGUID, "Owned");
            Guid oldOwner = Guid.NewGuid();
            await SeedOwner(customerGUID, characterID, oldOwner, null, null);

            // A claim holds the character row lock (CharacterOwnersRepository.SetOwner) until it commits.
            await using var claim = new NpgsqlConnection(ConnectionString);
            await claim.OpenAsync();
            await using NpgsqlTransaction claimTx = await claim.BeginTransactionAsync();
            await claim.ExecuteAsync("SELECT CharacterID FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID FOR UPDATE",
                new { CustomerGUID = customerGUID, CharacterID = characterID }, claimTx);

            Task<SuccessAndErrorMessage> save = Save(customerGUID, $"Owned:1:2:3:0:0:0:{oldOwner}");
            await WaitUntilBlockedOnLock(save);

            await claim.ExecuteAsync(@"UPDATE CharacterOwners SET OwnerToken = @NewOwner, PreviousOwnerToken = NULL, PreviousOwnerUntil = NULL
                    WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID",
                new { NewOwner = Guid.NewGuid(), CustomerGUID = customerGUID, CharacterID = characterID }, claimTx);
            await claimTx.CommitAsync();

            SuccessAndErrorMessage result = await save;

            Assert.Contains("Owned", result.ErrorMessage);
            Assert.Equal((0.0, 0.0, 0.0), await Location(customerGUID, "Owned"));
        }

        private async Task<SuccessAndErrorMessage> Save(Guid customerGUID, string serialized)
        {
            var request = new UpdateAllPlayerPositionsRequest { SerializedPlayerLocationData = serialized, MapName = "" };
            request.SetData(
                new CharactersRepository(Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBBackend = "postgres", OWSDBConnectionString = ConnectionString })),
                new HeaderCustomerGUID { CustomerGUID = customerGUID });
            return await request.Handle();
        }

        // Polled from its own connection: pg_stat_activity is read once per transaction, so the claim's would never change.
        private async Task WaitUntilBlockedOnLock(Task save)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Assert.False(save.IsCompleted, "The save finished while the claim held the character row.");
                int waiting = await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");
                if (waiting > 0)
                {
                    return;
                }
                await Task.Delay(20);
            }
            throw new TimeoutException("The save never waited on the character row.");
        }

        private async Task<int> SeedCharacter(Guid customerGUID, string name)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            return await connection.QuerySingleAsync<int>(
                @"WITH U AS (
                      INSERT INTO Users (UserGUID, CustomerGUID, FirstName, LastName, Email, PasswordHash, CreateDate, LastAccess, Role)
                      VALUES (gen_random_uuid(), @CustomerGUID, 'Test', 'User', @Email, 'x', NOW(), '2000-01-01', 'Player')
                      RETURNING UserGUID)
                  INSERT INTO Characters (CustomerGUID, UserGUID, CharName, MapName, X, Y, Z, ClassID)
                  SELECT @CustomerGUID, U.UserGUID, @Name, 'Map', 0, 0, 0, 1 FROM U
                  RETURNING CharacterID",
                new { CustomerGUID = customerGUID, Email = name + "@example.com", Name = name });
        }

        private async Task SeedOwner(Guid customerGUID, int characterID, Guid owner, Guid? previous, string previousUntilFromNow)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.ExecuteAsync(
                @"INSERT INTO CharacterOwners (CustomerGUID, CharacterID, OwnerToken, MapInstanceID, ClaimKind, LastConfirmedAt, PreviousOwnerToken, PreviousOwnerUntil)
                  VALUES (@CustomerGUID, @CharacterID, @Owner, 1, 2, NOW(), @Previous, NOW() + @Until::interval)",
                new { CustomerGUID = customerGUID, CharacterID = characterID, Owner = owner, Previous = previous, Until = previousUntilFromNow });
        }

        private sealed class LocationRow
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
        }

        private async Task<(double, double, double)> Location(Guid customerGUID, string name)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            LocationRow row = await connection.QuerySingleAsync<LocationRow>(
                "SELECT X, Y, Z FROM Characters WHERE CustomerGUID = @CustomerGUID AND CharName = @Name",
                new { CustomerGUID = customerGUID, Name = name });
            return (row.X, row.Y, row.Z);
        }

        private async Task<bool> LastAccessBumped(Guid customerGUID, string name)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            return await connection.QuerySingleAsync<bool>(
                @"SELECT U.LastAccess > '2000-01-01' FROM Users U
                  JOIN Characters C ON C.UserGUID = U.UserGUID
                  WHERE C.CustomerGUID = @CustomerGUID AND C.CharName = @Name",
                new { CustomerGUID = customerGUID, Name = name });
        }
    }
}
