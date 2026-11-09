using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OWSData.Repositories.Interfaces
{
    /// <summary>Current group recipients, including the sender; empty when the sender has no membership.</summary>
    public interface IChatMembershipRepository
    {
        Task<IEnumerable<string>> GetPartyMembers(Guid customerGUID, string characterName);
        Task<IEnumerable<string>> GetGuildMembers(Guid customerGUID, string characterName);
    }
}
