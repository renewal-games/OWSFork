using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using OWSCharacterPersistence.Requests.Characters;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Implementations;
using OWSShared.Options;
using Xunit;

namespace OWSTests.CharacterPersistence
{
    /// <summary>
    /// The stats, quests, abilities and combined saves hand their SaveSequence to the repository, which orders the
    /// writes by it (issue #247). The ordering itself is covered by Database/CharacterSaveOrderingTests.
    /// </summary>
    public class SaveSequenceForwardingTests
    {
        private const long Sequence = 42;
        private static readonly Guid Token = Guid.NewGuid();

        [Theory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("data")]
        public async Task SaveSequence_ReachesTheRepository(string kind)
        {
            ICharactersRepository repository = RecordingRepository.Create(out RecordingRepository recorder);
            var customer = new HeaderCustomerGUID { CustomerGUID = Guid.NewGuid() };

            switch (kind)
            {
                case "stats":
                    var stats = new UpdateCharacterStatsRequest { CharacterName = "Saver", OwnerToken = Token, SaveSequence = Sequence, CharacterStats = new List<UpdateCharacterStats>() };
                    stats.SetData(repository, customer);
                    await stats.Handle();
                    break;
                case "quests":
                    var quests = new UpdateCharacterQuestsRequest { CharacterName = "Saver", OwnerToken = Token, SaveSequence = Sequence, CharacterQuests = new List<UpdateCharacterQuest>() };
                    quests.SetData(repository, customer);
                    await quests.Handle();
                    break;
                case "abilities":
                    var abilities = new UpdateCharacterAbilitiesRequest { CharacterName = "Saver", OwnerToken = Token, SaveSequence = Sequence };
                    abilities.SetData(repository, customer);
                    await abilities.Handle();
                    break;
                default:
                    var data = new UpdateCharacterDataRequest
                    {
                        CharacterName = "Saver",
                        OwnerToken = Token,
                        SaveSequence = Sequence,
                        CharQuests = new List<UpdateCharacterQuest>(),
                        CharInventory = new List<UpdateCharacterInventory>(),
                        CharStats = new List<UpdateCharacterStats>()
                    };
                    data.SetData(repository, customer, new EconomyOptions());
                    await data.Handle();
                    break;
            }

            Assert.Equal(Sequence, recorder.SaveSequence);
        }

        public class RecordingRepository : DispatchProxy
        {
            public long? SaveSequence { get; private set; }

            public static ICharactersRepository Create(out RecordingRepository recorder)
            {
                ICharactersRepository proxy = Create<ICharactersRepository, RecordingRepository>();
                recorder = (RecordingRepository)proxy;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                ParameterInfo[] parameters = targetMethod.GetParameters();
                int index = Array.FindIndex(parameters, p => p.Name == "saveSequence");
                Assert.True(index >= 0, targetMethod.Name + " has no saveSequence parameter.");
                SaveSequence = (long?)args[index];

                return targetMethod.Name == nameof(ICharactersRepository.UpdateCharacterData)
                    ? Task.FromResult(new UpdateCharacterDataResponse { Success = true, ErrorMessage = "", ReasonCode = "" })
                    : Task.FromResult<string>(null);
            }
        }
    }
}
