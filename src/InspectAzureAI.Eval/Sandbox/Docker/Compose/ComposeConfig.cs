using System.Globalization;
using System.Text;

namespace InspectAzureAI.Eval.Sandbox.Docker.Compose;

/// <summary>
/// Port of <c>util/_sandbox/compose.py</c> <c>ComposeConfig</c> / <c>ComposeService</c> (the typed view of
/// a compose file the docker provider inspects) read by <see cref="MiniYaml"/> instead of the
/// <c>docker compose config</c> round trip of <c>compose_services</c>. Only the keys the provider acts on
/// are typed; every other key of a service, network or the file is kept verbatim in an <c>Extra</c>
/// dictionary, and the compose CLI always receives the original file, so nothing is lost in between.
/// </summary>
public sealed class ComposeConfig
{
    private ComposeConfig(
        OrderedDictionary<string, ComposeService> services,
        OrderedDictionary<string, ComposeNetwork> networks,
        OrderedDictionary<string, object?> volumes,
        OrderedDictionary<string, object?> extra)
    {
        Services = services;
        Networks = networks;
        Volumes = volumes;
        Extra = extra;
    }

    /// <summary>Service definitions in file order.</summary>
    public IReadOnlyDictionary<string, ComposeService> Services { get; }

    /// <summary>Top-level <c>networks</c> (empty when absent).</summary>
    public IReadOnlyDictionary<string, ComposeNetwork> Networks { get; }

    /// <summary>Top-level <c>volumes</c>, untyped (empty when absent).</summary>
    public IReadOnlyDictionary<string, object?> Volumes { get; }

    /// <summary>Every other top-level key (<c>name</c>, <c>x-*</c>, <c>configs</c>, ...), untyped.</summary>
    public IReadOnlyDictionary<string, object?> Extra { get; }

    /// <summary>
    /// Port of the default-service rule of <c>docker.py</c> <c>sample_init</c>: the service marked
    /// <c>x-default: true</c>, else the one named <c>default</c>. Deviation: a file with exactly one service
    /// makes that service the default too, rather than failing with Python's "No 'default' service" error.
    /// </summary>
    public string DefaultService =>
        TryGetDefaultService(out var name)
            ? name
            : throw new InvalidOperationException(
                "No 'default' service found in Docker compose file. You should either name a service 'default' or add 'x-default: true' to one of your service definitions.");

    /// <summary>See <see cref="DefaultService"/>.</summary>
    public bool TryGetDefaultService(out string name)
    {
        foreach (var (service, definition) in Services)
        {
            if (definition.XDefault)
            {
                name = service;
                return true;
            }
        }

        if (Services.ContainsKey("default"))
        {
            name = "default";
            return true;
        }

        if (Services.Count == 1)
        {
            name = Services.Keys.First();
            return true;
        }

        name = "";
        return false;
    }

    /// <summary>Reads a compose file.</summary>
    public static ComposeConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Error reading docker compose file '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>Parses compose YAML.</summary>
    public static ComposeConfig Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var root = MiniYaml.ParseMapping(yaml);
        var services = new OrderedDictionary<string, ComposeService>(StringComparer.Ordinal);
        var networks = new OrderedDictionary<string, ComposeNetwork>(StringComparer.Ordinal);
        var volumes = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var extra = new OrderedDictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in root)
        {
            switch (key)
            {
                case "services":
                    if (value is not OrderedDictionary<string, object?> serviceMap)
                    {
                        throw new InvalidOperationException("The compose file's 'services' key must be a mapping of service definitions.");
                    }

                    foreach (var (name, definition) in serviceMap)
                    {
                        services[name] = ComposeService.FromNode(name, definition);
                    }

                    break;
                case "networks":
                    foreach (var (name, definition) in ComposeValues.Mapping(value))
                    {
                        networks[name] = ComposeNetwork.FromNode(definition);
                    }

                    break;
                case "volumes":
                    foreach (var (name, definition) in ComposeValues.Mapping(value))
                    {
                        volumes[name] = definition;
                    }

                    break;
                default:
                    extra[key] = value;
                    break;
            }
        }

        if (services.Count == 0)
        {
            throw new InvalidOperationException("The compose file defines no services (a 'services' mapping with at least one entry is required).");
        }

        return new ComposeConfig(services, networks, volumes, extra);
    }
}

