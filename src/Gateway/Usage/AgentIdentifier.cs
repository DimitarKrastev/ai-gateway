using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Gateway.Infrastructure;
using Gateway.Proxy;

namespace Gateway.Usage;

public sealed record AgentIdentity(string Id, string Role);

/// <summary>
/// Attributes generation requests to agents within a session.
/// <list type="bullet">
/// <item>Codex: one agent per session, or <c>subagent:&lt;kind&gt;</c> when the request carries <c>x-openai-subagent</c>.</item>
/// <item>Claude Code and others: an agent instance is identified by its system prompt prefix plus its first user message
/// prefix (both stay constant over an agent's life; parallel subagents differ in their task prompt). Role: requests
/// without tools are <c>utility</c> calls (titles, summaries); an agent offered the <c>Task</c>/<c>Agent</c> tool is
/// <c>main</c> (subagents cannot spawn subagents); otherwise the first tool-using agent of a session is <c>main</c> and later
/// ones are <c>subagent</c>.</item>
/// </list>
/// </summary>
public sealed class AgentIdentifier(Database db)
{
    private static readonly HashSet<string> SpawnTools = new(StringComparer.Ordinal) { "Task", "Agent" };

    private sealed class SessionAgents
    {
        public readonly Dictionary<string, string> Roles = new();
        public bool HasMain;
    }

    private readonly ConcurrentDictionary<string, SessionAgents> _sessions = new();

    public AgentIdentity? Identify(ProxiedRequestContext ctx, RequestInfo info)
    {
        if (!Endpoints.IsGeneration(info.Endpoint)) return null;
        var sessionPrefix = $"{ctx.Provider}:{ctx.SessionId}";

        if (info.Client == "codex")
        {
            var role = info.SubagentHint is { } hint ? $"subagent:{hint}" : "main";
            return new AgentIdentity($"{sessionPrefix}:{role}", role);
        }

        if (info.ToolNames.Count == 0)
            return new AgentIdentity($"{sessionPrefix}:utility", "utility");

        var key = Hash(info.SystemPrefix + "\u0000" + info.FirstUserPrefix);
        var id = $"{sessionPrefix}:{key}";
        var state = _sessions.GetOrAdd(sessionPrefix, _ => Load(ctx.Provider, ctx.SessionId));
        lock (state)
        {
            if (state.Roles.TryGetValue(id, out var known)) return new AgentIdentity(id, known);
            var role = info.ToolNames.Any(SpawnTools.Contains) || !state.HasMain ? "main" : "subagent";
            if (role == "main") state.HasMain = true;
            state.Roles[id] = role;
            return new AgentIdentity(id, role);
        }
    }

    /// <summary>Rebuilds a session's known agents from history so roles survive restarts.</summary>
    private SessionAgents Load(string provider, string sessionId)
    {
        var state = new SessionAgents();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, agent_role FROM requests
            WHERE provider = $p AND session_id = $s AND agent_id IS NOT NULL AND agent_role IN ('main', 'subagent')
            GROUP BY agent_id;
            """;
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$s", sessionId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            state.Roles[r.GetString(0)] = r.GetString(1);
            state.HasMain |= r.GetString(1) == "main";
        }
        return state;
    }

    private static string Hash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];
}
