// ============================================================================
//  CROSS-CUTTING CONCERN 1 of 4: THE REGISTRY
//  Python: inspect_ai/_util/registry.py
//
//  Everything in Inspect that a user can name on the command line — tasks,
//  solvers, scorers, tools, model providers, sandboxes — is registered under
//  a string name and later *resolved* by that name. Registration happens as a
//  side effect of importing a module: the `@task`, `@solver`, `@scorer`,
//  `@tool` and `@modelapi` decorators all call into this one registry.
//
//  This is how the engine (layer 3) can run a task it has never imported, and
//  how the model layer (5) can construct a provider (6) it has never imported:
//  they ask the registry for a name. The registry is the only place where
//  "name -> object" is decided, and every layer uses it — hence cross-cutting.
//
//  C# has no import-time decorators, so the demo uses attributes on static
//  factory methods and scans the assembly once at start-up ("import time").
// ============================================================================
using System.Reflection;
using inspect_ai._util._async;
using inspect_ai._util.display;

namespace inspect_ai._util.registry;

/// <summary>The kinds of thing the registry knows about (Python: RegistryType).</summary>
public enum RegistryType { Task, Solver, Scorer, Tool, ModelApi }

/// <summary>
/// Base class for the decorator attributes. The public packages each expose
/// their own subclass (`[Task]`, `[Solver]`, `[Scorer]`, `[Tool]`, `[ModelApi]`)
/// so that authors never reference the underscore package directly — exactly
/// as `from inspect_ai import task` re-exports a function defined in `_util`.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public abstract class RegistryAttribute(RegistryType type, string name) : Attribute
{
    public RegistryType Type { get; } = type;
    public string Name { get; } = name;
}

internal static class Registry
{
    // A plain Dictionary with NO lock. Safe because every mutation and lookup
    // happens on the single event-loop thread (see EventLoop.cs). The assert
    // in each method makes that assumption executable.
    private static readonly Dictionary<(RegistryType Type, string Name), MethodInfo> Entries = new();

    /// <summary>
    /// "Import time": find every static method carrying a registry attribute
    /// and record it. In Python this is what happens when the CLI imports
    /// your task file — each decorator registers as the module loads.
    /// </summary>
    public static void RegisterAssembly(Assembly assembly)
    {
        SingleThreadEventLoop.AssertOnLoopThread("Registry");
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attribute = method.GetCustomAttribute<RegistryAttribute>();
                if (attribute is null) continue;
                Entries[(attribute.Type, attribute.Name)] = method;
                Display.Step("x  _util.registry", $"registered {attribute.Type,-8} '{attribute.Name}' -> {type.Namespace}.{type.Name}.{method.Name}");
            }
        }
    }

    /// <summary>
    /// Resolve a name and build the object by invoking its factory. Layers
    /// above call this with a string the user typed; they get back a fully
    /// constructed object without importing the module that defined it.
    /// </summary>
    public static T Create<T>(RegistryType type, string name, params object?[] args)
    {
        SingleThreadEventLoop.AssertOnLoopThread("Registry");
        if (!Entries.TryGetValue((type, name), out var factory))
            throw new KeyNotFoundException(
                $"No {type} named '{name}' is registered. Known: {string.Join(", ", Names(type))}");

        Display.Step("x  _util.registry", $"resolving {type} '{name}' -> invoking {factory.DeclaringType?.Name}.{factory.Name}");
        var created = factory.Invoke(null, args)
            ?? throw new InvalidOperationException($"Factory for {type} '{name}' returned null.");
        Display.Step("x  _util.registry", $"resolved {type} '{name}' -> {created.GetType().Name}");
        return (T)created;
    }

    /// <summary>All registered names of one kind (used by `inspect list`).</summary>
    public static IEnumerable<string> Names(RegistryType type)
        => Entries.Keys.Where(k => k.Type == type).Select(k => k.Name).OrderBy(n => n);
}
