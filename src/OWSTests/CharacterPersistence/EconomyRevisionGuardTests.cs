using OWSCharacterPersistence.Requests.Characters;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSShared.Implementations;
using OWSShared.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace OWSTests.CharacterPersistence
{
    /// <summary>
    /// The bag and wallet saves are absolute writes. A snapshot computed before a shop transaction
    /// committed, written without ExpectedRevision, puts the sold items or spent gold back. With
    /// RequireExpectedRevision on, such a write must be refused before it reaches the repository.
    /// </summary>
    public class EconomyRevisionGuardTests
    {
        private const string CharName = "Buyer";

        private static readonly EconomyOptions Required = new EconomyOptions { RequireExpectedRevision = true };
        private static readonly EconomyOptions Legacy = new EconomyOptions();
        private static readonly List<UpdateCharacterQuest> NoQuests = new List<UpdateCharacterQuest>();
        private static readonly List<UpdateCharacterStats> NoStats = new List<UpdateCharacterStats>();

        [Fact]
        public async Task Inventory_StaleSnapshotWithoutRevision_IsRefused_WhenRequired()
        {
            FakeEconomy economy = FakeEconomy.Create(revision: 4, bag: "potion", gold: 100);
            List<UpdateCharacterInventory> prePurchaseBag = economy.Bag;
            economy.CommitPurchase(item: "sword", price: 60);

            var request = new UpdateCharacterInventoryRequest { CharacterName = CharName, Inventory = prePurchaseBag };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), Required);
            UpdateCharacterInventoryResponse response = await request.Handle();

            Assert.False(response.Success);
            Assert.Equal("revision_required", response.ReasonCode);
            Assert.Contains(economy.Bag, i => i.ItemIDTag == "sword");
            Assert.Equal(5, economy.Revision);
        }

        [Fact]
        public async Task Inventory_StaleSnapshotWithoutRevision_StillOverwrites_WhenNotRequired()
        {
            // Documents the legacy path the option exists to close; turn the option on once the zone
            // server sends ExpectedRevision on every bag save.
            FakeEconomy economy = FakeEconomy.Create(revision: 4, bag: "potion", gold: 100);
            List<UpdateCharacterInventory> prePurchaseBag = economy.Bag;
            economy.CommitPurchase(item: "sword", price: 60);

            var request = new UpdateCharacterInventoryRequest { CharacterName = CharName, Inventory = prePurchaseBag };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), Legacy);
            UpdateCharacterInventoryResponse response = await request.Handle();

            Assert.True(response.Success);
            Assert.DoesNotContain(economy.Bag, i => i.ItemIDTag == "sword");
        }

        [Fact]
        public async Task Inventory_StaleRevision_IsRefused()
        {
            FakeEconomy economy = FakeEconomy.Create(revision: 4, bag: "potion", gold: 100);
            List<UpdateCharacterInventory> prePurchaseBag = economy.Bag;
            economy.CommitPurchase(item: "sword", price: 60);

            var request = new UpdateCharacterInventoryRequest { CharacterName = CharName, Inventory = prePurchaseBag, ExpectedRevision = 4 };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), Required);
            UpdateCharacterInventoryResponse response = await request.Handle();

            Assert.False(response.Success);
            Assert.Equal("stale_revision", response.ReasonCode);
            Assert.Equal(5, response.NewEconomyRevision);
            Assert.Contains(economy.Bag, i => i.ItemIDTag == "sword");
        }

        [Fact]
        public async Task Inventory_RevisionZero_IsARealRevision()
        {
            // EconomyRevision defaults to 0, so a character no shop op has touched must still be able
            // to save once the guard is mandatory.
            FakeEconomy economy = FakeEconomy.Create(revision: 0, bag: "potion", gold: 100);

            var request = new UpdateCharacterInventoryRequest { CharacterName = CharName, Inventory = economy.Bag, ExpectedRevision = 0 };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), Required);
            UpdateCharacterInventoryResponse response = await request.Handle();

            Assert.True(response.Success);
            Assert.Equal(1, response.NewEconomyRevision);
        }

        [Fact]
        public async Task CharacterData_WithoutRevision_RefusesBagButSavesQuestsAndStats_WhenRequired()
        {
            FakeEconomy economy = FakeEconomy.Create(revision: 4, bag: "potion", gold: 100);
            List<UpdateCharacterInventory> prePurchaseBag = economy.Bag;
            economy.CommitPurchase(item: "sword", price: 60);

            var request = new UpdateCharacterDataRequest { CharacterName = CharName, CharQuests = NoQuests, CharInventory = prePurchaseBag, CharStats = NoStats };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), Required);
            UpdateCharacterDataResponse response = await request.Handle();

            Assert.False(response.Success);
            Assert.Equal("revision_required", response.ReasonCode);
            Assert.Contains(economy.Bag, i => i.ItemIDTag == "sword");
            Assert.Equal(1, economy.QuestSaves);
            Assert.Equal(1, economy.StatSaves);
        }

        [Fact]
        public async Task CharacterData_PassesRevisionThroughAndReturnsTheNewOne()
        {
            FakeEconomy economy = FakeEconomy.Create(revision: 4, bag: "potion", gold: 100);

            var request = new UpdateCharacterDataRequest { CharacterName = CharName, CharQuests = NoQuests, CharInventory = economy.Bag, CharStats = NoStats, ExpectedRevision = 4 };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), Required);
            UpdateCharacterDataResponse response = await request.Handle();

            Assert.True(response.Success);
            Assert.Equal(5, response.NewEconomyRevision);
        }

        [Fact]
        public async Task Currency_StaleWalletWithoutRevision_IsRefused_WhenRequired()
        {
            FakeEconomy economy = FakeEconomy.Create(revision: 4, bag: "potion", gold: 100);
            economy.CommitPurchase(item: "sword", price: 60);

            var request = new UpdateCharacterCurrencyRequest { CharacterName = CharName, Gold = 100 };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), Required);
            UpdateCharacterCurrencyResponse response = await request.Handle();

            Assert.False(response.Success);
            Assert.Equal("revision_required", response.ReasonCode);
            Assert.Equal(40, economy.Gold);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        [InlineData(1_000_001)]
        public async Task Currency_OutOfRangeGold_IsRefused(int gold)
        {
            FakeEconomy economy = FakeEconomy.Create(revision: 4, bag: "potion", gold: 100);

            var request = new UpdateCharacterCurrencyRequest { CharacterName = CharName, Gold = gold, ExpectedRevision = 4 };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), new EconomyOptions { MaxGold = 1_000_000 });
            UpdateCharacterCurrencyResponse response = await request.Handle();

            Assert.False(response.Success);
            Assert.Equal("gold_out_of_range", response.ReasonCode);
            Assert.Equal(100, economy.Gold);
        }

        [Fact]
        public async Task Currency_InRangeGoldWithRevision_IsWritten()
        {
            FakeEconomy economy = FakeEconomy.Create(revision: 4, bag: "potion", gold: 100);

            var request = new UpdateCharacterCurrencyRequest { CharacterName = CharName, Gold = 1_000_000, ExpectedRevision = 4 };
            request.SetData(economy.Repository, new HeaderCustomerGUID(), new EconomyOptions { RequireExpectedRevision = true, MaxGold = 1_000_000 });
            UpdateCharacterCurrencyResponse response = await request.Handle();

            Assert.True(response.Success);
            Assert.Equal(1_000_000, economy.Gold);
            Assert.Equal(5, response.NewEconomyRevision);
        }

        /// <summary>
        /// In-memory stand-in for the Postgres CharactersRepository's revision protocol: the guard
        /// runs only when a revision is supplied, a guarded bag write bumps it, an unguarded one does
        /// not, and a wallet write always does.
        /// </summary>
        public class FakeEconomy : DispatchProxy
        {
            public long Revision;
            public int Gold;
            public List<UpdateCharacterInventory> Bag = new List<UpdateCharacterInventory>();
            public int QuestSaves;
            public int StatSaves;
            public ICharactersRepository Repository;

            public static FakeEconomy Create(long revision, string bag, int gold)
            {
                ICharactersRepository repository = Create<ICharactersRepository, FakeEconomy>();
                FakeEconomy economy = (FakeEconomy)(object)repository;
                economy.Repository = repository;
                economy.Revision = revision;
                economy.Gold = gold;
                economy.Bag = new List<UpdateCharacterInventory> { Item(bag) };
                return economy;
            }

            public void CommitPurchase(string item, int price)
            {
                Bag = Bag.Append(Item(item)).ToList();
                Gold -= price;
                Revision++;
            }

            private static UpdateCharacterInventory Item(string tag) => new UpdateCharacterInventory { ItemIDTag = tag, Quantity = 1 };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case nameof(ICharactersRepository.UpdateCharacterInventory):
                        // args[3] is the owner token; the stale-owner check is covered by InventoryOwnerTokenTests.
                        return Task.FromResult(WriteInventory((IEnumerable<UpdateCharacterInventory>)args[2], (long?)args[4]));
                    case nameof(ICharactersRepository.UpdateCharacterCurrency):
                        return Task.FromResult(WriteCurrency((int)args[2], (long?)args[3]));
                    case nameof(ICharactersRepository.UpdateCharacterData):
                        // args[5] is the owner token, args[6] the zone instance; both checks are covered by the database tests.
                        return Task.FromResult(WriteCharacterData((IEnumerable<UpdateCharacterInventory>)args[3], (long?)args[7], (bool)args[8]));
                    case nameof(ICharactersRepository.UpdateCharacterQuests):
                        QuestSaves++;
                        return Task.FromResult(true);
                    case nameof(ICharactersRepository.UpdateCharacterStats):
                        StatSaves++;
                        return Task.FromResult(true);
                    default:
                        throw new NotSupportedException(targetMethod.Name);
                }
            }

            private UpdateCharacterInventoryResponse WriteInventory(IEnumerable<UpdateCharacterInventory> bag, long? expectedRevision)
            {
                if (expectedRevision.HasValue && expectedRevision.Value != Revision)
                {
                    return new UpdateCharacterInventoryResponse { Success = false, ReasonCode = "stale_revision", NewEconomyRevision = Revision };
                }
                Bag = (bag ?? Enumerable.Empty<UpdateCharacterInventory>()).ToList();
                if (expectedRevision.HasValue)
                {
                    Revision++;
                }
                return new UpdateCharacterInventoryResponse { Success = true, NewEconomyRevision = Revision };
            }

            // Mirrors the Postgres combined save: a revision refusal skips only the bag, quests and stats still save.
            private UpdateCharacterDataResponse WriteCharacterData(IEnumerable<UpdateCharacterInventory> bag, long? expectedRevision, bool requireExpectedRevision)
            {
                QuestSaves++;
                StatSaves++;
                if (expectedRevision == null && requireExpectedRevision)
                {
                    return new UpdateCharacterDataResponse { Success = false, ReasonCode = "revision_required", NewEconomyRevision = Revision };
                }
                UpdateCharacterInventoryResponse inventory = WriteInventory(bag, expectedRevision);
                return new UpdateCharacterDataResponse { Success = inventory.Success, ReasonCode = inventory.ReasonCode ?? "", NewEconomyRevision = inventory.NewEconomyRevision };
            }

            private UpdateCharacterCurrencyResponse WriteCurrency(int gold, long? expectedRevision)
            {
                if (expectedRevision.HasValue && expectedRevision.Value != Revision)
                {
                    return new UpdateCharacterCurrencyResponse { Success = true, ReasonCode = "stale_revision", NewEconomyRevision = Revision, AuthoritativeGold = Gold };
                }
                Gold = gold;
                Revision++;
                return new UpdateCharacterCurrencyResponse { Success = true, NewEconomyRevision = Revision, AuthoritativeGold = gold };
            }
        }
    }
}
