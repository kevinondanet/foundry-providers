namespace InspectAzureAI.Eval.Tasks;

/// <summary>
/// Port of the <c>@task</c> decorator for assembly-hosted tasks: marks a <c>public static</c> method that returns an
/// <see cref="EvalTask"/> so the <c>inspectai</c> CLI can discover it by name over the loaded assemblies. The method's
/// parameters are the task arguments (<c>-T name=value</c>), bound by name; the <see cref="Name"/> defaults to the
/// snake_case form of the method name (<c>HelloWorld</c> is <c>hello_world</c>), matching Python's function-name default. <see cref="Attribs"/> are the decorator's keyword attributes
/// (<c>@task(light=True)</c>), given as <c>key=value</c> strings and parsed like <c>-T</c> values; <c>inspect list
/// tasks -F light=true</c> filters on them.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TaskAttribute : Attribute
{
    /// <summary>Marks a task named after its method.</summary>
    public TaskAttribute()
        : this(null)
    {
    }

    /// <summary>Marks a task with an explicit <paramref name="name"/> (null keeps the method name) and <c>key=value</c> <paramref name="attribs"/>.</summary>
    public TaskAttribute(string? name, params string[] attribs)
    {
        Name = name;
        Attribs = attribs ?? [];
    }

    /// <summary>The task name; null means the snake_case method name.</summary>
    public string? Name { get; }

    /// <summary>The task attributes as <c>key=value</c> strings (Python's <c>@task(**attribs)</c>).</summary>
    public IReadOnlyList<string> Attribs { get; }
}