/// <summary>Port of <c>ComposeService</c>: one service of a compose file (typed keys plus <see cref="Extra"/>).</summary>
public sealed class ComposeService
{
    private ComposeService(string name)
    {
        Name = name;
    }

    /// <summary>The service's key under <c>services</c>.</summary>
    public string Name { get; }

    public string? Image { get; private init; }

    public ComposeBuild? Build { get; private init; }

    public string? ContainerName { get; private init; }

    public bool? Init { get; private init; }

    public bool? Privileged { get; private init; }

    /// <summary>Short-syntax port strings (long-syntax entries are rendered as <c>[ip:]published:target[/protocol]</c>).</summary>
    public IReadOnlyList<string> Ports { get; private init; } = [];

    /// <summary>Names of the networks the service joins (a mapping's keys, or the list entries).</summary>
    public IReadOnlyList<string> Networks { get; private init; } = [];

    public string? NetworkMode { get; private init; }

    public IReadOnlyList<string> Volumes { get; private init; } = [];

    /// <summary>Environment as name → value (a list entry without <c>=</c> maps to null, as compose does).</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; private init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    public string? WorkingDir { get; private init; }

    public string? User { get; private init; }

    /// <summary>argv form (a string command is shell-split the way compose does).</summary>
    public IReadOnlyList<string>? Command { get; private init; }

    public IReadOnlyList<string>? Entrypoint { get; private init; }

    public ComposeDeploy? Deploy { get; private init; }

    /// <summary>The <c>cpus</c> shortcut (a number in the file; kept as its textual form, e.g. "1.0").</summary>
    public string? Cpus { get; private init; }

    /// <summary>The <c>mem_limit</c> shortcut (e.g. "2.0gb").</summary>
    public string? MemLimit { get; private init; }

    public ComposeHealthcheck? Healthcheck { get; private init; }

    /// <summary>Service names from <c>depends_on</c> (a mapping's keys, or the list entries).</summary>
    public IReadOnlyList<string> DependsOn { get; private init; } = [];

    /// <summary><c>x-default: true</c> marks the sandbox's default service.</summary>
    public bool XDefault { get; private init; }

    /// <summary><c>x-local: true</c> marks an image that is never pulled.</summary>
    public bool XLocal { get; private init; }

    /// <summary>Every key not typed above, verbatim.</summary>
    public IReadOnlyDictionary<string, object?> Extra { get; private init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>The effective CPU limit: <c>deploy.resources.limits.cpus</c>, else the <c>cpus</c> shortcut.</summary>
    public string? CpuLimit => Deploy?.Limits?.Cpus ?? Cpus;

    /// <summary>The effective memory limit: <c>deploy.resources.limits.memory</c>, else the <c>mem_limit</c> shortcut.</summary>
    public string? MemoryLimit => Deploy?.Limits?.Memory ?? MemLimit;

    internal static ComposeService FromNode(string name, object? node)
    {
        if (node is null)
        {
            return new ComposeService(name);
        }

        if (node is not OrderedDictionary<string, object?> map)
        {
            throw new InvalidOperationException($"Service '{name}' must be a mapping.");
        }

        string? image = null, containerName = null, networkMode = null, workingDir = null, user = null, cpus = null, memLimit = null;
        ComposeBuild? build = null;
        bool? init = null, privileged = null;
        var xDefault = false;
        var xLocal = false;
        IReadOnlyList<string> ports = [], networks = [], volumes = [], dependsOn = [];
        IReadOnlyList<string>? command = null, entrypoint = null;
        IReadOnlyDictionary<string, string?> environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        ComposeDeploy? deploy = null;
        ComposeHealthcheck? healthcheck = null;
        var extra = new OrderedDictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "image":
                    image = ComposeValues.Text(value);
                    break;
                case "build":
                    build = ComposeBuild.FromNode(value);
                    break;
                case "container_name":
                    containerName = ComposeValues.Text(value);
                    break;
                case "init":
                    init = ComposeValues.Flag(value);
                    break;
                case "privileged":
                    privileged = ComposeValues.Flag(value);
                    break;
                case "ports":
                    ports = ComposeValues.List(value).Select(ComposeValues.PortText).ToArray();
                    break;
                case "networks":
                    networks = ComposeValues.Names(value);
                    break;
                case "network_mode":
                    networkMode = ComposeValues.Text(value);
                    break;
                case "volumes":
                    volumes = ComposeValues.List(value).Select(ComposeValues.VolumeText).ToArray();
                    break;
                case "environment":
                    environment = ComposeValues.Environment(value);
                    break;
                case "working_dir":
                    workingDir = ComposeValues.Text(value);
                    break;
                case "user":
                    user = ComposeValues.Text(value);
                    break;
                case "command":
                    command = ComposeValues.Argv(value);
                    break;
                case "entrypoint":
                    entrypoint = ComposeValues.Argv(value);
                    break;
                case "deploy":
                    deploy = ComposeDeploy.FromNode(value);
                    break;
                case "cpus":
                    cpus = ComposeValues.Text(value);
                    break;
                case "mem_limit":
                    memLimit = ComposeValues.Text(value);
                    break;
                case "healthcheck":
                    healthcheck = ComposeHealthcheck.FromNode(value);
                    break;
                case "depends_on":
                    dependsOn = ComposeValues.Names(value);
                    break;
                case "x-default":
                    xDefault = ComposeValues.Flag(value) ?? false;
                    break;
                case "x-local":
                    xLocal = ComposeValues.Flag(value) ?? false;
                    break;
                default:
                    extra[key] = value;
                    break;
            }
        }

