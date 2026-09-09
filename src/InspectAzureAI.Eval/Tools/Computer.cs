using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>
/// Port of <c>tool/_tools/_computer/_computer.py</c> <c>computer()</c> and <c>_common.py</c>: the desktop computer
/// tool. Every action runs <c>python3 /opt/inspect/tool/computer_tool.py &lt;action&gt; ...</c> in the sample sandbox
/// that carries the tool service (the <c>aisiuk/inspect-computer-tool</c> image), and the service's JSON reply
/// (<c>output</c>, <c>error</c>, <c>base64_image</c>) becomes text plus a PNG <see cref="ContentImage"/>. With
/// <c>maxScreenshots</c> the tool installs a <see cref="ToolDef.ModelInput"/> hook that replaces older screenshots
/// with a text placeholder before the conversation is sent to the model.
/// Deviation: the Anthropic Messages route sends the tool as an ordinary function tool (no native
/// <c>computer_20250124</c> mapping); the OpenAI multi-action <c>actions</c> parameter is honoured as in Python.
/// </summary>
public static class Computer
{
    public const string Name = "computer";

    /// <summary>The tool service inside the sandbox (Python's <c>_send_cmd</c> and <c>computer_sandbox</c>).</summary>
    public const string ToolPath = "/opt/inspect/tool/computer_tool.py";

    /// <summary>Python: <c>timeout: int | None = 180</c>.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Python's <c>_computer_model_input</c> placeholder for a redacted screenshot.</summary>
    public const string ScreenshotRemovedText = "Screenshot removed to reduce size of input. Please consult the latest screenshots for the most up to date state of the screen.";

    /// <summary>The tool description the model sees (Python's <c>execute</c> docstring summary).</summary>
    public const string Description = "Use this tool to interact with a computer.\n\nUse a mouse and keyboard to interact with a computer's desktop GUI.\n\nKeep in mind that icons require double clicks to open while other UI affordances like menu items and buttons require a single click.";

    /// <summary>Port of the <c>Action</c> literal (kept in sync with the in-container <c>_constants.Action</c>).</summary>
    public static readonly IReadOnlyList<string> Actions =
    [
        "key", "hold_key", "type", "cursor_position", "mouse_move", "left_mouse_down", "left_mouse_up", "left_click",
        "left_click_drag", "right_click", "middle_click", "back_click", "forward_click", "double_click", "triple_click",
        "scroll", "wait", "screenshot", "zoom", "open_web_browser", "navigate",
    ];

    public static readonly IReadOnlyList<string> ScrollDirections = ["up", "down", "left", "right"];

