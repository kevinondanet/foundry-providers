// ============================================================================
//  ENTRY POINT and the layer guard  (demo host; not part of inspect_ai)
//
//  Read the folders in order, top to bottom, to follow one eval:
//
//    Layer1_Interfaces/   _cli, _view      parse args, watch, print
//    Layer2_ControlPlane/ _control         talk to a running eval from outside
//    Layer3_Engine/       _eval            drive samples through solvers, write the log
//    Layer4_Authoring/    solver, scorer, dataset, tool, Task   the public API
//    Layer5_Model/        model            provider-neutral generate(), retries
//    Layer6_Providers/    model._providers vendor wire formats
//    Layer7_Runtime/      util + inspect_sandbox_tools   sandbox, in-container RPC
//    CrossCutting/        registry, transcript, filesystem, event loop
//    examples/            an author's task file
//
//  Program.Main does three things Python does at `import inspect_ai` and
//  `anyio.run()`: create the single event loop, register filesystems and
//  decorated factories, and hand control to the CLI.
// ============================================================================
using System.Reflection;
using System.Reflection.Emit;
using inspect_ai._cli;
using inspect_ai._util._async;
using inspect_ai._util.display;
using inspect_ai._util.file;
using inspect_ai._util.registry;

namespace demo;

public static class Program
{
    public static int Main(string[] args)
    {
        // One event loop for the whole process (cross-cutting concern 4).
        return SingleThreadEventLoop.Run(async () =>
        {
            Startup.ImportPackages();

            if (args.Length == 0)
            {
                // The narrated walk-through: eval, then view, then the layer check.
                var code = await Cli.Run(new[] { "eval", "arithmetic" });
                if (code != 0) return code;
                await Cli.Run(new[] { "view" });
                return LayerGuard.Check(typeof(Program).Assembly) == 0 ? 0 : 3;
            }

            if (args[0] == "check-layers")
                return LayerGuard.Check(typeof(Program).Assembly) == 0 ? 0 : 3;

            return await Cli.Run(args);
        });
    }
}

internal static class Startup
{
    /// <summary>What happens at import time in Python: filesystems register their
    /// schemes, and every decorated task/solver/scorer/tool/provider registers its name.</summary>
    public static void ImportPackages()
    {
        Display.Banner("Start-up · 'import inspect_ai': one event loop, filesystems, registry scan");
        Display.Step("x  _util._async", $"single event loop running on thread {Environment.CurrentManagedThreadId}; no locks needed for module state");
        FileSystems.Register(new LocalFileSystem());
        FileSystems.Register(new MemoryFileSystem());
        Registry.RegisterAssembly(typeof(Program).Assembly);
    }
}

/// <summary>
/// Proves the one-way layering by reflection: for every type in the assembly
/// it collects the types it references (base types, members, and the types
/// used inside method bodies, found by scanning IL) and reports any reference
/// from a lower layer to a higher one. Also checks the two special rules:
/// inspect_sandbox_tools references nothing in inspect_ai, and author code in
/// `examples` references no underscore-prefixed package.
/// </summary>
internal static class LayerGuard
{
    // Namespace -> layer number (1 = top). The longest matching prefix wins.
    private static readonly (string Namespace, int Layer)[] Layers =
    {
        ("inspect_ai._cli", 1), ("inspect_ai._view", 1),
        ("inspect_ai._control", 2),
        ("inspect_ai._eval", 3),
        ("inspect_ai", 4), ("inspect_ai.dataset", 4), ("inspect_ai.solver", 4), ("inspect_ai.tool", 4), ("inspect_ai.scorer", 4),
        ("inspect_ai.model", 5),
        ("inspect_ai.model._providers", 6),
        ("inspect_ai.util", 7), ("inspect_sandbox_tools", 7),
    };

    // Used by every layer; exempt from the ordering.
    private static readonly string[] CrossCutting = { "inspect_ai._util", "inspect_ai.log" };

    // Dependency inversion: a provider implements the contract (ModelAPI) that
    // the model layer defines. That is the one sanctioned upward reference.
    private static readonly (string From, string To)[] Inversions = { ("inspect_ai.model._providers", "inspect_ai.model") };

