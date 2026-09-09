using System.Reflection;

namespace InspectAzureAI.Examples.Runner;

/// <summary>
/// Finds the <see cref="IExample"/> implementations of an assembly by reflection (public or internal, non-abstract,
/// with a parameterless constructor), so adding an example is adding a class: no registry edits. Names are matched
/// case-insensitively, with '/' and '\' treated alike ("bridge/langchain").
/// </summary>
public sealed class ExampleRegistry
{
    private readonly List<IExample> _examples;

    private ExampleRegistry(IEnumerable<IExample> examples)
    {
        _examples = examples.OrderBy(example => example.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var duplicates = _examples.GroupBy(example => Normalize(example.Name)).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"more than one example is named {string.Join(", ", duplicates)}");
        }
    }

    /// <summary>The examples of this assembly.</summary>
    public static ExampleRegistry Default { get; } = Discover(typeof(ExampleRegistry).Assembly);

    /// <summary>All examples, sorted by name.</summary>
    public IReadOnlyList<IExample> Examples => _examples;

    /// <summary>Discovers the examples of <paramref name="assemblies"/>.</summary>
    public static ExampleRegistry Discover(params IReadOnlyList<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var examples = new List<IExample>();
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition || !typeof(IExample).IsAssignableFrom(type))
                {
                    continue;
                }

                var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
                if (constructor is null)
                {
                    continue;
                }

                examples.Add((IExample)constructor.Invoke(null));
            }
        }

        return new ExampleRegistry(examples);
    }

    /// <summary>A registry of the given instances (for tests).</summary>
    public static ExampleRegistry Of(params IReadOnlyList<IExample> examples) => new(examples);

    /// <summary>The example named <paramref name="name"/>, or null.</summary>
    public IExample? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var wanted = Normalize(name);
        return _examples.FirstOrDefault(example => Normalize(example.Name) == wanted);
    }

    /// <summary>The example named <paramref name="name"/>; an unknown name is an <see cref="ArgumentException"/> listing the known ones.</summary>
    public IExample Get(string name) =>
        Find(name) ?? throw new ArgumentException($"unknown example '{name}' (known examples: {string.Join(", ", _examples.Select(example => example.Name))})");

    private static string Normalize(string name) => name.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
