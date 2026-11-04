using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Sokoni.Controllers;

/// <summary>Requires X-Admin-Key. Fine for a pilot; swap for proper login before real rollout.</summary>
public sealed class AdminKeyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var o = context.HttpContext.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
        if (!KeyMatches(o.AdminKey, context.HttpContext.Request.Headers["X-Admin-Key"]))
            context.Result = new UnauthorizedResult();
    }

    internal static bool KeyMatches(string expected, string? supplied) =>
        !string.IsNullOrEmpty(expected) && supplied is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
}

/// <summary>Dev endpoints exist only when Security:DevEnabled is true, and still need X-Dev-Key.</summary>
public sealed class DevOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var o = context.HttpContext.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
        if (!o.DevEnabled) { context.Result = new NotFoundResult(); return; }
        if (!AdminKeyAttribute.KeyMatches(o.DevKey, context.HttpContext.Request.Headers["X-Dev-Key"]))
            context.Result = new UnauthorizedResult();
    }
}