        return new ComposeService(name)
        {
            Image = image,
            Build = build,
            ContainerName = containerName,
            Init = init,
            Privileged = privileged,
            Ports = ports,
            Networks = networks,
            NetworkMode = networkMode,
            Volumes = volumes,
            Environment = environment,
            WorkingDir = workingDir,
            User = user,
            Command = command,
            Entrypoint = entrypoint,
            Deploy = deploy,
            Cpus = cpus,
            MemLimit = memLimit,
            Healthcheck = healthcheck,
            DependsOn = dependsOn,
            XDefault = xDefault,
            XLocal = xLocal,
            Extra = extra,
        };
    }
}

/// <summary>Port of <c>ComposeBuild</c>: <c>build: .</c> or <c>build: {context, dockerfile, ...}</c>.</summary>
public sealed record ComposeBuild(string Context, string? Dockerfile = null, IReadOnlyDictionary<string, object?>? Extra = null)
{
    internal static ComposeBuild FromNode(object? node)
    {
        if (node is OrderedDictionary<string, object?> map)
        {
            string context = ".";
            string? dockerfile = null;
            var extra = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in map)
            {
                switch (key)
                {
                    case "context":
                        context = ComposeValues.Text(value) ?? ".";
                        break;
                    case "dockerfile":
                        dockerfile = ComposeValues.Text(value);
                        break;
                    default:
                        extra[key] = value;
                        break;
                }
            }

            return new ComposeBuild(context, dockerfile, extra);
        }

        return new ComposeBuild(ComposeValues.Text(node) ?? ".");
    }
}

/// <summary>Port of <c>ComposeHealthcheck</c>; durations stay as compose strings (see <see cref="ComposeHealthchecks.ParseDuration"/>).</summary>
public sealed record ComposeHealthcheck(
    IReadOnlyList<string>? Test = null,
    string? Interval = null,
    string? Timeout = null,
    long? Retries = null,
    string? StartPeriod = null,
    bool? Disable = null,
    IReadOnlyDictionary<string, object?>? Extra = null)
{
    internal static ComposeHealthcheck FromNode(object? node)
    {
        if (node is not OrderedDictionary<string, object?> map)
        {
            return new ComposeHealthcheck();
        }

        IReadOnlyList<string>? test = null;
        string? interval = null, timeout = null, startPeriod = null;
        long? retries = null;
        bool? disable = null;
        var extra = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "test":
                    test = ComposeValues.Argv(value);
                    break;
                case "interval":
                    interval = ComposeValues.Text(value);
                    break;
                case "timeout":
                    timeout = ComposeValues.Text(value);
                    break;
                case "retries":
                    retries = value switch
                    {
                        long l => l,
                        double d => (long)d,
                        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                        _ => null,
                    };
                    break;
                case "start_period":
                    startPeriod = ComposeValues.Text(value);
                    break;
                case "disable":
                    disable = ComposeValues.Flag(value);
                    break;
                default:
                    extra[key] = value;
                    break;
            }
        }

        return new ComposeHealthcheck(test, interval, timeout, retries, startPeriod, disable, extra);
    }
}

