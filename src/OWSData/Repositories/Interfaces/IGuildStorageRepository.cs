using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OWSData.Models.Composites;

namespace OWSData.Repositories.Interfaces
{
    /// <summary>
    /// Guild storage, persisted and moved only by the server. Postgres-only.
    /// </summary>
    public interface IGuildStorageRepository
    {
        /// <summary>The storage of the guild with this GUID; empty when it holds nothing or does not exist.</summary>
        Task<List<GuildStorageEntry>> GetStoredItems(Guid customerGUID, Guid guildGuid);

        /// <summary>
        /// Moves <paramref name="items"/> between <paramref name="characterName"/>'s bag and guild storage in one
        /// transaction: a deposit debits the bag and credits storage, a withdrawal the reverse. The character must be a
        /// member of the guild <paramref name="guildGuid"/>. All or nothing: any refusal writes nothing.
        /// </summary>
        Task<GuildStorageTransferResult> Transfer(Guid customerGUID, string characterName, Guid guildGuid, bool deposit,
            IReadOnlyList<GuildStorageTransferItem> items);
    }
}
