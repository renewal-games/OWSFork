using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSData.SQL;
using OWSShared.Options;

namespace OWSData.Repositories.Implementations.Postgres
{
    /// <summary>
    /// Guild storage as a transfer the server owns (code issue #188). The caller names only what to move; the
    /// membership, the bag it comes from or goes to, and the stored balance are all read and changed here, under the
    /// same locks, in one transaction.
    /// </summary>
    public class GuildStorageRepository : IGuildStorageRepository
    {
        // Same row the inventory saves and shop transactions lock (UpdateCharacterInventory, PlayerShopsRepository), so a
        // transfer serializes with every other write to this bag. Names are unique per customer case-insensitively
        // (UniqueCharacterNames), and the session check compares them that way too.
        private const string LockCharacter = @"SELECT CharacterID
                FROM Characters
                WHERE CustomerGUID = @CustomerGUID AND LOWER(CharName) = LOWER(@CharName)
                FOR UPDATE";

        // The guild row lock serializes storage writes from different members; the membership row is held shared so a
        // concurrent kick or leave waits for the transfer instead of racing it.
        private const string LockMembership = @"SELECT G.GuildID
                FROM GuildMember GM
                INNER JOIN Guild G ON G.CustomerGUID = GM.CustomerGUID AND G.GuildID = GM.GuildID
                WHERE GM.CustomerGUID = @CustomerGUID AND GM.CharacterID = @CharacterID AND G.GuildGuid = @GuildGuid
                FOR UPDATE OF G FOR SHARE OF GM";

        private const string GetInventoryID = @"SELECT CharInventoryID
                FROM CharInventory
                WHERE CustomerGUID = @CustomerGUID AND CharacterID = @CharacterID
                ORDER BY CharInventoryID
                LIMIT 1";

        private const string GetBagStacks = @"SELECT InSlotNumber, Quantity
                FROM CharInventoryItems
                WHERE CustomerGUID = @CustomerGUID AND CharInventoryID = @CharInventoryID
                  AND ItemIDTag = @ItemIDTag AND COALESCE(CustomData, '') = @CustomData AND Quantity > 0
                ORDER BY InSlotNumber";

        private const string DeleteBagSlot = @"DELETE FROM CharInventoryItems
                WHERE CustomerGUID = @CustomerGUID AND CharInventoryID = @CharInventoryID AND InSlotNumber = @InSlotNumber";

        private const string DebitBagSlot = @"UPDATE CharInventoryItems
                SET Quantity = Quantity - @Take
                WHERE CustomerGUID = @CustomerGUID AND CharInventoryID = @CharInventoryID AND InSlotNumber = @InSlotNumber";

        // Lowest slot number with no item. 0..COUNT(*) always contains one.
        private const string FirstFreeSlot = @"SELECT s
                FROM generate_series(0, (SELECT COUNT(*) FROM CharInventoryItems
                        WHERE CustomerGUID = @CustomerGUID AND CharInventoryID = @CharInventoryID)::INT) AS s
                WHERE NOT EXISTS (SELECT 1 FROM CharInventoryItems
                        WHERE CustomerGUID = @CustomerGUID AND CharInventoryID = @CharInventoryID AND InSlotNumber = s)
                ORDER BY s
                LIMIT 1";

        private const string GetStoredStack = @"SELECT GuildStorageID, Quantity
                FROM GuildStorage
                WHERE CustomerGUID = @CustomerGUID AND GuildID = @GuildID AND ItemIDTag = @ItemIDTag
                  AND md5(CustomData) = md5(@CustomData) AND CustomData = @CustomData";

        private const string InsertStoredStack = @"INSERT INTO GuildStorage (CustomerGUID, GuildID, ItemIDTag, Quantity, CustomData)
                VALUES (@CustomerGUID, @GuildID, @ItemIDTag, @Quantity, @CustomData)";

        private const string SetStoredQuantity = @"UPDATE GuildStorage SET Quantity = @Quantity
                WHERE CustomerGUID = @CustomerGUID AND GuildStorageID = @GuildStorageID";

        private const string DeleteStoredStack = @"DELETE FROM GuildStorage
                WHERE CustomerGUID = @CustomerGUID AND GuildStorageID = @GuildStorageID";

        private const string GetStorageByGuildID = @"SELECT ItemIDTag, Quantity, CustomData
                FROM GuildStorage
                WHERE CustomerGUID = @CustomerGUID AND GuildID = @GuildID
                ORDER BY ItemIDTag, CustomData";

        private const string GetStorageByGuildGuid = @"SELECT S.ItemIDTag, S.Quantity, S.CustomData
                FROM GuildStorage S
                INNER JOIN Guild G ON G.CustomerGUID = S.CustomerGUID AND G.GuildID = S.GuildID
                WHERE G.CustomerGUID = @CustomerGUID AND G.GuildGuid = @GuildGuid
                ORDER BY S.ItemIDTag, S.CustomData";

