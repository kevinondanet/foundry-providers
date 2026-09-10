using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

/// <summary>Port of <c>agent/_bridge/sandbox/types.py</c> <c>_BridgedToolId</c>: one bridged tool within the registry.</summary>
public readonly record struct BridgedToolId(string Server, string Tool);

/// <summary>
/// Port of the <c>PermissionError</c> raised by the bridge service's <c>call_tool</c>: a host tool call that no approved
/// execution grant matched while tool approval is active.
/// </summary>
public sealed class BridgedToolDeniedException(string message) : Exception(message);

/// <summary>
/// Port of the bridged-tools state of <c>agent/_bridge/sandbox/types.py</c> <c>SandboxAgentBridge</c>
/// (<c>bridged_tools</c>, the execution grants, <c>_candidate_functions</c>, <c>_resolve_bridged_tools</c> and
/// <c>_json_equal</c>) plus the registration of <c>sandbox/bridge.py</c> (<c>_register_bridged_tools</c>): the host tools
/// served at <c>/mcp/{server}</c> and the one-shot grants that let an approved call execute exactly once.
/// </summary>
/// <remarks>
/// A grant binds the exact (server, tool) an approved call's model-facing function name denotes and the approved
/// arguments; <see cref="ConsumeToolExecutionGrant"/> removes it when a matching MCP call arrives. Names are matched
/// as exact candidate strings computed from the registry (<see cref="CandidateFunctions"/>), never parsed out of a
/// call name, so an unknown naming scheme matches nothing (deny-safe). Handlers run concurrently, so the grant list is
/// guarded by a lock.
/// </remarks>
public sealed partial class BridgedToolRegistry
{
    /// <summary>Port of <c>_MAX_TOOL_EXECUTION_GRANTS</c>: unconsumed grants kept before the oldest is evicted.</summary>
    public const int MaxToolExecutionGrants = 1024;

    private readonly List<string> _servers = [];

    private readonly Dictionary<string, IReadOnlyDictionary<string, ToolDef>> _tools = new(StringComparer.Ordinal);

    private readonly object _grantsSync = new();

    private readonly LinkedList<Grant> _grants = new();

    /// <summary>A registry with no bridged tools.</summary>
    public static BridgedToolRegistry Empty { get; } = new([]);

    /// <summary>
    /// Registers <paramref name="specs"/> in order. Throws <see cref="ArgumentException"/> for a name not matching
    /// <see cref="BridgedToolsSpec.NamePattern"/>, a duplicate spec name (Python's message) or a duplicate tool name
    /// within a spec (Python keeps the last).
    /// </summary>
    public BridgedToolRegistry(IEnumerable<BridgedToolsSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        foreach (var spec in specs)
        {
            if (spec is null)
            {
                throw new ArgumentException("bridged_tools contains a null BridgedToolsSpec.");
            }

            if (spec.Name is null || !NameRegex().IsMatch(spec.Name) || spec.Name.EndsWith('\n'))
            {
                throw new ArgumentException($"Invalid bridged_tools name: '{spec.Name}'. A name must match {BridgedToolsSpec.NamePattern}.");
            }

            if (_tools.ContainsKey(spec.Name))
            {
                throw new ArgumentException($"Duplicate bridged_tools name: '{spec.Name}'. Each BridgedToolsSpec must have a unique name.");
            }

            if (spec.Tools is null)
            {
                throw new ArgumentException($"bridged_tools '{spec.Name}' has no tools list.");
            }

            var tools = new Dictionary<string, ToolDef>(StringComparer.Ordinal);
            foreach (var tool in spec.Tools)
            {
                if (tool is null)
                {
                    throw new ArgumentException($"bridged_tools '{spec.Name}' contains a null tool.");
                }

                if (!tools.TryAdd(tool.Name, tool))
                {
                    throw new ArgumentException($"Duplicate tool name '{tool.Name}' in bridged_tools '{spec.Name}'. Tool names must be unique within a BridgedToolsSpec.");
                }
            }

            _servers.Add(spec.Name);
            _tools[spec.Name] = tools;
        }
    }

    /// <summary>Server names in spec order.</summary>
    public IReadOnlyList<string> Servers => _servers;

