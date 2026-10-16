using System.Threading.Channels;
using Gateway.Accounts;
using Gateway.Infrastructure;

namespace Gateway.Limits;

/// <summary>
/// Periodically pulls usage endpoints and reset credits of enabled OAuth accounts (every
/// <see cref="GatewayOptions.LimitsRefreshMinutes"/>; 0 disables, as in tests). <see cref="Trigger"/> queues one account,
/// e.g. right after it is linked.
/// </summary>
public sealed class LimitsRefreshService(
    LimitsService limits, AccountStore accounts, GatewayOptions options, ILogger<LimitsRefreshService> log) : BackgroundService
{
    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>();

    public bool Enabled => options.LimitsRefreshMinutes > 0;

    public void Trigger(long accountId)
    {
        if (Enabled) _queue.Writer.TryWrite(accountId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        var interval = TimeSpan.FromMinutes(options.LimitsRefreshMinutes);
        var next = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= next)
            {
                foreach (var a in accounts.List().Where(a => a.Enabled && ProviderUsageClient.SupportsUsageEndpoint(a)))
                    await RefreshOneAsync(a, stoppingToken);
                next = DateTimeOffset.UtcNow + interval;
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(next - DateTimeOffset.UtcNow is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero);
            try
            {
                var id = await _queue.Reader.ReadAsync(wait.Token);
                if (accounts.Get(id) is { } a) await RefreshOneAsync(a, stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RefreshOneAsync(LinkedAccount account, CancellationToken ct)
    {
        try { await limits.RefreshAsync(account, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Limit refresh for account {Account} failed", account.Id); }
    }
}
