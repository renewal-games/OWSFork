using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OWSData.Repositories.Interfaces;
using OWSParty.Controllers;
using OWSShared.Implementations;
using Xunit;

namespace OWSTests.Party
{
    public class PartyControllerLogTests
    {
        [Theory]
        [InlineData("Description")]
        [InlineData("ExpDistribution")]
        [InlineData("LootDistribution")]
        public async Task UnexpectedFailure_LogsCanonicalPartyGuid(string setting)
        {
            Guid partyGuid = Guid.NewGuid();
            string routePartyGuid = $" {partyGuid}\r\n";
            Assert.True(Guid.TryParse(routePartyGuid, out _));

            string expectedMethod = $"UpdateParty{setting}";
            string actualMethod = null;
            ICharactersRepository repository = FakeRepository.Create<ICharactersRepository>((method, _) =>
            {
                actualMethod = method.Name;
                throw new Exception("Repository failed.");
            });
            var logger = new CapturingLogger();
            var controller = new PartyController(logger, repository, new HeaderCustomerGUID { CustomerGUID = Guid.NewGuid() });

            IActionResult result = setting switch
            {
                "Description" => await controller.UpdatePartyDescription(routePartyGuid, new UpdatePartyDescriptionRequest { ActorCharName = "Actor" }),
                "ExpDistribution" => await controller.UpdatePartyExpDistribution(routePartyGuid, new UpdatePartyExpDistributionRequest { ActorCharName = "Actor" }),
                _ => await controller.UpdatePartyLootDistribution(routePartyGuid, new UpdatePartyLootDistributionRequest { ActorCharName = "Actor" })
            };

            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal(expectedMethod, actualMethod);
            Assert.Equal($"Failed to update party {setting switch { "ExpDistribution" => "exp distribution", "LootDistribution" => "loot distribution", _ => "description" }} for {partyGuid:D}.", logger.Message);
            Assert.DoesNotContain("\r", logger.Message);
            Assert.DoesNotContain("\n", logger.Message);
        }

        private sealed class CapturingLogger : ILogger<PartyController>
        {
            public string Message { get; private set; }

            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                Assert.Equal(LogLevel.Error, logLevel);
                Message = formatter(state, exception);
            }
        }
    }
}