        private const string GetMembers = @"SELECT C.CharName, GM.GuildRank
                FROM GuildMember GM
                INNER JOIN Characters C ON C.CustomerGUID = GM.CustomerGUID AND C.CharacterID = GM.CharacterID
                WHERE GM.CustomerGUID = @CustomerGUID AND GM.GuildID = @GuildID
                ORDER BY C.CharName";

        private readonly IOptions<StorageOptions> _storageOptions;

        public GuildStorageRepository(IOptions<StorageOptions> storageOptions)
        {
            _storageOptions = storageOptions;
        }

        private DbConnection CreateConnection() =>
            new NpgsqlConnection(PostgresConnectionString.WithPoolDefaults(_storageOptions.Value.OWSDBConnectionString));

        private sealed class SlotRow
        {
            public int InSlotNumber { get; set; }
            public int Quantity { get; set; }
        }

        private sealed class StoredRow
        {
            public long GuildStorageID { get; set; }
            public int Quantity { get; set; }
        }

        public async Task<List<GuildStorageEntry>> GetStoredItems(Guid customerGUID, Guid guildGuid)
        {
            await using DbConnection conn = CreateConnection();
            return (await conn.QueryAsync<GuildStorageEntry>(GetStorageByGuildGuid,
                new { CustomerGUID = customerGUID, GuildGuid = guildGuid }, commandType: CommandType.Text)).ToList();
        }

        public async Task<GuildStorageTransferResult> Transfer(Guid customerGUID, string characterName, Guid guildGuid, bool deposit,
            IReadOnlyList<GuildStorageTransferItem> items)
        {
            List<GuildStorageTransferItem> stacks = Normalize(items);
            if (stacks == null || string.IsNullOrWhiteSpace(characterName))
            {
                return Fail("bad_request");
            }

            await using DbConnection conn = CreateConnection();
            await conn.OpenAsync();
            await using DbTransaction tx = await conn.BeginTransactionAsync();
            try
            {
                int? characterID = await conn.QuerySingleOrDefaultAsync<int?>(LockCharacter,
                    new { CustomerGUID = customerGUID, CharName = characterName.Trim() },
                    transaction: tx, commandType: CommandType.Text);
                if (characterID == null)
                {
                    await tx.RollbackAsync();
                    return Fail("not_member");
                }

                int? guildID = await conn.QuerySingleOrDefaultAsync<int?>(LockMembership,
                    new { CustomerGUID = customerGUID, CharacterID = characterID.Value, GuildGuid = guildGuid },
                    transaction: tx, commandType: CommandType.Text);
                if (guildID == null)
                {
                    await tx.RollbackAsync();
                    return Fail("not_member");
                }

                int? inventoryID = await conn.QuerySingleOrDefaultAsync<int?>(GetInventoryID,
                    new { CustomerGUID = customerGUID, CharacterID = characterID.Value },
                    transaction: tx, commandType: CommandType.Text);
                if (inventoryID == null)
                {
                    await tx.RollbackAsync();
                    return Fail("no_inventory");
                }

                foreach (GuildStorageTransferItem stack in stacks)
                {
                    string refusal = deposit
                        ? await Deposit(conn, tx, customerGUID, guildID.Value, inventoryID.Value, stack)
                        : await Withdraw(conn, tx, customerGUID, guildID.Value, inventoryID.Value, stack);
                    if (refusal != null)
                    {
                        await tx.RollbackAsync();
                        return Fail(refusal);
                    }
                }

                // The bag changed outside the zone server's save, so a zone server on the revision protocol must see its
                // snapshot as stale rather than write the pre-transfer bag back over this.
                long newRevision = await conn.ExecuteScalarAsync<long>(PlayerShopQueries.BumpRevision,
                    new { CustomerGUID = customerGUID, CharacterID = characterID.Value },
                    transaction: tx, commandType: CommandType.Text);

                var result = new GuildStorageTransferResult
                {
                    Success = true,
                    NewEconomyRevision = newRevision,
                    StoredItems = (await conn.QueryAsync<GuildStorageEntry>(GetStorageByGuildID,
                        new { CustomerGUID = customerGUID, GuildID = guildID.Value },
                        transaction: tx, commandType: CommandType.Text)).ToList(),
                    Members = (await conn.QueryAsync<GuildStorageMember>(GetMembers,
                        new { CustomerGUID = customerGUID, GuildID = guildID.Value },
                        transaction: tx, commandType: CommandType.Text)).ToList()
                };

                await tx.CommitAsync();
                return result;
            }
            catch
            {
                try { await tx.RollbackAsync(); } catch { /* connection already gone */ }
                throw;
            }
        }

