using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using InspectAzureAI.Cli.Args;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Registry;

/// <summary>Port of <c>TaskInfo</c> (<c>_eval/task/task.py</c>): where a task lives, its name and its <c>@task</c> attributes.</summary>
public sealed record TaskInfo(string File, string Name, IReadOnlyDictionary<string, object?> Attribs)
{
    /// <summary>The <c>[Task]</c> method that builds the task.</summary>
    public required MethodInfo Method { get; init; }

    /// <summary>The assembly the method lives in.</summary>
    public Assembly Assembly => Method.DeclaringType!.Assembly;

    public override string ToString() => $"{File}@{Name}";
}

/// <summary>
/// Port of <c>_eval/list.py</c> <c>list_tasks</c> and the task-loading half of <c>_eval/loader.py</c> for assembly-hosted
/// tasks: discovers <see cref="TaskAttribute"/> methods over the loaded assemblies (plus any <c>--assembly</c> paths),
/// resolves a task spec (<c>name</c> or <c>file@name</c>, Python's <c>path@task</c>) and builds the
/// <see cref="EvalTask"/> with the <c>-T</c> arguments bound to the method's parameters.
/// </summary>
public sealed class TaskRegistry
{
    private static readonly string[] SkippedAssemblyPrefixes = ["System.", "Microsoft.", "netstandard", "mscorlib", "xunit", "Azure.", "Newtonsoft.", "testhost", "Humanizer"];

    private TaskRegistry(IReadOnlyList<TaskInfo> tasks)
    {
        Tasks = tasks;
    }

    /// <summary>Every discovered task, sorted by <c>file@name</c> as <c>list_tasks</c> sorts.</summary>
    public IReadOnlyList<TaskInfo> Tasks { get; }

