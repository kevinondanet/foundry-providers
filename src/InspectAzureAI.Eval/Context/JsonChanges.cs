using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Context;

/// <summary>Port of <c>JsonChangeOp</c>: the RFC 6902 operations.</summary>
public enum JsonChangeOp
{
    Remove,
    Add,
    Replace,
    Move,
    Test,
    Copy,
}

/// <summary>
/// Port of <c>_util/json.py</c> <c>JsonChange</c>: one JSON Patch operation plus, for a <c>replace</c>, the value
/// it overwrote. Written as <c>{"op", "path", "from", "value", "replaced"}</c> with null members omitted, the shape
/// Python's log writer produces.
/// </summary>
public sealed record JsonChange(JsonChangeOp Op, string Path)
{
    /// <summary>Location from which data was moved or copied.</summary>
    public string? From { get; init; }

    /// <summary>Changed value (an explicit JSON null and an absent value are both null, as in Python).</summary>
    public JsonNode? Value { get; init; }

    /// <summary>Replaced value.</summary>
    public JsonNode? Replaced { get; init; }

    /// <summary>The operation name as written to the log (<c>"add"</c>, <c>"remove"</c>, ...).</summary>
    public string OpName => OpToWire(Op);

    public static string OpToWire(JsonChangeOp op) => op switch
    {
        JsonChangeOp.Remove => "remove",
        JsonChangeOp.Add => "add",
        JsonChangeOp.Replace => "replace",
        JsonChangeOp.Move => "move",
        JsonChangeOp.Test => "test",
        JsonChangeOp.Copy => "copy",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown JSON change operation."),
    };

    public static JsonChangeOp ParseOp(string op) => op switch
    {
        "remove" => JsonChangeOp.Remove,
        "add" => JsonChangeOp.Add,
        "replace" => JsonChangeOp.Replace,
        "move" => JsonChangeOp.Move,
        "test" => JsonChangeOp.Test,
        "copy" => JsonChangeOp.Copy,
        _ => throw new JsonException($"Unknown JSON change operation '{op}'."),
    };

    /// <summary>The change as the log writes it (null members omitted).</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject { ["op"] = OpName, ["path"] = Path };
        if (From is not null)
        {
            obj["from"] = From;
        }

        if (Value is not null)
        {
            obj["value"] = Value.DeepClone();
        }

        if (Replaced is not null)
        {
            obj["replaced"] = Replaced.DeepClone();
        }

        return obj;
    }

    /// <summary>Reads a change written by <see cref="ToJson"/> (or by Python); <c>op</c> and <c>path</c> are required.</summary>
    public static JsonChange FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            throw new JsonException("A JSON change must be an object.");
        }

        var op = obj["op"]?.GetValue<string>() ?? throw new JsonException("A JSON change requires 'op'.");
        var path = obj["path"]?.GetValue<string>() ?? throw new JsonException("A JSON change requires 'path'.");
        return new JsonChange(ParseOp(op), path)
        {
            From = obj["from"]?.GetValue<string>(),
            Value = obj["value"]?.DeepClone(),
            Replaced = obj["replaced"]?.DeepClone(),
        };
    }
}

/// <summary>
/// Port of <c>_util/json.py</c> <c>json_changes</c> and of the diff algorithm of the <c>jsonpatch</c> library it
/// calls (<c>DiffBuilder</c>): <see cref="Diff"/> yields the patch that turns <c>before</c> into <c>after</c>, with
/// jsonpatch's move detection and its convention that an appended list item is an <c>add</c> at the next index
/// (never <c>-</c>), plus the <c>replaced</c> value of every <c>replace</c> resolved through shadow copies of the
/// lists whose indices shift.
/// </summary>
public static partial class JsonChanges
{
    [GeneratedRegex(@"^(.*)/(\d+)$")]
    private static partial Regex ArrayIndexPattern();

