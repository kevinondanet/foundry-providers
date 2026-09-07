using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.Eval.Context;

/// <summary>Raised when a store value cannot be presented as the type a <see cref="StoreModel"/> property declares.</summary>
public sealed class StoreModelException : Exception
{
    public StoreModelException(string message)
        : base(message)
    {
    }

    public StoreModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Port of <c>util/_store_model.py</c> <c>StoreModel</c>: a typed view over a <see cref="Store"/>. Every property
/// reads and writes the store under the key <c>{TypeName}:{instance}:{Property}</c> (<c>{TypeName}:{Property}</c>
/// without an instance), so two models bound to one store share state and the values appear in the log's store.
/// Properties are declared as <c>public int X { get => Get(5); set => Set(value); }</c>: the argument to
/// <see cref="Get{T}(T, string)"/> is the field default. Binding (<see cref="Create{TModel}"/>, Python's
/// <c>model_post_init</c>) writes the default of every field the store lacks; afterwards a read of a key that was
/// deleted from the store returns the model's own last known value (Python's <c>__dict__</c>) without re-adding it.
/// A value read from a log or a replay (plain lists, dictionaries, longs) is coerced to the property type through
/// a JSON round trip and written back, like Python's <c>TypeAdapter</c> coercion; an impossible coercion is a
/// <see cref="StoreModelException"/> where Python silently returns the raw value.
/// </summary>
public abstract class StoreModel
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    private Store? _store;

    private string? _instance;

    private PropertyInfo[] _fields = [];

    private bool _binding;

    /// <summary>The bound store (an <see cref="InvalidOperationException"/> before <see cref="Create{TModel}"/> binds one).</summary>
    public Store Store => _store ?? throw new InvalidOperationException(
        $"{GetType().Name} is not bound to a store; create it with store.As<{GetType().Name}>(), StoreModel.Create<{GetType().Name}>(store), Store.StoreAs<{GetType().Name}>() or TaskState.StoreAs<{GetType().Name}>().");

    /// <summary>Port of <c>StoreModel.instance</c>: the namespace that lets several instances of one model type share a sample.</summary>
    public string? Instance => _instance;

    /// <summary>Port of <c>model_cls(store=store, instance=instance)</c>: binds a new model and initialises the keys the store lacks with the property defaults.</summary>
    public static TModel Create<TModel>(Store store, string? instance = null) where TModel : StoreModel, new()
    {
        ArgumentNullException.ThrowIfNull(store);
        var model = new TModel();
        model.Bind(store, instance);
        return model;
    }

    /// <summary>
    /// Port of <c>_ns_name</c>: the store key of the property <paramref name="name"/> for this model and instance. The
    /// property name is written in snake_case (<c>MyModel:my_field</c>), the form Python's field names take, so keys
    /// match logs written by either side and <see cref="StoreReplay.StoreFromEventsAs{TModel}"/> can read Python logs.
    /// </summary>
    public string NamespacedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var ns = _instance is null ? "" : _instance + ":";
        return $"{GetType().Name}:{ns}{FieldName(name)}";
    }

    /// <summary>A field's name as Python spells it: the C# property name in snake_case.</summary>
    public static string FieldName(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return JsonNamingPolicy.SnakeCaseLower.ConvertName(propertyName);
    }

    /// <summary>Port of <c>model_dump()</c>: every field (snake_case name) read from the store, so changes made behind the model's back are reflected.</summary>
    public IReadOnlyDictionary<string, object?> ToDictionary()
    {
        _ = Store;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in _fields)
        {
            result[FieldName(field.Name)] = Read(field);
        }

        return result;
    }

    /// <summary>Port of <c>model_dump_json()</c> as a node.</summary>
    public JsonObject ToJson() => Jsonable.FromDictionary(ToDictionary());

    /// <summary>
    /// Reads the property named <paramref name="name"/> (the caller by default) from the store. When the key is
    /// absent the model's last known value stands (the default while binding, which is then written to the store).
    /// </summary>
    protected T Get<T>(T @default = default!, [CallerMemberName] string name = "")
    {
        var store = Store;
        var key = NamespacedName(name);
        if (store.TryGetValue(key, out var raw))
        {
            return Coerce<T>(name, key, raw);
        }

        if (_values.TryGetValue(name, out var known))
        {
            return known is null ? default! : (T)known;
        }

        if (_binding)
        {
            store.Set(key, @default);
        }

        _values[name] = @default;
        return @default;
    }

    /// <summary>Writes the property named <paramref name="name"/> (the caller by default) to the store; a nested <see cref="StoreModel"/> is rejected as in Python.</summary>
    protected void Set<T>(T value, [CallerMemberName] string name = "")
    {
        if (value is StoreModel)
        {
            throw new ArgumentException(
                $"{name} is a StoreModel and you may not embed a StoreModel inside another StoreModel (use a plain record or class for fields in a StoreModel).",
                nameof(value));
        }

        Store.Set(NamespacedName(name), value);
        _values[name] = value;
    }

    private void Bind(Store store, string? instance)
    {
        _store = store;
        _instance = instance;
        _fields = GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.DeclaringType != typeof(StoreModel) && property.GetIndexParameters().Length == 0 && property.CanRead)
            .ToArray();
        foreach (var field in _fields)
        {
            if (field.GetMethod!.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                throw new InvalidOperationException(
                    $"{GetType().Name}.{field.Name} is an auto-property; StoreModel properties must read and write the store through Get and Set.");
            }
        }

        _binding = true;
        try
        {
            foreach (var field in _fields)
            {
                Read(field);
            }
        }
        finally
        {
            _binding = false;
        }
    }

    private object? Read(PropertyInfo field)
    {
        try
        {
            return field.GetValue(this);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private T Coerce<T>(string name, string key, object? raw)
    {
        if (raw is T typed)
        {
            _values[name] = typed;
            return typed;
        }

        if (raw is null)
        {
            if (default(T) is not null)
            {
                throw new StoreModelException($"Store value '{key}' is null but {GetType().Name}.{name} is a non-nullable {typeof(T).Name}.");
            }

            _values[name] = null;
            return default!;
        }

        T? coerced;
        try
        {
            coerced = JsonSerializer.Deserialize<T>(Jsonable.FromValue(raw), EvalLogWriter.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new StoreModelException($"Store value '{key}' ({raw.GetType().Name}) cannot be converted to {typeof(T).Name} for {GetType().Name}.{name}.", ex);
        }

        if (coerced is null)
        {
            throw new StoreModelException($"Store value '{key}' ({raw.GetType().Name}) cannot be converted to {typeof(T).Name} for {GetType().Name}.{name}.");
        }

        Store.Set(key, coerced);
        _values[name] = coerced;
        return coerced;
    }
}
