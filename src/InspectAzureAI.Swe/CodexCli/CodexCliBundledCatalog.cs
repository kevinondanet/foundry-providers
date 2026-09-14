namespace InspectAzureAI.Swe.CodexCli;

/// <summary>
/// Port of inspect_swe <c>_codex_cli/_bundled_catalog.py</c>: the fallback Codex model catalog used when the
/// version-matched <c>models.json</c> cannot be fetched (offline, rate limited, or a release that predates
/// <c>models-manager</c>). Only the fields the slug resolver reads are kept (<c>slug</c>, <c>priority</c>,
/// <c>apply_patch_tool_type</c>, <c>supports_search_tool</c>). Snapshot source: <c>openai/codex</c>
/// <c>codex-rs/models-manager/models.json</c> at <c>rust-v0.145.0</c>, the same snapshot as Python (deviation D-X11
/// keeps parity rather than refreshing it).
/// </summary>
internal static class CodexCliBundledCatalog
{
    /// <summary>The snapshot as JSON text (a raw string literal, so no embedded resource is needed).</summary>
    public const string Json = """
        {
          "models": [
            {
              "slug": "gpt-5.6-sol",
              "priority": 1,
              "apply_patch_tool_type": "freeform",
              "supports_search_tool": true
            },
            {
              "slug": "gpt-5.6-terra",
              "priority": 2,
              "apply_patch_tool_type": "freeform",
              "supports_search_tool": true
            },
            {
              "slug": "gpt-5.6-luna",
              "priority": 3,
              "apply_patch_tool_type": "freeform",
              "supports_search_tool": true
            },
            {
              "slug": "gpt-5.5",
              "priority": 7,
              "apply_patch_tool_type": "freeform",
              "supports_search_tool": true
            },
            {
              "slug": "gpt-5.4",
              "priority": 16,
              "apply_patch_tool_type": "freeform",
              "supports_search_tool": true
            },
            {
              "slug": "gpt-5.4-mini",
              "priority": 23,
              "apply_patch_tool_type": "freeform",
              "supports_search_tool": true
            },
            {
              "slug": "gpt-5.2",
              "priority": 29,
              "apply_patch_tool_type": "freeform",
              "supports_search_tool": true
            },
            {
              "slug": "codex-auto-review",
              "priority": 43,
              "apply_patch_tool_type": "freeform",
              "supports_search_tool": true
            }
          ]
        }
        """;
}
