using System.Net;

namespace Gateway.Fakes;

/// <summary>
/// Standalone fake provider server (Anthropic + OpenAI on one port) for end-to-end runs.
/// Port: FAKES_PORT (default 6202). It must lie inside AUX_PORTS (e.g. "6202-6219") when that variable is set;
/// Kestrel refuses to start if the port is already taken, so nothing else is ever disturbed.
/// Explicit Main (not top-level statements) so this assembly does not export a global <c>Program</c> type
/// that would clash with the gateway's in the test project.
/// </summary>
internal static class FakesProgram
{
    public static int Main(string[] args)
    {
        var portText = Environment.GetEnvironmentVariable("FAKES_PORT") ?? "6202";
        if (!int.TryParse(portText, out var port)) { Console.Error.WriteLine($"Invalid FAKES_PORT '{portText}'"); return 1; }
        var aux = Environment.GetEnvironmentVariable("AUX_PORTS");
        if (!string.IsNullOrEmpty(aux))
        {
            var parts = aux.Split('-', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi) && (port < lo || port > hi))
            {
                Console.Error.WriteLine($"FAKES_PORT {port} is outside AUX_PORTS {aux}");
                return 1;
            }
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddFakeProviders(o =>
        {
            o.AutoRegister = builder.Configuration.GetValue("Fakes:AutoRegister", true);
            o.StreamChunkDelayMs = builder.Configuration.GetValue("Fakes:StreamChunkDelayMs", 0);
            o.TokensPerSecond = builder.Configuration.GetValue("Fakes:TokensPerSecond", 0.0);
            o.FastSpeedFactor = builder.Configuration.GetValue("Fakes:FastSpeedFactor", 2.5);
        });
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        app.UseFakeProviders();
        app.Run();
        return 0;
    }
}
