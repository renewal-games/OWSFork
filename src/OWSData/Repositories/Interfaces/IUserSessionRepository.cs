using System;
using System.Threading.Tasks;
using OWSData.Models.StoredProcs;

namespace OWSData.Repositories.Interfaces
{
    public interface IUserSessionRepository
    {
        Task<GetUserSession> GetUserSession(Guid userGuid);
        Task SetUserSession(Guid userGuid, GetUserSession userSession);
    }
}
