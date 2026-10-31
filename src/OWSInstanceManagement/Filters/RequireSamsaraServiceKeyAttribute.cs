using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OWSShared;

namespace OWSInstanceManagement.Filters
{
    /// <summary>
    /// Rejects a request without the X-Samsara-Service-Key (<see cref="SamsaraServiceKeyCheck"/>). X-CustomerGUID is not
    /// a secret (the launcher ships it in config), so on its own it let anyone who could reach this service spin up,
    /// shut down or reap zone instances. Callers: the instance launcher, OWSPublicAPI and OWSManagement.
    /// </summary>
    public sealed class RequireSamsaraServiceKeyAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            context.HttpContext.Request.Headers.TryGetValue(SamsaraServiceKeyCheck.Header, out var provided);
            if (!SamsaraServiceKeyCheck.IsValid(provided.ToString()))
            {
                context.Result = new UnauthorizedResult();
            }
        }
    }
}
