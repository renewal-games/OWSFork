using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OWSData.Models.Composites;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared.Implementations;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Users
{
    /// <summary>
    /// Character create/remove are reached from the internet-facing PublicAPI and return ErrorMessage in a 200 body,
    /// so a database failure must come back as a fixed message, never the Npgsql/Postgres exception text.
    /// </summary>
    public class CharacterErrorMessageTests
    {
        // Nothing listens on port 1, so every query fails with an NpgsqlException.
        private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=ows;Username=ows;Password=ows;Timeout=2;Pooling=false";

        private static UsersRepository Repository() =>
            new UsersRepository(
                Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBConnectionString = UnreachableDatabase }),
                NullLogger<UsersRepository>.Instance);

        [Fact]
        public async Task CreateSamsaraCharacter_DatabaseFailureReturnsGenericMessage()
        {
            CreateCharacter result = await Repository().CreateSamsaraCharacter(Guid.NewGuid(), Guid.NewGuid(), "SomeCharacter", "Warrior");

            Assert.False(result.Success);
            Assert.Equal(UsersRepository.CreateCharacterFailedMessage, result.ErrorMessage);
        }

        [Fact]
        public async Task RemoveCharacter_DatabaseFailureReturnsGenericMessage()
        {
            SuccessAndErrorMessage result = await Repository().RemoveCharacter(Guid.NewGuid(), Guid.NewGuid(), "SomeCharacter");

            Assert.False(result.Success);
            Assert.Equal(UsersRepository.RemoveCharacterFailedMessage, result.ErrorMessage);
        }

        [Theory]
        [InlineData(4, true)]
        [InlineData(50, true)]
        [InlineData(51, false)]
        public void ValidateCharacterName_EnforcesColumnLength(int length, bool valid)
        {
            string error = new DefaultPublicAPIInputValidation().ValidateCharacterName(new string('a', length));

            Assert.Equal(valid, error == "");
        }
    }
}
