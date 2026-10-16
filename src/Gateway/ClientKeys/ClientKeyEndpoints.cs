namespace Gateway.ClientKeys;

public static class ClientKeyEndpoints
{
    public record CreateClientKeyRequest(string? Name);

    public static void MapClientKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/client-keys");

        group.MapGet("", (ClientKeyStore store) => store.List());

        // The plaintext key is returned only here.
        group.MapPost("", (CreateClientKeyRequest? body, ClientKeyStore store) =>
        {
            var name = string.IsNullOrWhiteSpace(body?.Name) ? "default" : body.Name.Trim();
            var (key, plaintext) = store.Create(name);
            return Results.Created($"/api/client-keys/{key.Id}",
                new { key.Id, key.Name, key.Prefix, key.CreatedAt, Key = plaintext });
        });

        group.MapDelete("/{id:long}", (long id, ClientKeyStore store) =>
            store.Revoke(id) ? Results.NoContent() : Results.NotFound());
    }
}
