using Gateway.Accounts;
using Gateway.Auth;
using Gateway.Bridge;
using Gateway.ClientKeys;
using Gateway.Infrastructure;
using Gateway.Limits;
using Gateway.Live;
using Gateway.Proxy;
using Gateway.Usage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

var options = GatewayOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(options.DataDir);
builder.WebHost.UseUrls($"http://{options.BindAddress}:{options.Port}");
// Agent requests carry whole conversations; allow large bodies.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 256L * 1024 * 1024);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<AdminCredentials>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddGatewayProxy();
builder.Services.AddUsageAnalytics();
builder.Services.AddLimitsAndRouting();
builder.Services.AddBridge();
builder.Services.AddLiveUpdates();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDir, "keys")))
    .SetApplicationName("AiGateway");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "aigw_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = AuthEndpoints.ValidateStampAsync;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.Services.GetRequiredService<Database>().Migrate();
// Corrected or newly configured prices apply to stored history too.
var repriced = app.Services.GetRequiredService<Gateway.Usage.PricingTable>().RepriceIfChanged(app.Services.GetRequiredService<Database>());
if (repriced > 0) app.Logger.LogInformation("Recomputed the cost of {Rows} recorded requests for the current price table", repriced);
app.Services.GetRequiredService<AdminCredentials>();

app.UseAuthentication();
app.UseApiLoginRequirement();
app.UseAuthorization();

var webFiles = Directory.Exists(options.WebRoot)
    ? (IFileProvider)new PhysicalFileProvider(options.WebRoot)
    : new NullFileProvider();
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapClientKeyEndpoints();
app.MapProxyEndpoints();
app.MapBridgeEndpoints();
app.MapAnalyticsEndpoints();
app.MapLimitsEndpoints();
app.MapLiveUpdates();

// SPA fallback for client-side routes; API and proxy prefixes never fall back to the UI.
app.MapFallback(async ctx =>
{
    var path = ctx.Request.Path;
    var index = webFiles.GetFileInfo("index.html");
    if (path.StartsWithSegments("/api") || path.StartsWithSegments("/anthropic") || path.StartsWithSegments("/openai")
        || !HttpMethods.IsGet(ctx.Request.Method) || !index.Exists)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(index);
});

app.Run();

public partial class Program;
