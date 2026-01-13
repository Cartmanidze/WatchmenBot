using System.Net.Http.Headers;
using System.Text;
using Hangfire.Dashboard;

namespace WatchmenBot.Infrastructure.Hangfire;

/// <summary>
/// Basic authentication filter for Hangfire Dashboard.
/// Protects the dashboard with username/password authentication.
/// </summary>
public class HangfireBasicAuthFilter(string username, string password) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();

        if (string.IsNullOrEmpty(authHeader))
        {
            SetUnauthorizedResponse(httpContext);
            return false;
        }

        try
        {
            var authHeaderValue = AuthenticationHeaderValue.Parse(authHeader);

            if (!"Basic".Equals(authHeaderValue.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                SetUnauthorizedResponse(httpContext);
                return false;
            }

            var credentialBytes = Convert.FromBase64String(authHeaderValue.Parameter ?? "");
            var credentials = Encoding.UTF8.GetString(credentialBytes).Split(':', 2);

            if (credentials.Length != 2)
            {
                SetUnauthorizedResponse(httpContext);
                return false;
            }

            var inputUsername = credentials[0];
            var inputPassword = credentials[1];

            if (inputUsername == username && inputPassword == password)
            {
                return true;
            }
        }
        catch
        {
            // Invalid auth header format
        }

        SetUnauthorizedResponse(httpContext);
        return false;
    }

    private static void SetUnauthorizedResponse(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = 401;
        httpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"Hangfire Dashboard\"";
    }
}