/// <summary>Port of <c>ComposeResources</c> limits / reservations.</summary>
public sealed record ComposeResourceLimits(string? Cpus = null, string? Memory = null, string? Pids = null, IReadOnlyDictionary<string, object?>? Extra = null)
{
    internal static ComposeResourceLimits? FromNode(object? node)
    {
        if (node is not OrderedDictionary<string, object?> map)
        {
            return null;
        }

        string? cpus = null, memory = null, pids = null;
        var extra = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "cpus":
                    cpus = ComposeValues.Text(value);
                    break;
                case "memory":
                    memory = ComposeValues.Text(value);
                    break;
                case "pids":
                    pids = ComposeValues.Text(value);
                    break;
                default:
                    extra[key] = value;
                    break;
            }
        }

        return new ComposeResourceLimits(cpus, memory, pids, extra);
    }
}

/// <summary>Port of <c>ComposeDeploy</c>: <c>deploy.resources.{limits,reservations}</c>.</summary>
public sealed record ComposeDeploy(ComposeResourceLimits? Limits = null, ComposeResourceLimits? Reservations = null, IReadOnlyDictionary<string, object?>? Extra = null)
{
    internal static ComposeDeploy? FromNode(object? node)
    {
        if (node is not OrderedDictionary<string, object?> map)
        {
            return null;
        }

        ComposeResourceLimits? limits = null, reservations = null;
        var extra = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            if (key == "resources" && value is OrderedDictionary<string, object?> resources)
            {
                foreach (var (resourceKey, resourceValue) in resources)
                {
                    switch (resourceKey)
                    {
                        case "limits":
                            limits = ComposeResourceLimits.FromNode(resourceValue);
                            break;
                        case "reservations":
                            reservations = ComposeResourceLimits.FromNode(resourceValue);
                            break;
                        default:
                            extra[$"resources.{resourceKey}"] = resourceValue;
                            break;
                    }
                }
            }
            else
            {
                extra[key] = value;
            }
        }

        return new ComposeDeploy(limits, reservations, extra);
    }
}

/// <summary>A top-level network: <c>internal: true</c> keeps the sandbox off the Internet while its services still see each other.</summary>
public sealed record ComposeNetwork(bool? Internal = null, string? Driver = null, IReadOnlyDictionary<string, object?>? Extra = null)
{
    internal static ComposeNetwork FromNode(object? node)
    {
        if (node is not OrderedDictionary<string, object?> map)
        {
            return new ComposeNetwork();
        }

        bool? internalNetwork = null;
        string? driver = null;
        var extra = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            switch (key)
            {
                case "internal":
                    internalNetwork = ComposeValues.Flag(value);
                    break;
                case "driver":
                    driver = ComposeValues.Text(value);
                    break;
                default:
                    extra[key] = value;
                    break;
            }
        }

        return new ComposeNetwork(internalNetwork, driver, extra);
    }
}

