using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OWSShared;

namespace OWSCharacterPersistence.Controllers
{
    /// <summary>
    /// The X-Samsara-Service-Key check (<see cref="SamsaraServiceKeyCheck"/>) for a controller's request.
    /// </summary>
    internal static class SamsaraServiceKey
    {
        public static bool IsValid(HttpRequest request)
        {
            request.Headers.TryGetValue(SamsaraServiceKeyCheck.Header, out var provided);
            return SamsaraServiceKeyCheck.IsValid(provided.ToString());
        }
    }

    /// <summary>
    /// Refuses every action of the controller with 401 unless the request carries the service key. Runs before model
    /// binding, so an unauthenticated body is never read.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class RequireSamsaraServiceKeyAttribute : Attribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (!SamsaraServiceKey.IsValid(context.HttpContext.Request))
            {
                context.Result = new UnauthorizedResult();
            }
        }
    }
}
