namespace Gateway.Fakes;

/// <summary>
/// Control API for tests and e2e runs ({provider} = anthropic | openai):
/// <list type="bullet">
/// <item>GET/POST <c>/fake-admin/{provider}/accounts</c> — list / register (body: <see cref="FakeAccountSpec"/>)</item>
/// <item>GET/PATCH/DELETE <c>/fake-admin/{provider}/accounts/{id}</c> — inspect / change limits, exhaustion, resets, tokens</item>
/// <item>POST <c>/fake-admin/{provider}/accounts/{id}/expire-token</c> — make the access token expired</item>
/// <item>GET/DELETE <c>/fake-admin/{provider}/requests</c> — recorded requests (filters: path, accountId, sinceSeq)</item>
/// <item>POST <c>/fake-admin/{provider}/reset</c>, POST <c>/fake-admin/reset</c> — clear state</item>
/// </list>
/// </summary>
public static class FakeAdmin
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/fake-admin");
        g.MapPost("/reset", (FakeStates s) => { s.Anthropic.Clear(); s.OpenAI.Clear(); return Results.Ok(new { ok = true }); });

        g.MapGet("/{provider}/accounts", (string provider, FakeStates s) =>
            s.ByName(provider) is { } st ? Results.Ok(st.Accounts.Select(a => Snapshot(st, a))) : Results.NotFound());

        g.MapPost("/{provider}/accounts", (string provider, FakeAccountSpec spec, FakeStates s) =>
        {
            if (s.ByName(provider) is not { } st) return Results.NotFound();
            if (spec.Id != null && st.Get(spec.Id) != null) return Results.Conflict(new { error = "account exists" });
            return Results.Ok(Snapshot(st, st.Register(spec)));
        });

        g.MapGet("/{provider}/accounts/{id}", (string provider, string id, FakeStates s) =>
            s.ByName(provider) is { } st && st.Get(id) is { } a ? Results.Ok(Snapshot(st, a)) : Results.NotFound());

        g.MapPatch("/{provider}/accounts/{id}", (string provider, string id, FakeAccountSpec spec, FakeStates s) =>
            s.ByName(provider) is { } st && st.Patch(id, spec) is { } a ? Results.Ok(Snapshot(st, a)) : Results.NotFound());

        g.MapPost("/{provider}/accounts/{id}/expire-token", (string provider, string id, FakeStates s) =>
            s.ByName(provider) is { } st && st.ExpireAccessToken(id) ? Results.Ok(Snapshot(st, st.Get(id)!)) : Results.NotFound());

        g.MapGet("/{provider}/requests", (string provider, string? path, string? accountId, long? sinceSeq, FakeStates s) =>
        {
            if (s.ByName(provider) is not { } st) return Results.NotFound();
            var q = st.Requests.AsEnumerable();
            if (path != null) q = q.Where(r => r.Path.StartsWith(path, StringComparison.Ordinal));
            if (accountId != null) q = q.Where(r => r.AccountId == accountId);
            if (sinceSeq != null) q = q.Where(r => r.Seq > sinceSeq);
            return Results.Ok(q.ToList());
        });

        g.MapDelete("/{provider}/requests", (string provider, FakeStates s) =>
        {
            if (s.ByName(provider) is not { } st) return Results.NotFound();
            st.ClearRequests();
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/{provider}/reset", (string provider, FakeStates s) =>
        {
            if (s.ByName(provider) is not { } st) return Results.NotFound();
            st.Clear();
            return Results.Ok(new { ok = true });
        });
    }

    public static object Snapshot(FakeProviderState state, FakeAccount a)
    {
        lock (state.Gate)
        {
            state.RollLocked(a);
            return new
            {
                a.Id,
                Kind = a.Kind == FakeAuthKind.ApiKey ? "api_key" : "oauth",
                a.ApiKey, a.AccessToken, a.RefreshToken, a.AccessTokenExpiresAt, a.ChatGptAccountId, a.PlanType,
                Windows = a.Windows.Select(w => new { w.Name, w.Utilization, w.WindowMinutes, w.ResetsAt, w.IncrementPerRequest }).ToList(),
                Resets = a.Resets.Select(r => new { r.Id, r.ExpiresAt }).ToList(),
                Exhausted = state.BlockingWindowLocked(a) != null,
                a.ForceExhausted, a.RequestsLimit, a.RequestsUsed, a.TokensLimit, a.TokensUsed, a.RequestCount, a.RedeemedResets,
            };
        }
    }
}
