using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using OWSData.Models;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSData.SQL;
using OWSShared.Options;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OWSData.Repositories.Implementations.Postgres
{
    public class ActionHouseRepository : IActionHouseRepository
    {
        private readonly IOptions<StorageOptions> _storageOptions;

        public ActionHouseRepository(IOptions<StorageOptions> storageOptions)
        {
            _storageOptions = storageOptions;
        }

        private IDbConnection Connection => new NpgsqlConnection(_storageOptions.Value.OWSDBConnectionString);

        public async Task<ActionHousePlayerContainer> GetActionHousePlayerItems(Guid customerGUID, string characterName)
        {
            ActionHousePlayerContainer result = new();

            try
            {
                using (var connection = Connection)
                {
                    var parameters = new DynamicParameters();
                    parameters.Add("CustomerGUID", customerGUID);
                    parameters.Add("CharacterName", characterName);

                    var queryResults = await connection.QueryAsync<ActionHousePlayerItem>(PostgresQueries.GetActionHousePlayerItems, parameters);

                    var firstRow = queryResults.FirstOrDefault();
                    if (firstRow != null)
                    {
                        result.ErrorMessage = firstRow.ErrorMessage;
                        result.SuccessMessage = firstRow.SuccessMessage;
                        result.Items = string.IsNullOrEmpty(result.ErrorMessage)
                            ? queryResults.Where(item => item.SlotIndex != 0)
                            : Enumerable.Empty<ActionHousePlayerItem>();
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        public async Task GetActionHouseItemSearch(Guid customerGUID, string ItemSearch)
        {
            throw new NotImplementedException();
        }
    }
}
