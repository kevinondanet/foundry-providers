using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Reflection-derived tools: the port of <c>parse_tool_info</c> (<c>tool/_tool_info.py</c>) over a method or
/// delegate, <c>tool_with</c> (<c>tool/_tool_with.py</c>), and the argument binding of <c>call_tool</c>
/// (<c>model/_call_tools.py</c>). Python reads the signature, type hints and the docstring; here the signature,
/// <see cref="JsonSchemaGenerator"/> and <see cref="DescriptionAttribute"/> on the method and its parameters play
/// those roles, and the output shape (<c>name</c>, <c>description</c>, <c>parameters</c> with properties in
/// signature order, <c>required</c> for parameters without defaults, <c>default</c> otherwise) is the same.
/// </summary>
public sealed partial record ToolDef
{
    private static readonly JsonSerializerOptions ArgumentOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// A tool over <paramref name="method"/>'s target method: parameters become the schema, the delegate runs the
    /// call. A lambda has a compiler-generated name, so <paramref name="name"/> is required for one.
    /// </summary>
    public static ToolDef FromMethod(Delegate method, string? name = null, string? description = null, bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(method);
        return FromMethod(method.Method, method.Target, name, description, parallel);
    }

    /// <summary>A tool over <paramref name="method"/> invoked on <paramref name="target"/> (null for static methods).</summary>
    public static ToolDef FromMethod(MethodInfo method, object? target = null, string? name = null, string? description = null, bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(method);
        var info = ParseToolInfo(method, name, description);
        var parameters = method.GetParameters();
        return new ToolDef(info.Name, info.Description, info.Parameters, (arguments, cancellationToken) => InvokeAsync(method, target, parameters, arguments, cancellationToken))
        {
            Parallel = parallel,
        };
    }

    /// <summary>
    /// Port of <c>parse_tool_info</c>: the tool's name (the method name unless overridden), description
    /// (<see cref="DescriptionAttribute"/> on the method, else empty, like a missing docstring) and parameters
    /// (<see cref="JsonSchemaGenerator.JsonSchemaOf(ParameterInfo)"/> per parameter, with its
    /// <see cref="DescriptionAttribute"/>; required when it has no default, otherwise <c>default</c> carries the
    /// default value — a null default is omitted like Python's <c>None</c>). <see cref="CancellationToken"/>
    /// parameters are not tool parameters.
    /// </summary>
    public static ToolInfo ParseToolInfo(MethodInfo method, string? name = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        name ??= method.Name;
        if (name.Contains('<') || name.Contains('>'))
        {
            throw new ArgumentException($"'{name}' is a compiler-generated method name; pass an explicit tool name for a lambda.", nameof(name));
        }

        description ??= method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        var properties = new Dictionary<string, ToolParam>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            var parameterName = parameter.Name ?? throw new ArgumentException($"Parameter {parameter.Position} of '{name}' has no name.", nameof(method));
            var schema = JsonSchemaGenerator.JsonSchemaOf(parameter);
            if (parameter.HasDefaultValue)
            {
                if (parameter.DefaultValue is { } defaultValue)
                {
                    schema = schema with { Default = JsonSchemaGenerator.ToJsonNode(defaultValue) };
                }
            }
            else
            {
                required.Add(parameterName);
            }

            if (parameter.GetCustomAttribute<DescriptionAttribute>()?.Description is { } parameterDescription)
            {
                schema = schema with { Description = parameterDescription };
            }

            properties[parameterName] = schema.ToToolParam();
        }

        return new ToolInfo(name, description) { Parameters = new ToolParams { Properties = properties, Required = required } };
    }

    /// <summary>
    /// Port of <c>tool_with</c>: the tool with a new name, description, parameter descriptions and/or parallel
    /// flag. Python modifies the tool in place; records are immutable so a modified copy is returned. A
    /// parameter name that the tool does not declare is an <see cref="ArgumentException"/> with Python's message.
    /// </summary>
    public static ToolDef ToolWith(
        ToolDef tool,
        string? name = null,
        string? description = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        bool? parallel = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var properties = new Dictionary<string, ToolParam>(tool.Parameters.Properties, StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var (parameterName, parameterDescription) in parameters)
            {
                if (!properties.TryGetValue(parameterName, out var existing))
                {
                    throw new ArgumentException(
                        $"tool_with error: no parameter named '{parameterName}' (valid parameters are {string.Join(", ", properties.Keys)})",
                        nameof(parameters));
                }

                properties[parameterName] = existing with { Description = parameterDescription };
            }
        }

        return tool with
        {
            Name = name ?? tool.Name,
            Description = description ?? tool.Description,
            Parameters = tool.Parameters with { Properties = properties },
            Parallel = parallel ?? tool.Parallel,
        };
    }

    /// <summary>
    /// Binds the JSON arguments to the parameters (System.Text.Json web defaults, enums by name), fills defaults,
    /// invokes and converts the result. A missing required argument or an unconvertible value is a
    /// <see cref="ToolParsingError"/> (Python's messages); exceptions from the method propagate unwrapped.
    /// </summary>
    private static async Task<ToolResult> InvokeAsync(MethodInfo method, object? target, ParameterInfo[] parameters, JsonObject arguments, CancellationToken cancellationToken)
    {
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                values[i] = cancellationToken;
                continue;
            }

            var parameterName = parameter.Name!;
            if (arguments.TryGetPropertyValue(parameterName, out var node))
            {
                values[i] = ConvertArgument(node, parameter);
            }
            else if (parameter.HasDefaultValue)
            {
                values[i] = parameter.DefaultValue ?? (parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null);
            }
            else
            {
                throw new ToolParsingError($"Required parameter {parameterName} not provided to tool call.");
            }
        }

        object? result;
        try
        {
            result = method.Invoke(target, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }

        return await ToToolResultAsync(result).ConfigureAwait(false);
    }

    private static object? ConvertArgument(JsonNode? node, ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (node is null)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                throw new ToolParsingError($"Unable to convert 'null' to {type.Name}");
            }

            return null;
        }

        try
        {
            return node.Deserialize(type, ArgumentOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ToolParsingError($"Unable to convert '{node.ToJsonString()}' to {type.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The port of the result handling in <c>call_tool</c>: awaits tasks, keeps a <see cref="ToolResult"/> or
    /// content list as is, and stringifies anything else (<c>str(result)</c>: bools as <c>True</c>/<c>False</c>,
    /// numbers invariant, other objects as JSON).
    /// </summary>
    private static async Task<ToolResult> ToToolResultAsync(object? result)
    {
        switch (result)
        {
            case Task task:
                await task.ConfigureAwait(false);
                result = TaskResult(task);
                break;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                result = null;
                break;
            default:
                if (result is not null && result.GetType() is { IsGenericType: true } valueTaskType && valueTaskType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTask = (Task)valueTaskType.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(result, null)!;
                    await asTask.ConfigureAwait(false);
                    result = TaskResult(asTask);
                }

                break;
        }

        return result switch
        {
            null => ToolResult.Empty,
            ToolResult toolResult => toolResult,
            string text => text,
            Content content => ToolResult.FromContents([content]),
            IEnumerable<Content> contents => ToolResult.FromContents(contents),
            bool flag => flag ? "True" : "False",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => JsonSerializer.Serialize(result, result.GetType(), ArgumentOptions),
        };
    }

    private static object? TaskResult(Task task)
    {
        var type = task.GetType();
        var resultProperty = type.GetProperty("Result");
        if (resultProperty is null || resultProperty.PropertyType.Name == "VoidTaskResult")
        {
            return null;
        }

        return resultProperty.GetValue(task);
    }
}
