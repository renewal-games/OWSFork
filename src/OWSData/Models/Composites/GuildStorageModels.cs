using System.Collections.Generic;

namespace OWSData.Models.Composites
{
    /// <summary>One stack the caller asks to move. A stack is identified by ItemIDTag and CustomData together.</summary>
    public class GuildStorageTransferItem
    {
        public string ItemIDTag { get; set; }
        public int Quantity { get; set; }

        /// <summary>"" and null both mean "no custom data".</summary>
        public string CustomData { get; set; }
    }

    /// <summary>A stack held in guild storage.</summary>
    public class GuildStorageEntry
    {
        public string ItemIDTag { get; set; }
        public int Quantity { get; set; }
        public string CustomData { get; set; }
    }

    public class GuildStorageMember
    {
        public string CharName { get; set; }
        public int GuildRank { get; set; }
    }

    public class GuildStorageTransferResult
    {
        public bool Success { get; set; }

        /// <summary>
        /// "" on success. Otherwise nothing was written:
        /// "bad_request" (empty list, quantity &lt;= 0, missing item ID, totals past int range),
        /// "not_member" (the character is not in the named guild, or does not exist),
        /// "no_inventory" (the character has no bag row),
        /// "insufficient_items" (deposit: the bag holds less than asked),
        /// "insufficient_storage" (withdraw: guild storage holds less than asked),
        /// "storage_full" (deposit: the stored stack would pass int range).
        /// </summary>
        public string ReasonCode { get; set; } = "";

        /// <summary>The guild's storage after the transfer.</summary>
        public List<GuildStorageEntry> StoredItems { get; set; } = new List<GuildStorageEntry>();

        /// <summary>The guild's members at commit time; the only recipients of the storage update.</summary>
        public List<GuildStorageMember> Members { get; set; } = new List<GuildStorageMember>();

        /// <summary>The character's Characters.EconomyRevision after the transfer bumped it.</summary>
        public long NewEconomyRevision { get; set; }
    }
}