/// <summary>Conversions from the <see cref="MiniYaml"/> tree to the compose model's field types.</summary>
internal static class ComposeValues
{
    /// <summary>A scalar as text (a YAML float renders in its shortest round-trip form, so <c>cpus: 1.0</c> reads as "1").</summary>
    public static string? Text(object? node) => node switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Expected a scalar, got a {Describe(node)}."),
    };

    public static bool? Flag(object? node) => node switch
    {
        null => null,
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        string s => throw new InvalidOperationException($"Expected a boolean, got '{s}'."),
        long l => l != 0,
        _ => throw new InvalidOperationException($"Expected a boolean, got a {Describe(node)}."),
    };

    public static IReadOnlyList<object?> List(object? node) => node switch
    {
        null => [],
        List<object?> list => list,
        OrderedDictionary<string, object?> => throw new InvalidOperationException("Expected a list, got a mapping."),
        _ => [node],
    };

    public static IEnumerable<KeyValuePair<string, object?>> Mapping(object? node) => node switch
    {
        null => [],
        OrderedDictionary<string, object?> map => map,
        _ => throw new InvalidOperationException($"Expected a mapping, got a {Describe(node)}."),
    };

    /// <summary>A list of names, or a mapping's keys (<c>networks</c>, <c>depends_on</c>).</summary>
    public static IReadOnlyList<string> Names(object? node) => node switch
    {
        null => [],
        OrderedDictionary<string, object?> map => map.Keys.ToArray(),
        List<object?> list => list.Select(item => Text(item) ?? "").ToArray(),
        _ => [Text(node) ?? ""],
    };

    /// <summary>List form (<c>K=V</c> / <c>K</c>) or mapping form of <c>environment</c>.</summary>
    public static IReadOnlyDictionary<string, string?> Environment(object? node)
    {
        var result = new OrderedDictionary<string, string?>(StringComparer.Ordinal);
        switch (node)
        {
            case null:
                break;
            case OrderedDictionary<string, object?> map:
                foreach (var (key, value) in map)
                {
                    result[key] = Text(value);
                }

                break;
            case List<object?> list:
                foreach (var item in list)
                {
                    var entry = Text(item) ?? "";
                    var equals = entry.IndexOf('=', StringComparison.Ordinal);
                    if (equals < 0)
                    {
                        result[entry] = null;
                    }
                    else
                    {
                        result[entry[..equals]] = entry[(equals + 1)..];
                    }
                }

                break;
            default:
                throw new InvalidOperationException($"Expected a list or mapping for 'environment', got a {Describe(node)}.");
        }

        return result;
    }

    /// <summary>A command in argv form: a list stays as-is, a string is split the way compose (shlex) does.</summary>
    public static IReadOnlyList<string>? Argv(object? node) => node switch
    {
        null => null,
        List<object?> list => list.Select(item => Text(item) ?? "").ToArray(),
        _ => ShellSplit(Text(node) ?? ""),
    };

    public static string PortText(object? node)
    {
        if (node is OrderedDictionary<string, object?> map)
        {
            var target = Text(map.GetValueOrDefault("target"));
            var published = Text(map.GetValueOrDefault("published"));
            var hostIp = Text(map.GetValueOrDefault("host_ip"));
            var protocol = Text(map.GetValueOrDefault("protocol"));
            var sb = new StringBuilder();
            if (hostIp is not null)
            {
                sb.Append(hostIp).Append(':');
            }

            if (published is not null || hostIp is not null)
            {
                sb.Append(published).Append(':');
            }

            sb.Append(target);
            if (protocol is not null)
            {
                sb.Append('/').Append(protocol);
            }

            return sb.ToString();
        }

        return Text(node) ?? "";
    }

    public static string VolumeText(object? node)
    {
        if (node is OrderedDictionary<string, object?> map)
        {
            var source = Text(map.GetValueOrDefault("source"));
            var target = Text(map.GetValueOrDefault("target"));
            var readOnly = Flag(map.GetValueOrDefault("read_only")) ?? false;
            return (source is null ? target : $"{source}:{target}") + (readOnly ? ":ro" : "");
        }

        return Text(node) ?? "";
    }

    /// <summary>POSIX-style word splitting (quotes and backslashes), as <c>shlex.split</c> does for a string command.</summary>
    public static IReadOnlyList<string> ShellSplit(string command)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        var quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = '\0';
                }
                else if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\' or '$' or '`')
                {
                    current.Append(command[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    inWord = false;
                }

                continue;
            }

            inWord = true;
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '\\' when i + 1 < command.Length:
                    current.Append(command[++i]);
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (quote != '\0')
        {
            throw new InvalidOperationException($"Unterminated quote in command: {command}");
        }

        if (inWord)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static string Describe(object node) => node switch
    {
        List<object?> => "list",
        OrderedDictionary<string, object?> => "mapping",
        _ => node.GetType().Name,
    };
}
