using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Gateway.Auth;

public static class AuthEndpoints
{
    public const string AdminName = "admin";
    /// <summary>Session claim holding <see cref="AdminCredentials.Stamp"/> at sign-in; changing the password ends other sessions.</summary>
    public const string StampClaim = "aigw:pw";

    /// <summary>/api paths reachable without a UI login.</summary>
    private static readonly string[] AnonymousApiPaths = ["/api/health", "/api/auth/login", "/api/auth/logout", "/api/auth/me"];

    public record LoginRequest(string? Password);
    public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest body, AdminCredentials creds, HttpContext ctx) =>
        {
            if (!creds.Verify(body.Password))
                return Results.Json(new { error = "invalid_password" }, statusCode: StatusCodes.Status401Unauthorized);

            await SignInAsync(ctx, creds);
            return Results.Ok(new { user = AdminName });
        });

        app.MapGet("/api/auth/password", (AdminCredentials creds) =>
            Results.Ok(new { source = creds.Source, changeable = creds.Source != "configured", minLength = AdminCredentials.MinLength }));

        app.MapPost("/api/auth/change-password", async (ChangePasswordRequest body, AdminCredentials creds, HttpContext ctx) =>
        {
            if (creds.Change(body.CurrentPassword, body.NewPassword) is { } error)
                return Results.Json(new { error }, statusCode: creds.Source == "configured" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
            // Keep this session; every other one carries the old stamp and is signed out.
            await SignInAsync(ctx, creds);
            return Results.NoContent();
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

    private static Task SignInAsync(HttpContext ctx, AdminCredentials creds)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, AdminName), new Claim(StampClaim, creds.Stamp)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>Rejects sessions signed in before the last password change.</summary>
    public static async Task ValidateStampAsync(CookieValidatePrincipalContext ctx)
    {
        var creds = ctx.HttpContext.RequestServices.GetRequiredService<AdminCredentials>();
        if (ctx.Principal?.FindFirst(StampClaim)?.Value == creds.Stamp) return;
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
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