    /// <summary>
    /// Port of <c>json_changes(before, after)</c>: the changes including <c>replaced</c> values, or null when the
    /// documents are equal.
    /// </summary>
    public static IReadOnlyList<JsonChange>? Diff(JsonNode? before, JsonNode? after)
    {
        var patch = MakePatch(before, after);
        if (patch.Count == 0)
        {
            return null;
        }

        var tracked = TrackedContainers(patch);
        var shadow = tracked.ToDictionary(path => path, path => new JsonPointer(path).Resolve(before)?.DeepClone(), StringComparer.Ordinal);
        var changes = new List<JsonChange>(patch.Count);
        foreach (var op in patch)
        {
            var (container, relative) = ActiveContainer(op.Path, tracked);
            JsonNode? replaced = null;
            if (op.Op == JsonChangeOp.Replace)
            {
                var source = container is not null ? shadow[container] : before;
                var lookup = container is not null ? "/" + relative : op.Path;
                try
                {
                    replaced = new JsonPointer(lookup).Resolve(source)?.DeepClone();
                }
                catch (JsonPointerException)
                {
                    // the path is not in the shadow state: Python leaves `replaced` as None
                }
            }

            if (container is not null && op.Op is JsonChangeOp.Add or JsonChangeOp.Remove && shadow[container] is JsonArray list)
            {
                ApplyFastListOp(list, op, relative!);
            }

            changes.Add(op.Op == JsonChangeOp.Replace ? op with { Replaced = replaced } : op);
        }

        return changes;
    }

    /// <summary>Port of <c>jsonpatch.make_patch</c>: the raw operations (no <c>replaced</c> values); empty for equal documents.</summary>
    public static IReadOnlyList<JsonChange> MakePatch(JsonNode? before, JsonNode? after)
    {
        var builder = new DiffBuilder(after);
        builder.CompareValues("", null, before, after);
        return builder.Execute().ToList();
    }

    /// <summary>Applies <paramref name="changes"/> with <see cref="JsonPatch.Apply"/>.</summary>
    public static JsonNode? Apply(JsonNode? document, IEnumerable<JsonChange> changes, bool inPlace = false) => JsonPatch.Apply(document, changes, inPlace);

    /// <summary>
    /// Port of <c>_json_change_to_patch_op</c>: the change as a bare patch operation — <c>value</c> only for
    /// <c>add</c> / <c>replace</c> / <c>test</c> (an explicit null is kept), <c>from</c> required for <c>move</c> /
    /// <c>copy</c> (an <see cref="ArgumentException"/> otherwise), nothing but the path for <c>remove</c>.
    /// </summary>
    public static JsonChange ToPatchOp(JsonChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        switch (change.Op)
        {
            case JsonChangeOp.Add or JsonChangeOp.Replace or JsonChangeOp.Test:
                return new JsonChange(change.Op, change.Path) { Value = change.Value };
            case JsonChangeOp.Move or JsonChangeOp.Copy:
                if (change.From is null)
                {
                    throw new ArgumentException($"JsonChange operation '{change.OpName}' requires 'from' field", nameof(change));
                }

                return new JsonChange(change.Op, change.Path) { From = change.From };
            default:
                return new JsonChange(change.Op, change.Path);
        }
    }

    /// <summary>The changes as the JSON array written to a <c>StoreEvent</c> / <c>StateEvent</c>.</summary>
    public static JsonArray ToJson(IEnumerable<JsonChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var array = new JsonArray();
        foreach (var change in changes)
        {
            array.Add(change.ToJson());
        }

        return array;
    }

    /// <summary>Reads a <c>changes</c> array (the payload of a <c>StoreEvent</c> / <c>StateEvent</c>).</summary>
    public static IReadOnlyList<JsonChange> FromJson(JsonNode? array)
    {
        if (array is not JsonArray items)
        {
            throw new JsonException("Changes must be a JSON array.");
        }

        return items.Select(JsonChange.FromJson).ToList();
    }

    /// <inheritdoc cref="FromJson(JsonNode?)"/>
    public static IReadOnlyList<JsonChange> FromJson(JsonElement array) => FromJson(JsonNode.Parse(array.GetRawText()));

    /// <summary>
    /// Port of <c>_get_tracked_containers</c>: the arrays that are structurally modified (an <c>add</c> / <c>remove</c>
    /// at an index) and contain a later <c>replace</c>. Python keeps only the first matching container per replace
    /// (in set order); every match is kept here, which is deterministic and never resolves a shifted index wrongly.
    /// </summary>
    private static HashSet<string> TrackedContainers(IReadOnlyList<JsonChange> patch)
    {
        var structural = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in patch)
        {
            if (op.Op is JsonChangeOp.Add or JsonChangeOp.Remove && ArrayIndexPattern().Match(op.Path) is { Success: true } match)
            {
                structural.Add(match.Groups[1].Value);
            }
        }

        var tracked = new HashSet<string>(StringComparer.Ordinal);
        if (structural.Count > 0)
        {
            foreach (var op in patch)
            {
                if (op.Op == JsonChangeOp.Replace)
                {
                    foreach (var container in structural)
                    {
                        if (op.Path.StartsWith(container + "/", StringComparison.Ordinal))
                        {
                            tracked.Add(container);
                        }
                    }
                }
            }
        }

