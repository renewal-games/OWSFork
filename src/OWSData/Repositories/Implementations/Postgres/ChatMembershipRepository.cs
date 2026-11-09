using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using OWSData.Repositories.Interfaces;
using OWSShared.Options;

namespace OWSData.Repositories.Implementations.Postgres
{
    public class ChatMembershipRepository : IChatMembershipRepository
    {
        private readonly IOptions<StorageOptions> _storageOptions;

        public ChatMembershipRepository(IOptions<StorageOptions> storageOptions)
        {
            _storageOptions = storageOptions;
        }

        public Task<IEnumerable<string>> GetPartyMembers(Guid customerGUID, string characterName) =>
            GetMembers(@"SELECT recipient.CharName
                FROM Characters sender
                INNER JOIN PartyMember membership ON membership.CustomerGUID = sender.CustomerGUID AND membership.CharacterID = sender.CharacterID
                INNER JOIN PartyMember peer ON peer.CustomerGUID = membership.CustomerGUID AND peer.PartyID = membership.PartyID
                INNER JOIN Characters recipient ON recipient.CustomerGUID = peer.CustomerGUID AND recipient.CharacterID = peer.CharacterID
                WHERE sender.CustomerGUID = @CustomerGUID AND LOWER(sender.CharName) = LOWER(@CharacterName)",
                customerGUID, characterName);

        public Task<IEnumerable<string>> GetGuildMembers(Guid customerGUID, string characterName) =>
            GetMembers(@"SELECT recipient.CharName
                FROM Characters sender
                INNER JOIN GuildMember membership ON membership.CustomerGUID = sender.CustomerGUID AND membership.CharacterID = sender.CharacterID
                INNER JOIN GuildMember peer ON peer.CustomerGUID = membership.CustomerGUID AND peer.GuildID = membership.GuildID
                INNER JOIN Characters recipient ON recipient.CustomerGUID = peer.CustomerGUID AND recipient.CharacterID = peer.CharacterID
                WHERE sender.CustomerGUID = @CustomerGUID AND LOWER(sender.CharName) = LOWER(@CharacterName)",
                customerGUID, characterName);

        private async Task<IEnumerable<string>> GetMembers(string sql, Guid customerGUID, string characterName)
        {
            await using var connection = new NpgsqlConnection(
                PostgresConnectionString.WithPoolDefaults(_storageOptions.Value.OWSDBConnectionString));
            return await connection.QueryAsync<string>(sql, new { CustomerGUID = customerGUID, CharacterName = characterName });
        }
    }
}
