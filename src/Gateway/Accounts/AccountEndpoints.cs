using Gateway.Limits;

namespace Gateway.Accounts;

public static class AccountEndpoints
{
    /// <summary>API view of a linked account: secrets are reduced to a masked hint.</summary>
    public sealed record AccountView(
        long Id, string Name, string Provider, string Kind, bool Enabled, int Priority,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string CredentialHint, bool HasRefreshToken,
        DateTimeOffset? AccessTokenExpiresAt, AccountLimitsView? Limits = null);

    public static AccountView ToView(LinkedAccount a) => new(
        a.Id, a.Name, a.Provider, a.Kind, a.Enabled, a.Priority, a.CreatedAt, a.UpdatedAt,
        a.Credentials.Hint(), a.Credentials.RefreshToken != null, a.IsOAuth ? a.Credentials.ExpiresAt : null);

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/accounts");

        group.MapGet("", (AccountStore store, LimitsService limits) =>
        {
            var list = store.List();
            var views = limits.Views(list.Select(a => a.Id).ToList());
            return list.Select(a => ToView(a) with { Limits = views[a.Id] });
        });

        group.MapGet("/{id:long}", (long id, AccountStore store, LimitsService limits) =>
            store.Get(id) is { } a ? Results.Ok(ToView(a) with { Limits = limits.View(id) }) : Results.NotFound());

        group.MapPost("", (AccountInput input, AccountStore store, LimitsRefreshService refresh) =>
        {
            var parsed = CredentialParser.Parse(input.Kind, input);
            if (parsed.Error != null) return Results.BadRequest(new { error = parsed.Error });
            var name = string.IsNullOrWhiteSpace(input.Name) ? DefaultName(parsed.Kind!) : input.Name.Trim();
            var account = store.Create(name, parsed.Kind!, input.Enabled ?? true, input.Priority ?? 100, parsed.Credentials!);
            refresh.Trigger(account.Id);
            return Results.Created($"/api/accounts/{account.Id}", ToView(account));
        });

        group.MapPatch("/{id:long}", (long id, AccountInput input, AccountStore store) =>
        {
            var existing = store.Get(id);
            if (existing == null) return Results.NotFound();
            if (input.Kind != null && AccountKinds.ProviderOf(input.Kind) != existing.Provider)
                return Results.BadRequest(new { error = "An account cannot change provider." });

            AccountCredentials? credentials = null;
            string? kind = null;
            if (input.HasSecrets)
            {
                var parsed = CredentialParser.Parse(input.Kind ?? existing.Kind, input);
                if (parsed.Error != null) return Results.BadRequest(new { error = parsed.Error });
                if (AccountKinds.ProviderOf(parsed.Kind) != existing.Provider)
                    return Results.BadRequest(new { error = "An account cannot change provider." });
                credentials = parsed.Credentials;
                kind = parsed.Kind;
            }
            else if (input.Kind != null && input.Kind != existing.Kind)
                return Results.BadRequest(new { error = "Changing the kind requires new credentials." });

            var name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim();
            var updated = store.Update(id, name, input.Enabled, input.Priority, kind, credentials);
            return updated is null ? Results.NotFound() : Results.Ok(ToView(updated));
        });

        group.MapDelete("/{id:long}", (long id, AccountStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());
    }

    private static string DefaultName(string kind) => kind switch
    {
        AccountKinds.AnthropicApiKey => "Anthropic API key",
        AccountKinds.AnthropicOAuth => "Claude subscription",
        AccountKinds.OpenAIApiKey => "OpenAI API key",
        _ => "ChatGPT subscription",
    };
}