        return tracked;
    }

    /// <summary>Port of <c>_get_active_container</c>: the longest tracked container the path lies under, and the path relative to it.</summary>
    private static (string? Container, string? Relative) ActiveContainer(string path, HashSet<string> tracked)
    {
        string? best = null;
        foreach (var container in tracked)
        {
            if (path.StartsWith(container + "/", StringComparison.Ordinal) && (best is null || container.Length > best.Length))
            {
                best = container;
            }
        }

        return best is null ? (null, null) : (best, path[(best.Length + 1)..]);
    }

    /// <summary>Port of <c>_apply_fast_list_op</c>: keeps a shadow list in step with an <c>add</c> / <c>remove</c> beneath it.</summary>
    private static void ApplyFastListOp(JsonArray target, JsonChange op, string relative)
    {
        if (!relative.Contains('/'))
        {
            if (op.Op == JsonChangeOp.Add)
            {
                var index = relative == "-" ? target.Count : int.Parse(relative, CultureInfo.InvariantCulture);
                target.Insert(Math.Min(index, target.Count), op.Value?.DeepClone());
            }
            else
            {
                target.RemoveAt(int.Parse(relative, CultureInfo.InvariantCulture));
            }
        }
        else
        {
            JsonPatch.Apply(target, [op with { Path = "/" + relative }], inPlace: true);
        }
    }

    /// <summary>
    /// Port of <c>jsonpatch.DiffBuilder</c>: a linked list of operations built by walking both documents, with
    /// removed / added items indexed by <c>(value, type)</c> so a removal matching a later addition (or vice versa)
    /// collapses into a <c>move</c> whose list indices are corrected by the "undo" adjustments, and a
    /// <c>remove</c> immediately followed by an <c>add</c> at the same location collapsed into a <c>replace</c>.
    /// Dictionary keys are visited in document order (Python visits them in set order, which varies by hash seed).
    /// </summary>
    private sealed class DiffBuilder(JsonNode? destination)
    {
        private const int StoreAdd = 0;

        private const int StoreRemove = 1;

        private readonly LinkedList<PatchOp> _ops = new();

        private readonly List<(JsonNode? Value, LinkedListNode<PatchOp> Node)>[] _index = [new(), new()];

        public IEnumerable<JsonChange> Execute()
        {
            for (var node = _ops.First; node is not null;)
            {
                if (node.Next is { } next && node.Value.Kind == JsonChangeOp.Remove && next.Value.Kind == JsonChangeOp.Add
                    && string.Equals(node.Value.Location, next.Value.Location, StringComparison.Ordinal))
                {
                    yield return new JsonChange(JsonChangeOp.Replace, next.Value.Location) { Value = next.Value.Value?.DeepClone() };
                    node = next.Next;
                    continue;
                }

                yield return node.Value.ToChange();
                node = node.Next;
            }
        }

        public void CompareValues(string path, string? key, JsonNode? source, JsonNode? target)
        {
            switch (source, target)
            {
                case (JsonObject sourceObject, JsonObject targetObject):
                    CompareDicts(PathJoin(path, key), sourceObject, targetObject);
                    break;
                case (JsonArray sourceArray, JsonArray targetArray):
                    CompareLists(PathJoin(path, key), sourceArray, targetArray);
                    break;
                default:
                    if (!JsonValues.DumpsEquals(source, target))
                    {
                        ItemReplaced(path, key, target);
                    }

                    break;
            }
        }

        private void CompareDicts(string path, JsonObject source, JsonObject target)
        {
            foreach (var (key, value) in source)
            {
                if (!target.ContainsKey(key))
                {
                    ItemRemoved(path, key, false, value);
                }
            }

            foreach (var (key, value) in target)
            {
                if (!source.ContainsKey(key))
                {
                    ItemAdded(path, key, false, value);
                }
            }

            foreach (var (key, value) in source)
            {
                if (target.TryGetPropertyValue(key, out var other))
                {
                    CompareValues(path, key, value, other);
                }
            }
        }

        private void CompareLists(string path, JsonArray source, JsonArray target)
        {
            var maxLength = Math.Max(source.Count, target.Count);
            var minLength = Math.Min(source.Count, target.Count);
            for (var key = 0; key < maxLength; key++)
            {
                if (key < minLength)
                {
                    var old = source[key];
                    var updated = target[key];
                    if (JsonValues.PythonEquals(old, updated))
                    {
                        continue;
                    }

                    switch (old, updated)
                    {
                        case (JsonObject oldObject, JsonObject newObject):
                            CompareDicts(PathJoin(path, Index(key)), oldObject, newObject);
                            break;
                        case (JsonArray oldArray, JsonArray newArray):
                            CompareLists(PathJoin(path, Index(key)), oldArray, newArray);
                            break;
                        default:
                            ItemRemoved(path, Index(key), true, old);
                            ItemAdded(path, Index(key), true, updated);
                            break;
                    }
                }
                else if (source.Count > target.Count)
                {
                    ItemRemoved(path, Index(target.Count), true, source[key]);
                }
                else
                {
                    ItemAdded(path, Index(key), true, target[key]);
                }
            }
        }

        private void ItemAdded(string path, string key, bool isIndex, JsonNode? item)
        {
            var index = TakeIndex(item, StoreRemove);
            if (index is not null)
            {
                var op = index.Value;
                if (op.IntKey is not null && isIndex)
                {
                    foreach (var later in IterFrom(index))
                    {
                        op.SetKey(later.OnUndoRemove(op.PathKey, op.IntKey!.Value));
                    }
                }

                _ops.Remove(index);
                var location = PathJoin(path, key);
                if (!string.Equals(op.Location, location, StringComparison.Ordinal))
                {
                    _ops.AddLast(PatchOp.Move(op.Parts, new JsonPointer(location).Parts));
                }
            }
            else
            {
                var node = _ops.AddLast(PatchOp.Add(new JsonPointer(PathJoin(path, key)).Parts, item));
                _index[StoreAdd].Add((item, node));
            }
        }

        private void ItemRemoved(string path, string key, bool isIndex, JsonNode? item)
        {
            _ = isIndex;
            var removal = PatchOp.Remove(new JsonPointer(PathJoin(path, key)).Parts);
            var index = TakeIndex(item, StoreAdd);
            var node = _ops.AddLast(removal);
            if (index is not null)
            {
                var op = index.Value;
                // jsonpatch checks the container in the destination document rather than the key type, since a
                // numeric dictionary key parses as an int too
                var (addedContainer, _) = new JsonPointer(op.Parts).ToLast(destination);
                if (addedContainer is JsonArray && op.IntKey is not null)
                {
                    foreach (var later in IterFrom(index))
                    {
                        op.SetKey(later.OnUndoAdd(op.PathKey, op.IntKey!.Value));
                    }
                }

                _ops.Remove(index);
                if (!string.Equals(removal.Location, op.Location, StringComparison.Ordinal))
                {
                    node.Value = PatchOp.Move(removal.Parts, op.Parts);
                }
                else
                {
                    _ops.Remove(node);
                }
            }
            else
            {
                _index[StoreRemove].Add((item, node));
            }
        }

        private void ItemReplaced(string path, string? key, JsonNode? item) =>
            _ops.AddLast(PatchOp.Replace(new JsonPointer(PathJoin(path, key)).Parts, item));

        private LinkedListNode<PatchOp>? TakeIndex(JsonNode? value, int storage)
        {
            var entries = _index[storage];
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (JsonValues.TypedEquals(entries[i].Value, value))
                {
                    var node = entries[i].Node;
                    entries.RemoveAt(i);
                    return node;
                }
            }

            return null;
        }

        private IEnumerable<PatchOp> IterFrom(LinkedListNode<PatchOp> start)
        {
            for (var node = start.Next; node is not null; node = node.Next)
            {
                yield return node.Value;
            }
        }

        private static string Index(int key) => key.ToString(CultureInfo.InvariantCulture);

        /// <summary>Port of <c>_path_join</c>: a null key is the path itself (the root).</summary>
        private static string PathJoin(string path, string? key) => key is null ? path : path + "/" + JsonPointer.Escape(key);
    }

    /// <summary>
    /// Port of <c>jsonpatch.PatchOperation</c> as the diff builder mutates it: the pointer tokens of <c>path</c>
    /// (and <c>from</c> for a move) with the last token exposed as an integer key when it parses as one, so the
    /// undo adjustments can shift list indices in place.
    /// </summary>
    private sealed class PatchOp
    {
        private PatchOp(JsonChangeOp kind, List<string> parts, List<string>? fromParts, JsonNode? value)
        {
            Kind = kind;
            Parts = parts;
            FromParts = fromParts;
            Value = value;
        }

        public JsonChangeOp Kind { get; }

        public List<string> Parts { get; }

        public List<string>? FromParts { get; }

        public JsonNode? Value { get; }

        public string Location => JsonPointer.Join(Parts);

        /// <summary>Python's <c>PatchOperation.path</c>: the unescaped parent tokens joined with <c>/</c>.</summary>
        public string PathKey => string.Join("/", Parts.Take(Parts.Count - 1));

        public int? IntKey => Parts.Count > 0 ? ParseKey(Parts[^1]) : null;

        private string FromPathKey => string.Join("/", FromParts!.Take(FromParts!.Count - 1));

        private int? FromIntKey => FromParts!.Count > 0 ? ParseKey(FromParts![^1]) : null;

        public static PatchOp Add(IEnumerable<string> parts, JsonNode? value) => new(JsonChangeOp.Add, parts.ToList(), null, value);

        public static PatchOp Remove(IEnumerable<string> parts) => new(JsonChangeOp.Remove, parts.ToList(), null, null);

        public static PatchOp Replace(IEnumerable<string> parts, JsonNode? value) => new(JsonChangeOp.Replace, parts.ToList(), null, value);

        public static PatchOp Move(IEnumerable<string> fromParts, IEnumerable<string> parts) => new(JsonChangeOp.Move, parts.ToList(), fromParts.ToList(), null);

        public void SetKey(int key) => Parts[^1] = key.ToString(CultureInfo.InvariantCulture);

        private void SetFromKey(int key) => FromParts![^1] = key.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Port of <c>_on_undo_remove</c>. A key that is not an integer is left alone (Python would raise a
        /// <c>TypeError</c> comparing it with an int; it only arises for dictionaries mixing numeric and other keys).
        /// </summary>
        public int OnUndoRemove(string path, int key)
        {
            switch (Kind)
            {
                case JsonChangeOp.Remove:
                    if (PathKey == path && IntKey is { } removeKey)
                    {
                        if (removeKey >= key)
                        {
                            SetKey(removeKey + 1);
                        }
                        else
                        {
                            key -= 1;
                        }
                    }

                    return key;
                case JsonChangeOp.Add:
                    if (PathKey == path && IntKey is { } addKey)
                    {
                        if (addKey > key)
                        {
                            SetKey(addKey + 1);
                        }
                        else
                        {
                            key += 1;
                        }
                    }

                    return key;
                case JsonChangeOp.Move:
                    if (FromPathKey == path && FromIntKey is { } fromKey)
                    {
                        if (fromKey >= key)
                        {
                            SetFromKey(fromKey + 1);
                        }
                        else
                        {
                            key -= 1;
                        }
                    }

                    if (PathKey == path && IntKey is { } moveKey)
                    {
                        if (moveKey > key)
                        {
                            SetKey(moveKey + 1);
                        }
                        else
                        {
                            key += 1;
                        }
                    }

                    return key;
                default:
                    return key;
            }
        }

        /// <summary>Port of <c>_on_undo_add</c> (see <see cref="OnUndoRemove"/> for the non-integer key rule).</summary>
        public int OnUndoAdd(string path, int key)
        {
            switch (Kind)
            {
                case JsonChangeOp.Remove:
                    if (PathKey == path && IntKey is { } removeKey)
                    {
                        if (removeKey > key)
                        {
                            SetKey(removeKey - 1);
                        }
                        else
                        {
                            key -= 1;
                        }
                    }

                    return key;
                case JsonChangeOp.Add:
                    if (PathKey == path && IntKey is { } addKey)
                    {
                        if (addKey > key)
                        {
                            SetKey(addKey - 1);
                        }
                        else
                        {
                            key += 1;
                        }
                    }

                    return key;
                case JsonChangeOp.Move:
                    if (FromPathKey == path && FromIntKey is { } fromKey)
                    {
                        if (fromKey > key)
                        {
                            SetFromKey(fromKey - 1);
                        }
                        else
                        {
                            key -= 1;
                        }
                    }

                    if (PathKey == path && IntKey is { } moveKey)
                    {
                        if (moveKey > key)
                        {
                            SetKey(moveKey - 1);
                        }
                        else
                        {
                            key += 1;
                        }
                    }

                    return key;
                default:
                    return key;
            }
        }

        public JsonChange ToChange() => Kind switch
        {
            JsonChangeOp.Add or JsonChangeOp.Replace => new JsonChange(Kind, Location) { Value = Value?.DeepClone() },
            JsonChangeOp.Move => new JsonChange(Kind, Location) { From = JsonPointer.Join(FromParts!) },
            _ => new JsonChange(Kind, Location),
        };

        private static int? ParseKey(string part) =>
            int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ? key : null;
    }
}