    /// <summary>
    /// Loads each of <paramref name="assemblyPaths"/> into the default load context (a missing or non-managed file is a
    /// <see cref="PrerequisiteError"/>) and scans it together with the assemblies already loaded in the process
    /// (framework and test-host assemblies are skipped). <paramref name="absolute"/> reports absolute assembly paths.
    /// </summary>
    public static TaskRegistry Discover(IEnumerable<string>? assemblyPaths = null, bool absolute = false, IEnumerable<Assembly>? assemblies = null)
    {
        var scan = new List<Assembly>();
        foreach (var path in assemblyPaths ?? [])
        {
            scan.Add(LoadAssembly(path));
        }

        foreach (var assembly in assemblies ?? AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || scan.Contains(assembly))
            {
                continue;
            }

            var name = assembly.GetName().Name ?? "";
            if (SkippedAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            scan.Add(assembly);
        }

        var tasks = new List<TaskInfo>();
        foreach (var assembly in scan)
        {
            var file = TaskFile(assembly, absolute);
            foreach (var type in TypesOf(assembly))
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<TaskAttribute>() is not { } attribute)
                    {
                        continue;
                    }

                    if (method.ReturnType != typeof(EvalTask))
                    {
                        throw new PrerequisiteError($"[Task] method {type.FullName}.{method.Name} must return {nameof(EvalTask)} (it returns {method.ReturnType.Name}).");
                    }

                    tasks.Add(new TaskInfo(file, attribute.Name ?? ParameterBinder.SnakeCase(method.Name), CliArgs.ParseCliArgs(attribute.Attribs)) { Method = method });
                }
            }
        }

        return new TaskRegistry(tasks.OrderBy(task => task.ToString(), StringComparer.Ordinal).ToList());
    }

    /// <summary>The tasks that pass <paramref name="filter"/> (all of them when null), sorted.</summary>
    public IReadOnlyList<TaskInfo> List(Func<TaskInfo, bool>? filter = null) =>
        Tasks.Where(task => filter is null || filter(task)).ToList();

    /// <summary>
    /// Port of the <c>-F</c> filter of <c>inspect list tasks</c>: <c>name=value</c> keeps tasks whose attribute equals the
    /// (YAML-parsed) value, <c>name~=value</c> keeps those whose attribute differs; every filter must pass.
    /// </summary>
    public static Func<TaskInfo, bool> AttribFilter(IEnumerable<string>? filters)
    {
        var parsed = CliArgs.ParseCliArgs(filters);
        return task =>
        {
            foreach (var (rawName, value) in parsed)
            {
                var negate = rawName.EndsWith('~');
                var name = negate ? rawName[..^1] : rawName;
                var actual = task.Attribs.TryGetValue(name, out var found) ? found : null;
                var equal = ValuesEqual(actual, value);
                if (negate ? equal : !equal)
                {
                    return false;
                }
            }

            return true;
        };
    }

    /// <summary>
    /// Resolves a task spec: a bare name (unique across the discovered tasks) or <c>file@name</c> where <c>file</c> is
    /// the assembly path or its simple name. No match, or an ambiguous bare name, is a <see cref="PrerequisiteError"/>.
    /// </summary>
    public TaskInfo Resolve(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        var at = spec.LastIndexOf('@');
        List<TaskInfo> matches;
        if (at > 0)
        {
            var file = spec[..at];
            var name = spec[(at + 1)..];
            matches = Tasks.Where(task => task.Name == name && FileMatches(task, file)).ToList();
        }
        else
        {
            matches = Tasks.Where(task => task.Name == spec).ToList();
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count == 0)
        {
            var available = Tasks.Count == 0 ? "(none discovered; pass --assembly to load a task assembly)" : string.Join(", ", Tasks.Select(task => task.ToString()));
            throw new PrerequisiteError($"Task '{spec}' not found. Available tasks: {available}");
        }

        throw new PrerequisiteError($"Task '{spec}' is ambiguous; use file@name to choose one of: {string.Join(", ", matches.Select(task => task.ToString()))}");
    }

    /// <summary>Builds the task: binds <paramref name="taskArgs"/> to the method (see <see cref="ParameterBinder"/>), invokes it, and records the args as <see cref="EvalTask.TaskArgs"/> when the task did not set its own.</summary>
    public static EvalTask Create(TaskInfo task, IReadOnlyDictionary<string, object?>? taskArgs = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var args = taskArgs ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        var values = ParameterBinder.Bind(task.Method, args, $"task '{task.Name}'");
        EvalTask? created;
        try
        {
            created = (EvalTask?)task.Method.Invoke(null, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        if (created is null)
        {
            throw new PrerequisiteError($"Task '{task.Name}' returned null.");
        }

        return created.TaskArgs is null && args.Count > 0 ? created with { TaskArgs = args } : created;
    }

    /// <summary>The task file Python reports: the assembly path relative to the working directory (or absolute).</summary>
    public static string TaskFile(Assembly assembly, bool absolute)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var location = assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            return (assembly.GetName().Name ?? "assembly") + ".dll";
        }

        return absolute ? location : Path.GetRelativePath(Directory.GetCurrentDirectory(), location);
    }

    internal static bool ValuesEqual(object? left, object? right)
    {
        switch (left, right)
        {
            case (null, null):
                return true;
            case (null, _) or (_, null):
                return false;
            case (bool a, bool b):
                return a == b;
            case (string a, string b):
                return string.Equals(a, b, StringComparison.Ordinal);
            case (IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b):
                return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && ValuesEqual(pair.Value, other));
            case (IEnumerable<object?> a, IEnumerable<object?> b):
                return a.Zip(b, ValuesEqual).All(equal => equal) && a.Count() == b.Count();
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Convert.ToDouble(left, System.Globalization.CultureInfo.InvariantCulture) == Convert.ToDouble(right, System.Globalization.CultureInfo.InvariantCulture);
        }

        return false;
    }

    private static bool IsNumber(object value) => value is long or int or double or float or decimal or System.Numerics.BigInteger;

    private static bool FileMatches(TaskInfo task, string file)
    {
        var location = task.Assembly.Location;
        var simpleName = task.Assembly.GetName().Name ?? "";
        var wanted = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        if (string.Equals(wanted, simpleName, StringComparison.OrdinalIgnoreCase) && !file.Contains('/', StringComparison.Ordinal) && !file.Contains('\\', StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrEmpty(location))
        {
            return string.Equals(file, task.File, StringComparison.Ordinal);
        }

        return string.Equals(Path.GetFullPath(file), Path.GetFullPath(location), StringComparison.Ordinal)
            || string.Equals(file, task.File, StringComparison.Ordinal);
    }

    private static Assembly LoadAssembly(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new PrerequisiteError($"Assembly '{path}' does not exist.");
        }

        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
        }
        catch (BadImageFormatException ex)
        {
            throw new PrerequisiteError($"'{path}' is not a .NET assembly: {ex.Message}");
        }
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
