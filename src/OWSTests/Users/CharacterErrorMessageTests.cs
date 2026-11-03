using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OWSData.Models.Composites;
using OWSData.Repositories.Implementations.Postgres;
using OWSShared;
using OWSShared.Implementations;
using OWSShared.Options;
using Xunit;

namespace OWSTests.Users
{
    /// <summary>
    /// Character create/remove are reached from the internet-facing PublicAPI and return ErrorMessage in a 200 body,
    /// so a database failure must come back as a fixed message, never the Npgsql/Postgres exception text.
    /// The failure is logged with the client-supplied character name, which must not be able to forge log lines.
    /// </summary>
    public class CharacterErrorMessageTests
    {
        // Nothing listens on port 1, so every query fails with an NpgsqlException.
        private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=ows;Username=ows;Password=ows;Timeout=2;Pooling=false";

        private static UsersRepository Repository(CapturingLogger logger = null) =>
            new UsersRepository(
                Microsoft.Extensions.Options.Options.Create(new StorageOptions { OWSDBConnectionString = UnreachableDatabase }),
                logger ?? new CapturingLogger());

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

        // ValidateCharacterName trims before checking, so leading/trailing line breaks reach the repository on create.
        [Fact]
        public async Task CreateSamsaraCharacter_DatabaseFailureLogCannotForgeLines()
        {
            const string characterName = "Alice\r\n";
            Assert.Equal("", new DefaultPublicAPIInputValidation().ValidateCharacterName(characterName));
            var logger = new CapturingLogger();

            await Repository(logger).CreateSamsaraCharacter(Guid.NewGuid(), Guid.NewGuid(), characterName, "Warrior");

            AssertNoLineBreaks(logger);
            Assert.Contains("Alice", logger.Message);
        }

        // Remove has no name validation at all.
        [Fact]
        public async Task RemoveCharacter_DatabaseFailureLogCannotForgeLines()
        {
            var logger = new CapturingLogger();

            await Repository(logger).RemoveCharacter(Guid.NewGuid(), Guid.NewGuid(), "Alice\r\nforged entry");

            AssertNoLineBreaks(logger);
            Assert.Contains("Alice??forged entry", logger.Message);
        }

        [Fact]
        public async Task RemoveCharacter_DatabaseFailureLogCapsLongName()
        {
            var logger = new CapturingLogger();

            await Repository(logger).RemoveCharacter(Guid.NewGuid(), Guid.NewGuid(), new string('a', 100_000));

            Assert.DoesNotContain(new string('a', LogSanitizer.DefaultMaxLength + 1), logger.Message);
            Assert.True(logger.Message.Length < 200, $"Log message is {logger.Message.Length} characters long.");
            Assert.True(((string)logger.CharacterName).Length < 100);
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

        // Text sinks print the formatted message; structured sinks print the CharacterName property. Check both.
        private static void AssertNoLineBreaks(CapturingLogger logger)
        {
            Assert.NotNull(logger.Message);
            Assert.DoesNotContain("\r", logger.Message);
            Assert.DoesNotContain("\n", logger.Message);

            string property = Assert.IsType<string>(logger.CharacterName);
            Assert.DoesNotContain("\r", property);
            Assert.DoesNotContain("\n", property);
        }

        private sealed class CapturingLogger : ILogger<UsersRepository>
        {
            public string Message { get; private set; }
            public object CharacterName { get; private set; }

            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                Assert.Equal(LogLevel.Error, logLevel);
                Message = formatter(state, exception);
                CharacterName = (state as IEnumerable<KeyValuePair<string, object>>)?
                    .FirstOrDefault(property => property.Key == "CharacterName").Value;
            }
        }
    }
}
