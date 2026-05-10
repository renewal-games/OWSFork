using System;
using System.Collections.Generic;

namespace OWSData.Models.Tables
{
    public partial class Party
    {
        public Guid CustomerGuid { get; set; }
        public int PartyID { get; set; }
        public Guid PartyGuid { get; set; }
        public bool bRaidingParty { get; set; }
        public string PartyName { get; set; }
        public string PartyDescription { get; set; }
        public bool PublicJoinEnabled { get; set; }
        public short ExpDistributionMode { get; set; }
        public short LootDistributionMode { get; set; }
        public int MaxMembers { get; set; }
        public bool IsTemporary { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DisbandedAt { get; set; }
    }
}