    public static int Check(Assembly assembly)
    {
        Display.Banner("Layer check · no layer may reference a layer above it");
        var violations = new List<string>();
        var counted = new Dictionary<int, int>();

        foreach (var type in assembly.GetTypes())
        {
            var ns = type.Namespace ?? "";
            var layer = LayerOf(ns);
            if (layer is { } l) counted[l] = counted.GetValueOrDefault(l) + 1;

            foreach (var referenced in References(type).Distinct())
            {
                var rns = referenced.Namespace ?? "";
                if (rns == ns) continue;

                if (ns.StartsWith("inspect_sandbox_tools") && rns.StartsWith("inspect_ai"))
                    violations.Add($"{Name(type)} (standalone container package) references {referenced.Name} in {rns}");
                else if (ns.StartsWith("examples") && rns.Split('.').Any(seg => seg.StartsWith('_')))
                    violations.Add($"{Name(type)} (author code) references private package {rns}");
                else if (layer is { } from && LayerOf(rns) is { } to && to < from && !Inversions.Any(i => Matches(ns, i.From) && Matches(rns, i.To)))
                    violations.Add($"{Name(type)} (layer {from}) reaches UP to {referenced.Name} in {rns} (layer {to})");
            }
        }

        foreach (var (layer, count) in counted.OrderBy(kv => kv.Key))
            Display.Step($"L{layer}", $"{count} types checked");
        Display.Step("x  inversions", string.Join("; ", Inversions.Select(i => $"{i.From} may implement contracts from {i.To}")));
        foreach (var v in violations) Display.Step("VIOLATION", v);
        Display.Step("result", violations.Count == 0 ? "0 violations: every reference points down or sideways" : $"{violations.Count} violation(s)");
        return violations.Count;
    }

    private static string Name(Type t) => $"{t.Namespace}.{t.Name}";
    private static bool Matches(string ns, string prefix) => ns == prefix || ns.StartsWith(prefix + ".");

    private static int? LayerOf(string ns)
    {
        if (CrossCutting.Any(c => Matches(ns, c))) return null;
        var best = Layers.Where(l => Matches(ns, l.Namespace)).OrderByDescending(l => l.Namespace.Length).FirstOrDefault();
        return best.Namespace is null ? null : best.Layer;
    }

    // ---- reference collection -------------------------------------------

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> References(Type type)
    {
        if (type.BaseType is { } b) foreach (var t in Expand(b)) yield return t;
        foreach (var i in type.GetInterfaces()) foreach (var t in Expand(i)) yield return t;
        foreach (var f in type.GetFields(Declared)) foreach (var t in Expand(f.FieldType)) yield return t;
        foreach (var p in type.GetProperties(Declared)) foreach (var t in Expand(p.PropertyType)) yield return t;
        foreach (var m in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            if (m is MethodInfo mi) foreach (var t in Expand(mi.ReturnType)) yield return t;
            foreach (var p in m.GetParameters()) foreach (var t in Expand(p.ParameterType)) yield return t;
            foreach (var t in BodyReferences(m)) foreach (var e in Expand(t)) yield return e;
        }
    }

    /// <summary>Unwrap arrays, byrefs and generic arguments so Func&lt;TaskState, Task&lt;TaskState&gt;&gt; yields TaskState.</summary>
    private static IEnumerable<Type> Expand(Type t)
    {
        if (t.IsByRef || t.IsArray || t.IsPointer) t = t.GetElementType()!;
        if (t.IsGenericParameter) yield break;
        yield return t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) foreach (var e in Expand(a)) yield return e;
    }

    private static readonly Dictionary<short, OpCode> OpCodeTable = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>Walk a method body's IL and resolve every method, field and type token it touches.</summary>
    private static IEnumerable<Type> BodyReferences(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null) yield break;
        foreach (var local in body.LocalVariables) yield return local.LocalType;

        var il = body.GetILAsByteArray()!;
        var module = method.Module;
        var typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var i = 0;
        while (i < il.Length)
        {
            var value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            var op = OpCodeTable[value];
            i += op.Size;

            var operandSize = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };

            if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                MemberInfo? member = null;
                try { member = module.ResolveMember(BitConverter.ToInt32(il, i), typeArgs, methodArgs); }
                catch (ArgumentException) { /* a signature or string token; not a member */ }

                switch (member)
                {
                    case Type t: yield return t; break;
                    case MethodBase mb when mb.DeclaringType is { } dt: yield return dt; break;
                    case FieldInfo fi when fi.DeclaringType is { } dt: yield return dt; break;
                }
            }
            i += operandSize;
        }
    }
}