    /// <summary>Number of bridged servers (specs); zero for <see cref="Empty"/>.</summary>
    public int Count => _servers.Count;

    /// <summary>Unconsumed execution grants (for diagnostics and tests).</summary>
    internal int GrantCount
    {
        get
        {
            lock (_grantsSync)
            {
                return _grants.Count;
            }
        }
    }

    /// <summary>The tools of <paramref name="server"/>, keyed by name in spec order.</summary>
    public bool TryGetServer(string server, [NotNullWhen(true)] out IReadOnlyDictionary<string, ToolDef>? tools)
    {
        ArgumentNullException.ThrowIfNull(server);
        return _tools.TryGetValue(server, out tools);
    }

    /// <summary>
    /// The MCP server configs a scaffold is given, in spec order (port of <c>_register_bridged_tools</c>'s return):
    /// <c>http</c> at <c>{baseUrl}/mcp/{name}</c> carrying <c>Authorization: Bearer {authToken}</c>, with all tools.
    /// </summary>
    public IReadOnlyList<McpServerConfigHttp> McpServerConfigs(string baseUrl, string authToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(authToken);
        var root = baseUrl.TrimEnd('/');
        return _servers
            .Select(name => new McpServerConfigHttp(
                "http",
                name,
                $"{root}/mcp/{Uri.EscapeDataString(name)}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {authToken}" }))
            .ToArray();
    }

    /// <summary>
    /// Port of <c>_candidate_functions</c>, extended: the exact names a scaffold may have declared a bridged tool as to
    /// its model, distinct and in order — the bare <paramref name="tool"/>, Claude Code's <c>mcp__{server}__{tool}</c>,
    /// Gemini CLI's <c>{server}__{tool}</c>, Copilot CLI's <c>{server}-{tool}</c>, and Codex's sanitized
    /// <c>mcp__{server}__{tool}</c> and <c>{server}__{tool}</c> (<see cref="SanitizeCodexName"/>).
    /// </summary>
    public static IReadOnlyList<string> CandidateFunctions(string server, string tool)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tool);
        var candidates = new List<string>(6);
        void Add(string candidate)
        {
            if (!candidates.Contains(candidate, StringComparer.Ordinal))
            {
                candidates.Add(candidate);
            }
        }

        Add(tool);
        Add($"mcp__{server}__{tool}");
        Add($"{server}__{tool}");
        Add($"{server}-{tool}");
        var sanitizedServer = SanitizeCodexName(server);
        var sanitizedTool = SanitizeCodexName(tool);
        Add($"mcp__{sanitizedServer}__{sanitizedTool}");
        Add($"{sanitizedServer}__{sanitizedTool}");
        return candidates;
    }

    /// <summary>Port of Codex's <c>sanitize_responses_api_tool_name</c>: every character outside <c>[A-Za-z0-9_]</c> becomes <c>_</c>.</summary>
    public static string SanitizeCodexName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? c : '_';
            }
        });
    }

    /// <summary>Port of <c>_resolve_bridged_tools</c>: every bridged (server, tool) whose candidates contain <paramref name="function"/>.</summary>
    public IReadOnlyList<BridgedToolId> Resolve(string function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var targets = new List<BridgedToolId>();
        foreach (var server in _servers)
        {
            foreach (var tool in _tools[server].Keys)
            {
                if (CandidateFunctions(server, tool).Contains(function, StringComparer.Ordinal))
                {
                    targets.Add(new BridgedToolId(server, tool));
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// Port of <c>register_tool_execution_grants</c> without its approval gate (the caller registers only when tool
    /// approval is active): one grant per call whose name denotes exactly one bridged tool, bound to a copy of its
    /// arguments. A name denoting several bridged tools registers nothing (fail closed, warned once); names denoting
    /// none are skipped. Past <see cref="MaxToolExecutionGrants"/> the oldest grant is evicted (warned once).
    /// </summary>
    public void RegisterToolExecutionGrants(IEnumerable<ToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        foreach (var call in calls)
        {
            if (call is null)
            {
                continue;
            }

            var targets = Resolve(call.Function);
            if (targets.Count == 0)
            {
                continue;
            }

            if (targets.Count > 1)
            {
                ProviderLogger.WarnOnce(
                    $"Approved tool call '{call.Function}' denotes more than one bridged tool; no execution grant registered "
                    + "(the call will be denied). Use unique tool names across bridged servers, or a qualified name "
                    + "('mcp__<server>__<tool>' or '<server>-<tool>').");
                continue;
            }

            var evicted = false;
            lock (_grantsSync)
            {
                if (_grants.Count >= MaxToolExecutionGrants)
                {
                    _grants.RemoveFirst();
                    evicted = true;
                }

                _grants.AddLast(new Grant(targets[0].Server, targets[0].Tool, call.Arguments.DeepClone().AsObject()));
            }

            if (evicted)
            {
                ProviderLogger.WarnOnce(
                    $"Bridged tool execution grants exceeded {MaxToolExecutionGrants}; evicting the oldest unconsumed grant. "
                    + "An approved-but-never-executed call that old can no longer be executed.");
            }
        }
    }

    /// <summary>
    /// Port of <c>consume_tool_execution_grant</c>: removes the first grant binding this exact (server, tool) whose
    /// arguments are <see cref="JsonEqual"/> to <paramref name="arguments"/>; false when none matches.
    /// </summary>
    public bool ConsumeToolExecutionGrant(string server, string tool, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        lock (_grantsSync)
        {
            for (var node = _grants.First; node is not null; node = node.Next)
            {
                var grant = node.Value;
                if (grant.Server == server && grant.Tool == tool && JsonEqual(grant.Arguments, arguments))
                {
                    _grants.Remove(node);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Port of <c>_json_equal</c>: equality by JSON semantics. Numbers compare by value (<c>5 == 5.0</c>), a boolean
    /// equals only a boolean (<c>true != 1</c>), objects need the same key set (any order), arrays compare in order,
    /// strings ordinally, and nulls are equal.
    /// </summary>
    public static bool JsonEqual(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (null, _) or (_, null):
                return false;
            case (JsonObject left, JsonObject right):
                if (left.Count != right.Count)
                {
                    return false;
                }

                foreach (var (key, value) in left)
                {
                    if (!right.TryGetPropertyValue(key, out var other) || !JsonEqual(value, other))
                    {
                        return false;
                    }
                }

                return true;
            case (JsonArray left, JsonArray right):
                if (left.Count != right.Count)
                {
                    return false;
                }

                for (var i = 0; i < left.Count; i++)
                {
                    if (!JsonEqual(left[i], right[i]))
                    {
                        return false;
                    }
                }

                return true;
            case (JsonValue left, JsonValue right):
                return ValueEqual(left, right);
            default:
                return false;
        }
    }

    private static bool ValueEqual(JsonValue left, JsonValue right)
    {
        var leftKind = left.GetValueKind();
        var rightKind = right.GetValueKind();
        switch (leftKind)
        {
            case JsonValueKind.True or JsonValueKind.False:
                return rightKind == leftKind;
            case JsonValueKind.Number when rightKind == JsonValueKind.Number:
                return NumberEqual(left.ToJsonString(), right.ToJsonString());
            case JsonValueKind.String when rightKind == JsonValueKind.String:
                return string.Equals(StringOf(left), StringOf(right), StringComparison.Ordinal);
            case JsonValueKind.Null:
                return rightKind == JsonValueKind.Null;
            default:
                return false;
        }
    }

    private static bool NumberEqual(string left, string right)
    {
        const NumberStyles style = NumberStyles.Float;
        if (decimal.TryParse(left, style, CultureInfo.InvariantCulture, out var leftDecimal)
            && decimal.TryParse(right, style, CultureInfo.InvariantCulture, out var rightDecimal))
        {
            return leftDecimal == rightDecimal;
        }

        return double.TryParse(left, style, CultureInfo.InvariantCulture, out var leftDouble)
            && double.TryParse(right, style, CultureInfo.InvariantCulture, out var rightDouble)
            && leftDouble.Equals(rightDouble);
    }

    private static string? StringOf(JsonValue value) =>
        value.TryGetValue<string>(out var text) ? text : JsonSerializer.Deserialize<string>(value.ToJsonString());

    [GeneratedRegex(BridgedToolsSpec.NamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    private sealed record Grant(string Server, string Tool, JsonObject Arguments);
}
