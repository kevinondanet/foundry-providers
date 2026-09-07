// ============================================================================
//  CROSS-CUTTING HELPER: console narration  (not part of inspect_ai)
//
//  Every layer calls Display.Step(...) so the console shows *which layer* is
//  doing the work at each moment. The tag is the layer number plus the Python
//  package the C# code stands in for, e.g. "L3 _eval".
//  Python's real equivalent is the rich/textual display in inspect_ai/_display,
//  which lives in layer 1; this helper is only here to narrate the demo.
// ============================================================================
namespace inspect_ai._util.display;

internal static class Display
{
    /// <summary>Print one narrated step. Layer tags are left-aligned so the
    /// output reads as a table of "who did what".</summary>
    public static void Step(string layer, string message)
        => Console.WriteLine($"  {layer,-26} {message}");

    /// <summary>A section banner for the seven walk-through steps.</summary>
    public static void Banner(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} " + new string('─', Math.Max(0, 70 - title.Length)));
    }
}

internal static class DisplayOnce
{
    private static readonly HashSet<string> Seen = new();   // no lock: single event loop

    /// <summary>Print a banner the first time a key is seen; used to mark the
    /// first entry into each layer even when samples interleave.</summary>
    public static void Banner(string key, string title)
    {
        if (Seen.Add(key)) Display.Banner(title);
    }
}
