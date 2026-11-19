using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
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
    /// The stats, quests, abilities and combined saves hand the repository a SaveOrder: the caller's SaveSequence, if
    /// any, and the time this service received the request, which orders the saves of callers that send no sequence
    /// (issue #247). The ordering itself is covered by Database/CharacterSaveOrderingTests.
    /// </summary>
    public class SaveSequenceForwardingTests
    {
        private static readonly Guid Token = Guid.NewGuid();

        // How AddMvcCore's default input formatter reads a request body.
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        // The body SSPlayerCharacterPersistenceComponent::SaveCharacterStatsToDatabase builds today (FUpdateCharStat
        // through FJsonObjectConverter, which writes camelCase) once UOWSPlayerControllerComponent::WithOwnerToken has
        // added OwnerToken. It has no SaveSequence.
        private static string CurrentClientStatsBody(int strength) =>
            "{\"characterName\":\"Saver\",\"characterStats\":[{\"statIdentifier\":\"Strength\",\"value\":" + strength + "}]," +
            "\"OwnerToken\":\"" + Token + "\"}";

        [Theory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("data")]
        public async Task SaveSequence_ReachesTheRepository(string kind)
        {
            SaveOrder order = await Send(kind, 42);

            Assert.Equal(42, order.Sequence);
        }

        [Theory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("data")]
        public async Task WithoutASequence_TheSaveIsStillOrderedByWhenItWasReceived(string kind)
        {
            SaveOrder order = await Send(kind, null);

            Assert.Null(order.Sequence);
            Assert.Equal(DateTimeKind.Utc, order.ReceivedAt.Kind);
        }

        [Fact]
        public async Task TodaysClientBody_IsStampedWhenItIsRead_NotWhenItReachesTheRepository()
        {
            // A is read first, then B; B reaches the repository first. A's stamp must still be the earlier one.
            var a = JsonSerializer.Deserialize<UpdateCharacterStatsRequest>(CurrentClientStatsBody(10), Web);
            var b = JsonSerializer.Deserialize<UpdateCharacterStatsRequest>(CurrentClientStatsBody(11), Web);

            Assert.Equal(Token, a.OwnerToken);
            Assert.Null(a.SaveSequence);
            Assert.Equal(10, Assert.Single(a.CharacterStats).Value);

            SaveOrder orderB = await Handle(b);
            SaveOrder orderA = await Handle(a);

            Assert.True(orderA.ReceivedAt < orderB.ReceivedAt);
        }

        [Fact]
        public async Task ABodyCannotSetTheReceiptTime()
        {
            var late = JsonSerializer.Deserialize<UpdateCharacterStatsRequest>(
                CurrentClientStatsBody(10).TrimEnd('}') + ",\"receivedAt\":\"2100-01-01T00:00:00Z\",\"ReceivedAt\":\"2100-01-01T00:00:00Z\"}", Web);

            SaveOrder order = await Handle(late);

            Assert.True(order.ReceivedAt < new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        [Fact]
        public void SequenceIsReadFromTheBody_AndZeroMeansNone()
        {
            var sequenced = JsonSerializer.Deserialize<UpdateCharacterStatsRequest>(CurrentClientStatsBody(10).TrimEnd('}') + ",\"SaveSequence\":7}", Web);
            Assert.Equal(7, sequenced.SaveSequence);

            Assert.Null(new SaveOrder(0, SaveClock.Next()).Sequence);
            Assert.Null(new SaveOrder(-1, SaveClock.Next()).Sequence);
        }

        [Fact]
        public void SaveClock_StrictlyIncreases()
        {
            DateTime previous = SaveClock.Next();
            for (int i = 0; i < 10000; i++)
            {
                DateTime next = SaveClock.Next();
                Assert.True(next > previous);
                previous = next;
            }
        }

        private static async Task<SaveOrder> Handle(UpdateCharacterStatsRequest request)
        {
            ICharactersRepository repository = RecordingRepository.Create(out RecordingRepository recorder);
            request.SetData(repository, new HeaderCustomerGUID { CustomerGUID = Guid.NewGuid() });
            await request.Handle();
            return recorder.SaveOrder;
        }

        private static async Task<SaveOrder> Send(string kind, long? sequence)
        {
            ICharactersRepository repository = RecordingRepository.Create(out RecordingRepository recorder);
            var customer = new HeaderCustomerGUID { CustomerGUID = Guid.NewGuid() };

            switch (kind)
            {
                case "stats":
                    var stats = new UpdateCharacterStatsRequest { CharacterName = "Saver", OwnerToken = Token, SaveSequence = sequence, CharacterStats = new List<UpdateCharacterStats>() };
                    stats.SetData(repository, customer);
                    await stats.Handle();
                    break;
                case "quests":
                    var quests = new UpdateCharacterQuestsRequest { CharacterName = "Saver", OwnerToken = Token, SaveSequence = sequence, CharacterQuests = new List<UpdateCharacterQuest>() };
                    quests.SetData(repository, customer);
                    await quests.Handle();
                    break;
                case "abilities":
                    var abilities = new UpdateCharacterAbilitiesRequest { CharacterName = "Saver", OwnerToken = Token, SaveSequence = sequence };
                    abilities.SetData(repository, customer);
                    await abilities.Handle();
                    break;
                default:
                    var data = new UpdateCharacterDataRequest
                    {
                        CharacterName = "Saver",
                        OwnerToken = Token,
                        SaveSequence = sequence,
                        CharQuests = new List<UpdateCharacterQuest>(),
                        CharInventory = new List<UpdateCharacterInventory>(),
                        CharStats = new List<UpdateCharacterStats>()
                    };
                    data.SetData(repository, customer, new EconomyOptions());
                    await data.Handle();
                    break;
            }

            Assert.NotNull(recorder.SaveOrder);
            return recorder.SaveOrder;
        }

        [Theory]
        [InlineData("stats")]
        [InlineData("quests")]
        [InlineData("abilities")]
        [InlineData("data")]
        public async Task SaveSequenceRequired_IsReportedAsAFailure(string kind)
        {
            ICharactersRepository repository = RecordingRepository.Create(out RecordingRepository recorder);
            recorder.Refusal = "save_sequence_required";
            var customer = new HeaderCustomerGUID { CustomerGUID = Guid.NewGuid() };

            SuccessAndErrorMessage reply;
            switch (kind)
            {
                case "stats":
                    var stats = new UpdateCharacterStatsRequest { CharacterName = "Saver", OwnerToken = Token, CharacterStats = new List<UpdateCharacterStats>() };
                    stats.SetData(repository, customer);
                    reply = await stats.Handle();
                    break;
                case "quests":
                    var quests = new UpdateCharacterQuestsRequest { CharacterName = "Saver", OwnerToken = Token, CharacterQuests = new List<UpdateCharacterQuest>() };
                    quests.SetData(repository, customer);
                    reply = await quests.Handle();
                    break;
                case "abilities":
                    var abilities = new UpdateCharacterAbilitiesRequest { CharacterName = "Saver", OwnerToken = Token };
                    abilities.SetData(repository, customer);
                    reply = await abilities.Handle();
                    break;
                default:
                    var data = new UpdateCharacterDataRequest
                    {
                        CharacterName = "Saver",
                        OwnerToken = Token,
                        CharQuests = new List<UpdateCharacterQuest>(),
                        CharInventory = new List<UpdateCharacterInventory>(),
                        CharStats = new List<UpdateCharacterStats>()
                    };
                    data.SetData(repository, customer, new EconomyOptions());
                    reply = await data.Handle();
                    break;
            }

            Assert.False(reply.Success);
            Assert.Contains("save_sequence_required", reply.ErrorMessage);
        }

        public class RecordingRepository : DispatchProxy
        {
            public SaveOrder SaveOrder { get; private set; }
            public string Refusal { get; set; }

            public static ICharactersRepository Create(out RecordingRepository recorder)
            {
                ICharactersRepository proxy = Create<ICharactersRepository, RecordingRepository>();
                recorder = (RecordingRepository)proxy;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                ParameterInfo[] parameters = targetMethod.GetParameters();
                int index = Array.FindIndex(parameters, p => p.Name == "saveOrder");
                Assert.True(index >= 0, targetMethod.Name + " has no saveOrder parameter.");
                SaveOrder = (SaveOrder)args[index];

                if (targetMethod.Name == nameof(ICharactersRepository.UpdateCharacterData))
                {
                    return Task.FromResult(Refusal == null
                        ? new UpdateCharacterDataResponse { Success = true, ErrorMessage = "", ReasonCode = "" }
                        : new UpdateCharacterDataResponse { Success = false, ErrorMessage = Refusal, ReasonCode = Refusal });
                }

                return Task.FromResult(Refusal);
            }
        }
    }
}
