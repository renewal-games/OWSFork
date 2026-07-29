using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using System;
using System.Threading.Tasks;

namespace OWSInstanceManagement.Requests.Instance
{
    public class CleanUpInstancesRequest
    {
        private Guid CustomerGUID;
        private ICharactersRepository _charactersRepository;

        public void SetData(ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid)
        {
            _charactersRepository = charactersRepository;
            CustomerGUID = customerGuid.CustomerGUID;
        }

        public async Task<IActionResult> Handle()
        {
            await _charactersRepository.CleanUpInstances(CustomerGUID);

            SuccessAndErrorMessage output = new SuccessAndErrorMessage
            {
                Success = true,
                ErrorMessage = ""
            };

            return new OkObjectResult(output);
        }
    }
}
