using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Api.Middleware;

public class RlsMiddleware
{
    private readonly RequestDelegate _next;

    public RlsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = context.User.FindFirstValue(ClaimTypes.Email)
            ?? context.User.FindFirst("email")?.Value
            ?? string.Empty;
        var isAdmin = context.User.HasClaim("is_admin", "true") ? "true" : "false";

        await db.Database.OpenConnectionAsync();

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.user_id', {userId ?? string.Empty}, false)");

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.is_admin', {isAdmin}, false)");

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.user_email', {email}, false)");

            await _next(context);
        }
        finally
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("RESET app.user_id; RESET app.is_admin; RESET app.user_email;");
            }
            catch
            {
                // Ignore cleanup failures; the connection is about to be returned/disposed.
            }

            await db.Database.CloseConnectionAsync();
        }
    }
}
