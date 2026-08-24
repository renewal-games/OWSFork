using OWSData.Models.Tables;
using OWSData.Repositories.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OWSManagement.Requests.Users
{
    public class SearchUsersRequest
    {
        private readonly Guid _customerGuid;
        private readonly string _searchText;
        private readonly IUsersRepository _usersRepository;

        public SearchUsersRequest(Guid customerGuid, string searchText, IUsersRepository usersRepository)
        {
            _customerGuid = customerGuid;
            _searchText = searchText;
            _usersRepository = usersRepository;
        }

        public async Task<IEnumerable<User>> Handle()
        {
            return await _usersRepository.SearchUsers(_customerGuid, _searchText);
        }
    }
}