        // Bag -> storage. Takes from the matching stacks in slot order; refuses if the bag holds less than asked.
        private static async Task<string> Deposit(DbConnection conn, DbTransaction tx, Guid customerGUID, int guildID,
            int inventoryID, GuildStorageTransferItem stack)
        {
            List<SlotRow> slots = (await conn.QueryAsync<SlotRow>(GetBagStacks,
                new { CustomerGUID = customerGUID, CharInventoryID = inventoryID, stack.ItemIDTag, stack.CustomData },
                transaction: tx, commandType: CommandType.Text)).ToList();

            if (slots.Sum(s => (long)s.Quantity) < stack.Quantity)
            {
                return "insufficient_items";
            }

            StoredRow stored = await conn.QuerySingleOrDefaultAsync<StoredRow>(GetStoredStack,
                new { CustomerGUID = customerGUID, GuildID = guildID, stack.ItemIDTag, stack.CustomData },
                transaction: tx, commandType: CommandType.Text);
            long newStored = (stored?.Quantity ?? 0L) + stack.Quantity;
            if (newStored > int.MaxValue)
            {
                return "storage_full";
            }

            int remaining = stack.Quantity;
            foreach (SlotRow slot in slots)
            {
                if (remaining == 0)
                {
                    break;
                }

                int take = Math.Min(slot.Quantity, remaining);
                await conn.ExecuteAsync(take == slot.Quantity ? DeleteBagSlot : DebitBagSlot,
                    new { CustomerGUID = customerGUID, CharInventoryID = inventoryID, slot.InSlotNumber, Take = take },
                    transaction: tx, commandType: CommandType.Text);
                remaining -= take;
            }

            if (stored == null)
            {
                await conn.ExecuteAsync(InsertStoredStack,
                    new { CustomerGUID = customerGUID, GuildID = guildID, stack.ItemIDTag, stack.Quantity, stack.CustomData },
                    transaction: tx, commandType: CommandType.Text);
            }
            else
            {
                await conn.ExecuteAsync(SetStoredQuantity,
                    new { CustomerGUID = customerGUID, stored.GuildStorageID, Quantity = (int)newStored },
                    transaction: tx, commandType: CommandType.Text);
            }

            return null;
        }

        // Storage -> bag. Refuses if storage holds less than asked; the withdrawn stack goes into the lowest free slot.
        private static async Task<string> Withdraw(DbConnection conn, DbTransaction tx, Guid customerGUID, int guildID,
            int inventoryID, GuildStorageTransferItem stack)
        {
            StoredRow stored = await conn.QuerySingleOrDefaultAsync<StoredRow>(GetStoredStack,
                new { CustomerGUID = customerGUID, GuildID = guildID, stack.ItemIDTag, stack.CustomData },
                transaction: tx, commandType: CommandType.Text);
            if (stored == null || stored.Quantity < stack.Quantity)
            {
                return "insufficient_storage";
            }

            int left = stored.Quantity - stack.Quantity;
            await conn.ExecuteAsync(left == 0 ? DeleteStoredStack : SetStoredQuantity,
                new { CustomerGUID = customerGUID, stored.GuildStorageID, Quantity = left },
                transaction: tx, commandType: CommandType.Text);

            int slot = await conn.ExecuteScalarAsync<int>(FirstFreeSlot,
                new { CustomerGUID = customerGUID, CharInventoryID = inventoryID },
                transaction: tx, commandType: CommandType.Text);

            await conn.ExecuteAsync(PlayerShopQueries.InsertInventoryItem,
                new
                {
                    CustomerGUID = customerGUID,
                    CharInventoryID = inventoryID,
                    stack.ItemIDTag,
                    stack.Quantity,
                    InSlotNumber = slot,
                    stack.CustomData,
                    InstanceId = (Guid?)null
                },
                transaction: tx, commandType: CommandType.Text);

            return null;
        }

        /// <summary>
        /// Validates the request and merges entries for the same stack, so a list naming one stack twice is checked
        /// against its total. Null when the request is malformed.
        /// </summary>
        private static List<GuildStorageTransferItem> Normalize(IReadOnlyList<GuildStorageTransferItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return null;
            }

            var totals = new Dictionary<(string, string), long>();
            foreach (GuildStorageTransferItem item in items)
            {
                // ItemIDTag is VARCHAR(50) in both tables.
                if (item == null || item.Quantity <= 0 || string.IsNullOrWhiteSpace(item.ItemIDTag) || item.ItemIDTag.Length > 50)
                {
                    return null;
                }

                var key = (item.ItemIDTag, item.CustomData ?? "");
                totals[key] = totals.GetValueOrDefault(key) + item.Quantity;
                if (totals[key] > int.MaxValue)
                {
                    return null;
                }
            }

            return totals
                .OrderBy(t => t.Key.Item1, StringComparer.Ordinal)
                .ThenBy(t => t.Key.Item2, StringComparer.Ordinal)
                .Select(t => new GuildStorageTransferItem { ItemIDTag = t.Key.Item1, CustomData = t.Key.Item2, Quantity = (int)t.Value })
                .ToList();
        }

        private static GuildStorageTransferResult Fail(string reasonCode) =>
            new GuildStorageTransferResult { Success = false, ReasonCode = reasonCode };
    }
}
