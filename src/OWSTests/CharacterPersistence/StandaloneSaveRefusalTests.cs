using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using OWSCharacterPersistence.Requests.Characters;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Implementations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Xunit;

namespace OWSTests.CharacterPersistence
{
    /// <summary>
    /// The standalone zone saves pass their owner token down to the repository, and a refusal there gets the same reply
    /// as the controller's check. The refusal itself is covered by the database tests.
    /// </summary>
    public class StandaloneSaveRefusalTests
    {
        private const string CharName = "Saver";
        private static readonly Guid Token = Guid.NewGuid();

        private static async Task<SuccessAndErrorMessage> Save(string kind, string refusal)
        {
            ICharactersRepository repository = FakeRepository.Create(refusal, out FakeRepository fake);
            SuccessAndErrorMessage reply;
            switch (kind)
            {
                case "stats":
                    var stats = new UpdateCharacterStatsRequest { CharacterName = CharName, OwnerToken = Token, CharacterStats = new List<UpdateCharacterStats>() };
                    stats.SetData(repository, new HeaderCustomerGUID());
                    reply = await stats.Handle();
                    break;
                case "quests":
                    var quests = new UpdateCharacterQuestsRequest { CharacterName = CharName, OwnerToken = Token, CharacterQuests = new List<UpdateCharacterQuest>() };
                    quests.SetData(repository, new HeaderCustomerGUID());
                    reply = await quests.Handle();
                    break;
                case "class":
                    var characterClass = new UpdateCharacterClassRequest { CharacterName = CharName, OwnerToken = Token, ClassName = "Mage" };
                    characterClass.SetData(repository, new HeaderCustomerGUID());
                    reply = await characterClass.Handle();
                    break;
                default:
                    var abilities = new UpdateCharacterAbilitiesRequest { CharacterName = CharName, OwnerToken = Token };
                    abilities.SetData(repository, new HeaderCustomerGUID());
                    reply = await abilities.Handle();
                    break;
            }

            Assert.Equal(Token, fake.TokenSeen);
            return reply;
        }

        [Theory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("class")]
        [InlineData("abilities")]
        public async Task StaleOwner_FromTheRepository_GetsTheControllersReply(string kind)
        {
            SuccessAndErrorMessage reply = await Save(kind, "stale_owner");

            Assert.False(reply.Success);
            Assert.Equal("stale_owner", reply.ErrorMessage);
            Assert.Equal("stale_owner", Assert.IsType<StaleOwnerResult>(reply).ReasonCode);
        }

        // These actions declare SuccessAndErrorMessage, so this checks ReasonCode still reaches the zone server.
        [Fact]
        public async Task StaleOwnerReply_ThroughADeclaredBaseType_KeepsItsWireFields()
        {
            var http = new DefaultHttpContext();
            await using var body = new MemoryStream();
            http.Response.Body = body;
            var formatter = new SystemTextJsonOutputFormatter(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            });

            await formatter.WriteResponseBodyAsync(new OutputFormatterWriteContext(http, (stream, encoding) => new StreamWriter(stream, encoding),
                typeof(SuccessAndErrorMessage), new StaleOwnerResult()), Encoding.UTF8);

            using JsonDocument reply = JsonDocument.Parse(body.ToArray());
            Assert.False(reply.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("stale_owner", reply.RootElement.GetProperty("errorMessage").GetString());
            Assert.Equal("stale_owner", reply.RootElement.GetProperty("reasonCode").GetString());
        }

        [Theory]
        [InlineData("stats")]
        [InlineData("quests")]
        public async Task StaleZone_StaysSuccessSoTheCallerDoesNotRetry(string kind)
        {
            SuccessAndErrorMessage reply = await Save(kind, "stale_zone");

            Assert.True(reply.Success);
            Assert.Equal("stale_zone", reply.ErrorMessage);
        }

        [Theory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("class")]
        public async Task UnknownCharacter_StaysANoOpSuccess(string kind)
        {
            SuccessAndErrorMessage reply = await Save(kind, "character_not_found");

            Assert.True(reply.Success);
            Assert.True(string.IsNullOrEmpty(reply.ErrorMessage));
        }

        [Fact]
        public async Task Abilities_UnknownCharacter_FailsOnlyWhenThereWasSomethingToWrite()
        {
            ICharactersRepository repository = FakeRepository.Create("character_not_found", out _);

            var empty = new UpdateCharacterAbilitiesRequest { CharacterName = CharName };
            empty.SetData(repository, new HeaderCustomerGUID());
            Assert.True((await empty.Handle()).Success);

            var withAbility = new UpdateCharacterAbilitiesRequest { CharacterName = CharName, CharacterAbilities = new List<CharacterAbilityDto> { new() } };
            withAbility.SetData(repository, new HeaderCustomerGUID());
            SuccessAndErrorMessage reply = await withAbility.Handle();
            Assert.False(reply.Success);
            Assert.Equal("Character not found.", reply.ErrorMessage);
        }

        [Theory]
        [InlineData("stale_owner", "stale_owner")]
        [InlineData("character_not_found", null)]
        [InlineData(null, null)]
        public async Task CustomData_ReportsOnlyTheOwnerRefusal(string refusal, string expected)
        {
            ICharactersRepository repository = FakeRepository.Create(refusal, out FakeRepository fake);
            var request = new AddOrUpdateCustomDataRequest
            {
                OwnerToken = Token,
                addOrUpdateCustomCharacterData = new AddOrUpdateCustomCharacterData { CharacterName = CharName, CustomFieldName = "Field", FieldValue = "Value" }
            };
            request.SetData(repository, new HeaderCustomerGUID());

            Assert.Equal(expected, await request.Handle());
            Assert.Equal(Token, fake.TokenSeen);
        }

        public class FakeRepository : DispatchProxy
        {
            private string _refusal;
            public Guid? TokenSeen { get; private set; }

            public static ICharactersRepository Create(string refusal, out FakeRepository fake)
            {
                ICharactersRepository proxy = Create<ICharactersRepository, FakeRepository>();
                fake = (FakeRepository)proxy;
                fake._refusal = refusal;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                TokenSeen = args.OfType<Guid>().Skip(1).Cast<Guid?>().FirstOrDefault();
                return Task.FromResult(_refusal);
            }
        }
    }
}