    /// <summary>Port of <c>_COMPUTER_TOOL_PARAMETERS</c>: the parameter names that identify this tool.</summary>
    public static readonly IReadOnlySet<string> ParameterNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "action", "coordinate", "duration", "region", "scroll_amount", "scroll_direction", "start_coordinate", "text", "press_enter", "actions",
    };

    private const string ActionDescription = "The action to perform.\n- `key`: Press a key or key-combination on the keyboard.\n    - Example: execute(action=\"key\", text=\"ctrl+s\")\n    - Text can be any key name supported by xdotool's `key` such as:\n        \"Return\", \"Escape\", \"alt+Tab\", \"BackSpace\", \"Tab\", \"alt+Tab\", \"ctrl+s\", \"Up\", \"KP_0\" (for the numpad 0 key),\n        \"Insert\", \"Delete\", \"Home\", \"End\", \"Prior\", \"Next\", \"Left\", \"Up\", \"Right\", \"Down\",\n        \"F1\", \"F2\", \"F3\", \"F4\", \"F5\", \"F6\", \"F7\", \"F8\", \"F9\", \"F10\", \"F11\", \"F12\",\n        \"Shift_L\", \"Shift_R\", \"Control_L\", \"Control_R\", \"Alt_L\", \"Alt_R\", \"Scroll_Lock\", \"Num_Lock\", \"Caps_Lock\", \"Pause\",\n        \"KP_Multiply\", \"KP_Home\", \"KP_Up\", \"KP_Prior\", \"KP_Subtract\", \"KP_Left\", \"KP_Begin\", \"KP_Right\", \"KP_Add\", \"KP_End\",\"KP_Down\",\n        \"KP_Next\", \"KP_Insert\", \"KP_Delete\", \"KP_Enter\", \"KP_Divide\", \"KP_Equal\", \"KP_Decimal\",\n- 'hold_key': Hold down a key or multiple keys for a specified duration (in seconds). Supports the same syntax as `key`.\n- `type`: Type a string of text on the keyboard. If the text contains spaces, enclose it in quotes.\n    - Example: execute(action=\"type\", text=\"The crux of the biscuit is the apostrophe!\")\n- `cursor_position`: Get the current (x, y) pixel coordinate of the cursor on the screen.\n- `mouse_move`: Move the cursor to a specified (x, y) pixel coordinate on the screen.\n    - Example: execute(action=\"mouse_move\", coordinate=(100, 200))\n- `left_mouse_down`: Press the left mouse button.\n- `left_mouse_up`: Release the left mouse button.\n- `left_click`: Click the left mouse button.\n- `left_click_drag`: Click and drag the cursor to a specified (x, y) pixel coordinate on the screen.\n    - Example: execute(action=\"left_click_drag\", coordinate=(150, 250))\n- `right_click`: Click the right mouse button.\n- `middle_click`: Click the middle mouse button.\n- `back_click`: Click the 'back' mouse button.\n- `forward_click`: Click the 'forward' mouse button.\n- `double_click`: Double-click the left mouse button.\n- `triple_click`: Triple-click the left mouse button.\n- `wait`: Wait for a specified duration (in seconds).\n- `screenshot`: Take a screenshot.\n- `zoom`: Take a zoomed-in screenshot of a specified region at native resolution.\n    - Example: execute(action=\"zoom\", region=[100, 100, 500, 400])\n- `open_web_browser`: Open the web browser in full screen view.\n- `navigate`: Navigate to a URL in the browser.\n    - Example: execute(action=\"navigate\", text=\"https://example.com\")";

    /// <summary>The parameter schema, what Python's <c>ToolDef(computer()).parameters</c> dumps (every parameter optional, none required).</summary>
    public static ToolParams Parameters { get; } = new()
    {
        Properties = new Dictionary<string, ToolParam>
        {
            ["action"] = NullableEnum(ActionDescription, Actions),
            ["coordinate"] = NullableIntList("The (x, y) pixel coordinate on the screen to which to move or drag. Required only by `action=mouse_move` and `action=left_click_drag`."),
            ["duration"] = Nullable("integer", "The duration to wait or hold the key down for. Required only by `action=hold_key` and `action=wait`."),
            ["region"] = NullableIntList("The region to zoom into as [x0, y0, x1, y1] coordinates. Required only by `action=zoom`."),
            ["scroll_amount"] = Nullable("integer", "The number of 'clicks' to scroll. Required only by `action=scroll`."),
            ["scroll_direction"] = NullableEnum("The direction to scroll the screen. Required only by `action=scroll`.", ScrollDirections),
            ["start_coordinate"] = NullableIntList("The (x, y) pixel coordinate on the screen from which to initiate a drag. Required only by `action=left_click_drag`."),
            ["text"] = Nullable("string", "The text to type or the key to press. Required when action is \"key\" or \"type\"."),
            ["press_enter"] = Nullable("boolean", "If True and action is \"type\", press Return after typing. Defaults to False."),
            ["actions"] = new()
            {
                Description = "A list of action dicts to execute sequentially (OpenAI multi-action format).",
                AnyOf =
                [
                    new ToolParam { Type = ["array"], Items = new ToolParam { Type = ["object"], AdditionalProperties = new ToolParam() } },
                    ToolParam.Of("null"),
                ],
            },
        },
        Required = [],
    };

    /// <summary>
    /// Port of <c>is_computer_tool_info</c>: whether <paramref name="tool"/> is this built-in tool (the name alone is
    /// not enough, a third-party tool could be called "computer"; the parameter set settles it).
    /// </summary>
    public static bool IsComputerToolInfo(ToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Name == Name && ParameterNames.SetEquals(tool.Parameters.Properties.Keys);
    }

    /// <summary>
    /// Port of <c>computer(max_screenshots, timeout)</c>.
    /// </summary>
    /// <param name="maxScreenshots">The maximum number of screenshots played back to the model as input (Python's default 1); null for no limit (no <see cref="ToolDef.ModelInput"/> hook).</param>
    /// <param name="timeout">Timeout for each action; null or zero means 180 seconds, <see cref="Timeout.InfiniteTimeSpan"/> means no timeout (Python's <c>None</c>).</param>
    public static ToolDef Create(int? maxScreenshots = 1, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout switch
        {
            null => DefaultTimeout,
            { } given when given == Timeout.InfiniteTimeSpan => (TimeSpan?)null,
            { Ticks: > 0 } given => given,
            _ => DefaultTimeout,
        };
        return new ToolDef(Name, Description, Parameters, (arguments, cancellationToken) => ExecuteAsync(arguments, effectiveTimeout, cancellationToken))
        {
            ModelInput = maxScreenshots is { } max ? ModelInput(max) : null,
        };
    }

    /// <summary>
    /// Port of <c>_computer_model_input</c>: keeps the images of the last <paramref name="maxScreenshots"/> tool
    /// results and replaces every other <see cref="ContentImage"/> with <see cref="ScreenshotRemovedText"/> (unless the
    /// model asked for no truncation).
    /// </summary>
    public static ToolCallModelInput ModelInput(int maxScreenshots) => (messageIndex, messageTotal, content, hints) =>
    {
        if (hints.DisableComputerScreenshotTruncation)
        {
            return content;
        }

        // nothing to do for scalars
        if (content.IsString)
        {
            return content;
        }

        // if we are inside max_screenshots then return as is
        if (messageTotal - messageIndex <= maxScreenshots)
        {
            return content;
        }

        // otherwise convert images to text placeholders
        return MessageContent.FromItems(content.Items!.Select(c => c is ContentImage ? new ContentText(ScreenshotRemovedText) : c));
    };

    /// <summary>
    /// Port of <c>computer_sandbox</c>: the sample sandbox that has <see cref="ToolPath"/>; a
    /// <see cref="PrerequisiteError"/> (Python's message) when none does.
    /// </summary>
    public static async Task<ISandboxEnvironment> ComputerSandboxAsync(CancellationToken cancellationToken = default) =>
        await SandboxWith.FindAsync(ToolPath, cancellationToken: cancellationToken).ConfigureAwait(false)
        ?? throw new PrerequisiteError(
            "The computer tool service was not found in any of the sandboxes for this sample. Please add the computer tool service to your configuration. For example, the following Docker compose file uses the aisiuk/inspect-computer-tool image as its default sandbox:\n\n"
            + "services:\n"
            + "  default:\n"
            + "    image: \"aisiuk/inspect-computer-tool\"\n"
            + "    init: true");

    /// <summary>
    /// Port of <c>_normalize_key_text</c>: each whitespace-separated combo of <paramref name="text"/> normalised for
    /// xdotool (single letters lower-cased inside a combo, keysym names case-corrected, common aliases such as
    /// <c>enter</c>, <c>esc</c>, <c>cmd</c> and <c>pageup</c> mapped to their keysyms).
    /// </summary>
    public static string NormalizeKeyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeKeyCombo));
    }

    private static string NormalizeKeyCombo(string combo)
    {
        var parts = combo.Split('+');
        var isCombo = parts.Length > 1;
        var normalized = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 1 && char.IsLetter(part[0]))
            {
                normalized[i] = isCombo ? part.ToLowerInvariant() : part;
            }
            else
            {
                normalized[i] = KeyAliases.TryGetValue(part.ToLowerInvariant(), out var alias) ? alias : part;
            }
        }

        return string.Join("+", normalized);
    }

    private static async Task<ToolResult> ExecuteAsync(JsonObject arguments, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        List<JsonObject>? actionList = null;
        if (arguments["actions"] is { } actionsNode)
        {
            if (actionsNode is not JsonArray actions)
            {
                throw new ToolParsingError($"Parameter 'actions' must be a list of objects, got {JsonRpc.PythonTypeName(actionsNode)}.");
            }

            actionList = [];
            foreach (var item in actions)
            {
                actionList.Add(item as JsonObject ?? throw new ToolParsingError($"Parameter 'actions' must be a list of objects, got an item of {JsonRpc.PythonTypeName(item)}."));
            }
        }
        else if (arguments["action"] is not null)
        {
            // Python's _build_action_args: the single action's parameters, nulls dropped.
            var single = new JsonObject();
            foreach (var name in ParameterNames.Where(n => n != "actions"))
            {
                if (arguments[name] is { } value)
                {
                    single[name] = value.DeepClone();
                }
            }

            actionList = [single];
        }

        if (actionList is not { Count: > 0 })
        {
            // Deviation: Python's `assert action_list` fails the sample; the model gets the message instead.
            throw new ToolParsingError("Either 'action' or 'actions' must be provided");
        }

        ToolResult result = "OK";
        foreach (var actionArgs in actionList)
        {
            result = await ExecuteSingleActionAsync(actionArgs, timeout, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task<ToolResult> ExecuteSingleActionAsync(JsonObject args, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var action = args["action"] switch
        {
            null => "",
            JsonValue value when value.TryGetValue<string>(out var actionName) => actionName,
            { } node => node.ToJsonString(),
        };
        var coordinate = ToolArguments.OptionalIntegerList(args, "coordinate");
        var text = ToolArguments.OptionalString(args, "text");
        var duration = ToolArguments.OptionalInteger(args, "duration");
        var scrollAmount = ToolArguments.OptionalInteger(args, "scroll_amount");
        var scrollDirection = ToolArguments.OptionalString(args, "scroll_direction");
        var startCoordinate = ToolArguments.OptionalIntegerList(args, "start_coordinate");
        var region = ToolArguments.OptionalIntegerList(args, "region");

        switch (action)
        {
            case "key":
                return await SendAsync(["key", "--text", NormalizeKeyText(NotNone(text, "text"))], timeout, cancellationToken).ConfigureAwait(false);
            case "hold_key":
                return await SendAsync(["hold_key", "--text", NormalizeKeyText(NotNone(text, "text")), "--duration", $"{NotNone(duration, "duration")}"], timeout, cancellationToken).ConfigureAwait(false);
            case "type":
            {
                if (coordinate is not null)
                {
                    await SendAsync(["left_click", .. Coordinate(coordinate, "coordinate")], timeout, cancellationToken).ConfigureAwait(false);
                }

                var result = await SendAsync(["type", $"--text={NotNone(text, "text")}"], timeout, cancellationToken).ConfigureAwait(false);
                if (Truthy(args["press_enter"]))
                {
                    result = await SendAsync(["key", "--text", NormalizeKeyText("Return")], timeout, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }

            case "cursor_position":
                return await SendAsync(["cursor_position"], timeout, cancellationToken).ConfigureAwait(false);
            case "mouse_move":
                return await SendAsync(["mouse_move", .. Coordinate(NotNone(coordinate, "coordinate"), "coordinate")], timeout, cancellationToken).ConfigureAwait(false);
            case "left_mouse_down":
                return await SendAsync(["left_mouse_down"], timeout, cancellationToken).ConfigureAwait(false);
            case "left_mouse_up":
                return await SendAsync(["left_mouse_up"], timeout, cancellationToken).ConfigureAwait(false);
            case "left_click":
            case "right_click":
            case "middle_click":
            case "back_click":
            case "forward_click":
            case "double_click":
            case "triple_click":
                return await SendAsync([action, .. Coordinate(NotNone(coordinate, "coordinate"), "coordinate")], timeout, cancellationToken).ConfigureAwait(false);
            case "left_click_drag":
                return await SendAsync(
                    ["left_click_drag", .. Coordinate(NotNone(startCoordinate, "start_coordinate"), "start_coordinate", "--start_coordinate"), .. Coordinate(NotNone(coordinate, "coordinate"), "coordinate")],
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            case "scroll":
            {
                List<string> tail = ["scroll", "--scroll_amount", $"{NotNone(scrollAmount, "scroll_amount")}", "--scroll_direction", NotNone(scrollDirection, "scroll_direction")];
                if (coordinate is { Count: > 0 })
                {
                    tail.AddRange(Coordinate(coordinate, "coordinate"));
                }

                return await SendAsync(tail, timeout, cancellationToken).ConfigureAwait(false);
            }

            case "wait":
                return await SendAsync(["wait", "--duration", $"{NotNone(duration, "duration")}"], timeout, cancellationToken).ConfigureAwait(false);
            case "screenshot":
                return await SendAsync(["screenshot"], timeout, cancellationToken).ConfigureAwait(false);
            case "zoom":
            {
                var box = NotNone(region, "region");
                if (box.Count < 4)
                {
                    throw new ToolParsingError("region must have 4 elements");
                }

                return await SendAsync(["zoom", "--region", $"{box[0]}", $"{box[1]}", $"{box[2]}", $"{box[3]}"], timeout, cancellationToken).ConfigureAwait(false);
            }

            case "open_web_browser":
                return await SendAsync(["open_web_browser"], timeout, cancellationToken).ConfigureAwait(false);
            case "navigate":
                return await SendAsync(["navigate", "--text", NotNone(text, "text")], timeout, cancellationToken).ConfigureAwait(false);
        }

        throw new ToolParsingError($"Invalid action: {action}");
    }

    /// <summary>
    /// Port of <c>_send_cmd</c>: runs the tool service, fails the sample when the command itself fails (Python:
    /// <c>RuntimeError</c>), reports the service's <c>error</c> as a <see cref="ToolError"/>, and turns
    /// <c>output</c>/<c>base64_image</c> into the result (text, text + image, image, or "OK").
    /// </summary>
    private static async Task<ToolResult> SendAsync(IReadOnlyList<string> tail, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        string[] cmd = ["python3", ToolPath, .. tail];
        var sandbox = await ComputerSandboxAsync(cancellationToken).ConfigureAwait(false);
        var raw = await sandbox.ExecAsync(cmd, timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!raw.Success)
        {
            throw new InvalidOperationException($"Failure executing command: ${PythonListRepr(cmd)} {raw.Stderr}");
        }

        ToolExecResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ToolExecResult>(raw.Stdout, ResultOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The computer tool service returned invalid JSON: {ex.Message}", ex);
        }

        result ??= new ToolExecResult();
        if (result.Error is { Length: > 0 } error)
        {
            throw new ToolError(error);
        }

        var image = result.Base64Image is { Length: > 0 } base64 ? new ContentImage($"data:image/png;base64,{base64}") : null;
        var text = result.Output is { Length: > 0 } output ? output : null;
        if (text is not null && image is not null)
        {
            return ToolResult.FromContents([new ContentText(text), image]);
        }

        if (text is not null)
        {
            return text;
        }

        if (image is not null)
        {
            return ToolResult.FromContents([image]);
        }

        return "OK";
    }

    private static string[] Coordinate(IReadOnlyList<long> values, string name, string flag = "--coordinate")
    {
        if (values.Count < 2)
        {
            // Deviation: Python raises IndexError (failing the sample); the model gets a parsing error instead.
            throw new ToolParsingError($"{name} must have 2 elements");
        }

        return [flag, $"{values[0]}", $"{values[1]}"];
    }

    private static T NotNone<T>(T? value, string name) where T : class =>
        value ?? throw new ToolParsingError($"{name} must be provided");

    private static long NotNone(long? value, string name) =>
        value ?? throw new ToolParsingError($"{name} must be provided");

    /// <summary>Python's <c>bool(args.get("press_enter"))</c>.</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue value when value.TryGetValue<bool>(out var b) => b,
        JsonValue value when value.TryGetValue<double>(out var d) => d != 0,
        JsonValue value when value.TryGetValue<string>(out var s) => s.Length > 0,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        _ => true,
    };

    /// <summary>Python's <c>str(list[str])</c>, as the failure message interpolates the argv.</summary>
    private static string PythonListRepr(IEnumerable<string> items) =>
        "[" + string.Join(", ", items.Select(i => i.Contains('\'') && !i.Contains('"') ? $"\"{i}\"" : "'" + i.Replace("\\", "\\\\").Replace("'", "\\'") + "'")) + "]";

    private static ToolParam Nullable(string type, string description) => new()
    {
        Description = description,
        AnyOf = [ToolParam.Of(type), ToolParam.Of("null")],
    };

    private static ToolParam NullableIntList(string description) => new()
    {
        Description = description,
        AnyOf = [new ToolParam { Type = ["array"], Items = ToolParam.Of("integer") }, ToolParam.Of("null")],
    };

    private static ToolParam NullableEnum(string description, IReadOnlyList<string> values) => new()
    {
        Description = description,
        AnyOf = [new ToolParam { Type = ["string"], Enum = values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray() }, ToolParam.Of("null")],
    };

    private static readonly JsonSerializerOptions ResultOptions = new() { PropertyNameCaseInsensitive = false };

    /// <summary>Port of <c>_common.py</c> <c>ToolExecResult</c>: the service's JSON reply.</summary>
    private sealed record ToolExecResult
    {
        [JsonPropertyName("output")]
        public string? Output { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("base64_image")]
        public string? Base64Image { get; init; }
    }

    /// <summary>Port of <c>_KEY_ALIASES</c>: model key names to xdotool keysyms (keys are lower-case).</summary>
    private static readonly IReadOnlyDictionary<string, string> KeyAliases = BuildKeyAliases();

    private static Dictionary<string, string> BuildKeyAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] keysyms =
        [
            "Return", "Escape", "BackSpace", "Tab", "Delete", "Insert", "Home", "End", "Prior", "Next", "Left", "Up", "Right", "Down",
            "Pause", "space", "Scroll_Lock", "Num_Lock", "Caps_Lock", "Shift_L", "Shift_R", "Control_L", "Control_R", "Alt_L", "Alt_R",
            "Super_L", "Super_R", "Meta_L", "Meta_R",
        ];
        foreach (var name in keysyms)
        {
            aliases[name.ToLowerInvariant()] = name;
        }

        for (var i = 1; i <= 12; i++)
        {
            aliases[$"f{i}"] = $"F{i}";
        }

        string[] keypad =
        [
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "Enter", "Add", "Subtract", "Multiply", "Divide", "Decimal", "Equal",
            "Home", "Up", "Prior", "Left", "Begin", "Right", "End", "Down", "Next", "Insert", "Delete",
        ];
        foreach (var suffix in keypad)
        {
            aliases[$"kp_{suffix.ToLowerInvariant()}"] = $"KP_{suffix}";
        }

        // Common alternate names that models use but aren't xdotool keysyms.
        aliases["enter"] = "Return";
        aliases["esc"] = "Escape";
        aliases["pageup"] = "Prior";
        aliases["pagedown"] = "Next";
        aliases["arrowleft"] = "Left";
        aliases["arrowup"] = "Up";
        aliases["arrowright"] = "Right";
        aliases["arrowdown"] = "Down";
        // xdotool doesn't recognize "PRINTSCREEN" or "PRTSCR" — it needs the keysym name "Print".
        aliases["printscreen"] = "Print";
        aliases["prtscr"] = "Print";
        // Modifier abbreviations — map to xdotool's built-in aliases
        aliases["ctl"] = "ctrl";
        aliases["control"] = "ctrl";
        aliases["cmd"] = "super";
        aliases["command"] = "super";
        aliases["win"] = "super";
        aliases["windows"] = "super";
        aliases["opt"] = "alt";
        aliases["option"] = "alt";
        // xdotool doesn't recognize "," — it needs the keysym name "comma".
        aliases[","] = "comma";
        return aliases;
    }
}
