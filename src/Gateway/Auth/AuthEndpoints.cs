using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Gateway.Auth;

public static class AuthEndpoints
{
    public const string AdminName = "admin";

    /// <summary>/api paths reachable without a UI login.</summary>
    private static readonly string[] AnonymousApiPaths = ["/api/health", "/api/auth/login", "/api/auth/logout", "/api/auth/me"];

    public record LoginRequest(string? Password);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest body, AdminCredentials creds, HttpContext ctx) =>
        {
            if (!creds.Verify(body.Password))
                return Results.Json(new { error = "invalid_password" }, statusCode: StatusCodes.Status401Unauthorized);

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, AdminName)], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Ok(new { user = AdminName });
        });

        app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
            ctx.User.Identity?.IsAuthenticated == true
                ? Results.Ok(new { user = ctx.User.Identity.Name })
                : Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized));
    }

    /// <summary>Every /api endpoint except the anonymous ones requires the admin cookie.</summary>
    public static IApplicationBuilder UseApiLoginRequirement(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api")
                && !AnonymousApiPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase))
                && ctx.User.Identity?.IsAuthenticated != true)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthenticated" });
                return;
            }
            await next();
        });
}
