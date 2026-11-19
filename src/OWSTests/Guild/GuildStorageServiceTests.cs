using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using GuildServiceApp.Protos;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Grpc;
using Xunit;
using static OWSTests.Guild.GuildStorageTestDoubles;

namespace OWSTests.Guild
{
    /// <summary>
    /// Code issue #188: SendGuildMsg's storage actions trusted the caller's guild, quantities and recipient list.
    /// These cover the service's side with a fake repository; GuildStorageTransferTests run the same paths on Postgres.
    /// </summary>
    [Collection(OWSTests.Grpc.SessionAuthEnvironmentCollection.Name)]
    public class GuildStorageServiceTests : IDisposable
    {
        private static readonly Guid Customer = Guid.Parse("44444444-4444-4444-4444-444444444444");
        private static readonly Guid GuildGuid = Guid.Parse("55555555-5555-5555-5555-555555555555");

        private readonly string _previous = Environment.GetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable);

        public GuildStorageServiceTests() => Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, "true");
        public void Dispose() => Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, _previous);

        [Theory]
        [InlineData(true, -5)]
        [InlineData(true, 0)]
        [InlineData(false, -5)]
        [InlineData(false, 0)]
        public async Task NonPositiveQuantity_IsInvalidArgument_AndNeverReachesStorage(bool deposit, int quantity)
        {
            var storage = new FakeStorage();

            var ex = await Assert.ThrowsAsync<RpcException>(() => CreateService(storage, "A")
                .SendGuildMsg(StorageMessage(Customer, GuildGuid, deposit, "Item.Potion", quantity), new CallContext(Customer)));

            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
            Assert.Empty(storage.Calls);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task WithoutRequiredEconomyRevision_StorageIsClosed_AndNeverReachesStorage(bool deposit)
        {
            var storage = new FakeStorage();
            using var a = Register(Customer, "A");

            var ex = await Assert.ThrowsAsync<RpcException>(() => CreateService(storage, "A", requireExpectedRevision: false)
                .SendGuildMsg(StorageMessage(Customer, GuildGuid, deposit, "Item.Potion", 1, "A"), new CallContext(Customer)));

            Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
            Assert.Contains("RequireExpectedRevision", ex.Status.Detail);
            Assert.Empty(storage.Calls);
            Assert.Empty(a.Written);
        }

        [Fact]
        public async Task WithSessionAuthOff_StorageIsRefused_BecauseNoCharacterIsVerified()
        {
            Environment.SetEnvironmentVariable(GrpcSessionAuth.RequireSessionAuthVariable, "false");
            var storage = new FakeStorage();

            var ex = await Assert.ThrowsAsync<RpcException>(() => CreateService(storage, null)
                .SendGuildMsg(StorageMessage(Customer, GuildGuid, false, "Item.Potion", 1), new CallContext(Customer)));

            Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
            Assert.Empty(storage.Calls);
        }

        [Fact]
        public async Task TransferActsForTheSessionCharacter_NotANameInTheRequest()
        {
            var storage = new FakeStorage();

            await CreateService(storage, "A")
                .SendGuildMsg(StorageMessage(Customer, GuildGuid, true, "Item.Potion", 3, "Someone", "Else"), new CallContext(Customer));

            var call = Assert.Single(storage.Calls);
            Assert.Equal(Customer, call.Customer);
            Assert.Equal("A", call.Character);
            Assert.Equal(GuildGuid, call.Guild);
            Assert.True(call.Deposit);
            Assert.Equal(3, Assert.Single(call.Items).Quantity);
        }

        [Theory]
        [InlineData("not_member", StatusCode.PermissionDenied)]
        [InlineData("insufficient_items", StatusCode.FailedPrecondition)]
        [InlineData("insufficient_storage", StatusCode.FailedPrecondition)]
        [InlineData("inventory_full", StatusCode.FailedPrecondition)]
        [InlineData("bad_request", StatusCode.InvalidArgument)]
        public async Task RepositoryRefusal_MapsToStatus_AndNotifiesNobody(string reason, StatusCode expected)
        {
            var storage = new FakeStorage { Result = new GuildStorageTransferResult { Success = false, ReasonCode = reason } };
            using var a = Register(Customer, "A");

            var ex = await Assert.ThrowsAsync<RpcException>(() => CreateService(storage, "A")
                .SendGuildMsg(StorageMessage(Customer, GuildGuid, false, "Item.Potion", 1, "A"), new CallContext(Customer)));

            Assert.Equal(expected, ex.StatusCode);
            Assert.Empty(a.Written);
        }

        [Fact]
        public async Task Update_GoesToTheGuildsMembersFromStorage_NotToRequestGuildMembers_NorOtherTenants()
        {
            var storage = new FakeStorage
            {
                Result = new GuildStorageTransferResult
                {
                    Success = true,
                    StoredItems = new List<GuildStorageEntry> { new GuildStorageEntry { ItemIDTag = "Item.Potion", Quantity = 6, CustomData = "" } },
                    Members = new List<GuildStorageMember> { new GuildStorageMember { CharName = "A" }, new GuildStorageMember { CharName = "B" } }
                }
            };
            using var a = Register(Customer, "A");
            using var b = Register(Customer, "b");
            using var outsider = Register(Customer, "Outsider");
            using var otherTenantB = Register(Guid.NewGuid(), "B");

            await CreateService(storage, "A")
                .SendGuildMsg(StorageMessage(Customer, GuildGuid, false, "Item.Potion", 4, "Outsider"), new CallContext(Customer));

            Assert.Empty(outsider.Written);
            Assert.Empty(otherTenantB.Written);
            foreach (var member in new[] { a, b })
            {
                GuildToSend update = Assert.Single(member.Written);
                Assert.Equal(GuildAction.MessageTypeStorageWithdraw, update.GuildAction);
                Assert.Equal(GuildGuid.ToString("N"), update.GuildInfo.GuildGuid);
                Assert.Equal(6, Assert.Single(update.GuildBank.GuildCurrentStoredItems).GuildItemQuantity);
                Assert.Equal(new[] { "A", "B" }, update.GuildMembers.Select(m => m.CharName));
            }
        }

        private sealed class FakeStorage : IGuildStorageRepository
        {
            public List<(Guid Customer, string Character, Guid Guild, bool Deposit, IReadOnlyList<GuildStorageTransferItem> Items)> Calls { get; } = new();

            public GuildStorageTransferResult Result { get; set; } = new GuildStorageTransferResult { Success = true };

            public Task<List<GuildStorageEntry>> GetStoredItems(Guid customerGUID, Guid guildGuid) =>
                Task.FromResult(new List<GuildStorageEntry>());

            public Task<GuildStorageTransferResult> Transfer(Guid customerGUID, string characterName, Guid guildGuid, bool deposit,
                IReadOnlyList<GuildStorageTransferItem> items)
            {
                Calls.Add((customerGUID, characterName, guildGuid, deposit, items));
                return Task.FromResult(Result);
            }
        }
    }
}
