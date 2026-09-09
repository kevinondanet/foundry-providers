using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Examples.Approval;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Approval;

// Declared inside the namespace so that `Approval` names the record rather than the enclosing
// InspectAzureAI.Examples.Approval namespace (see the same alias in PythonAllowlist.cs).
using Approval = InspectAzureAI.Eval.Approval.Approval;

/// <summary>
/// Tests for the port of <c>examples/approval/approval.py</c> <c>python_allowlist</c> (<see cref="ExampleApprovers.PythonAllowlist"/>),
/// its registry factory, the <c>python_allowlist</c> entry of <c>approval.json</c> and the <see cref="PythonSyntax"/> scanner
/// that stands in for <c>ast.parse</c> / <c>ast.walk</c>. Decision explanations are asserted verbatim: they are what the
/// Python original writes to the log.
/// </summary>
public sealed class PythonAllowlistTests
{
    /// <summary>The <c>python_allowlist</c> values of <c>approval.yaml</c>.</summary>
    private static readonly string[] DefaultModules = ["math"];

    private static readonly string[] DefaultFunctions = ["print"];

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    /// <summary>A python tool call the way <c>SandboxTools.Python</c> emits it (argument <c>code</c>).</summary>
    private static ToolCall PythonCall(string code, string argument = "code") =>
        new("1", "python", new JsonObject { [argument] = code });

    private static Task<Approval> DecideAsync(ApproverDef approver, ToolCall call) =>
        approver.Approve(string.Empty, call, ToolCallViews.Default(call), Array.Empty<ChatMessage>(), CancellationToken.None);

    private static Task<Approval> DecideAsync(ApproverDef approver, string code) =>
        DecideAsync(approver, PythonCall(code));

    private static ApproverDef Default(
        IReadOnlyList<string>? modules = null,
        IReadOnlyList<string>? functions = null,
        IReadOnlySet<string>? disallowedBuiltins = null,
        IReadOnlySet<string>? sensitiveModules = null,
        bool allowSystemStateModification = false) =>
        ExampleApprovers.PythonAllowlist(
            modules ?? DefaultModules,
            functions ?? DefaultFunctions,
            disallowedBuiltins,
            sensitiveModules,
            allowSystemStateModification);

    private static PythonNode Import(string name) => new(PythonNodeKind.Import, name);

    private static PythonNode ImportFrom(string module) => new(PythonNodeKind.ImportFrom, module);

    private static PythonNode Call(string name) => new(PythonNodeKind.Call, name);

    private static PythonNode Dunder(string attribute) => new(PythonNodeKind.DunderAssignment, attribute);

    // ----------------------------------------------------------------------------------------------------------
    // decisions: the approval.yaml configuration (allowed_modules [math], allowed_functions [print])
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void python_allowlist_is_named_after_the_python_approver()
    {
        Assert.Equal("python_allowlist", Default().Name);
    }

    [Fact]
    public async Task a_call_to_an_allowed_function_is_approved_with_the_python_explanation()
    {
        var approval = await DecideAsync(Default(), "print(\"hello\")");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
        Assert.Null(approval.Modified);
    }

    [Fact]
    public async Task an_allowed_module_and_an_attribute_call_are_approved()
    {
        // math.factorial is an ast.Attribute call: the Python approver only checks calls whose func is an ast.Name.
        var approval = await DecideAsync(Default(), "import math\nprint(math.factorial(5))");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Fact]
    public async Task importing_a_module_outside_the_allowlist_escalates_listing_the_allowed_modules()
    {
        var approval = await DecideAsync(Default(), "import shutil\nshutil.rmtree('/tmp/x')");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Module 'shutil' is not in the allowed list. Allowed modules: math", approval.Explanation);
    }

    [Fact]
    public async Task from_import_of_a_module_outside_the_allowlist_escalates()
    {
        var approval = await DecideAsync(Default(), "from os import path");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Module 'os' is not in the allowed list. Allowed modules: math", approval.Explanation);
    }

    [Fact]
    public async Task an_allowed_but_sensitive_module_escalates_with_the_sensitive_message()
    {
        var approver = Default(modules: ["math", "os"]);

        var approval = await DecideAsync(approver, "import os");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Module 'os' is considered sensitive and not allowed.", approval.Explanation);
    }

    [Theory]
    [InlineData("os")]
    [InlineData("sys")]
    [InlineData("subprocess")]
    [InlineData("socket")]
    [InlineData("requests")]
    public async Task every_default_sensitive_module_escalates_even_when_allowed(string module)
    {
        var approver = Default(modules: ["math", module]);

        Assert.Equal($"Module '{module}' is considered sensitive and not allowed.", (await DecideAsync(approver, $"import {module}")).Explanation);
        Assert.Equal($"Module '{module}' is considered sensitive and not allowed.", (await DecideAsync(approver, $"from {module} import x")).Explanation);
    }

    [Fact]
    public async Task the_allowed_modules_are_listed_in_the_order_given_without_duplicates()
    {
        var approver = Default(modules: ["json", "math", "json"]);

        var approval = await DecideAsync(approver, "import shutil");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Module 'shutil' is not in the allowed list. Allowed modules: json, math", approval.Explanation);
    }

    [Fact]
    public async Task each_alias_of_an_import_statement_is_checked()
    {
        Assert.Equal("Python code is approved.", (await DecideAsync(Default(), "import math as m\nprint(m.pi)")).Explanation);
        Assert.Equal(
            "Module 'shutil' is not in the allowed list. Allowed modules: math",
            (await DecideAsync(Default(), "import math, shutil")).Explanation);
    }

    [Fact]
    public async Task a_dotted_import_is_checked_by_its_full_name_as_in_python()
    {
        // alias.name is "os.path": not in the allowed list, and (as in Python) not the sensitive module "os".
        Assert.Equal(
            "Module 'os.path' is not in the allowed list. Allowed modules: math",
            (await DecideAsync(Default(), "import os.path")).Explanation);
        Assert.Equal(ApprovalDecision.Approve, (await DecideAsync(Default(modules: ["os.path"]), "import os.path")).Decision);
    }

    [Fact]
    public async Task a_relative_import_has_module_none_as_in_python()
    {
        // ast.ImportFrom.module is None for "from . import x"; Python interpolates it as 'None'.
        var approval = await DecideAsync(Default(), "from . import sibling");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Module 'None' is not in the allowed list. Allowed modules: math", approval.Explanation);
    }

    [Fact]
    public async Task calling_a_function_outside_the_allowlist_escalates_listing_the_allowed_functions()
    {
        var approval = await DecideAsync(Default(), "eval(\"1\")");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Function 'eval' is not in the allowed list. Allowed functions: print", approval.Explanation);
    }

    [Fact]
    public async Task an_allowed_disallowed_builtin_escalates_with_the_security_message()
    {
        var approver = Default(functions: ["print", "eval"]);

        var approval = await DecideAsync(approver, "eval(\"1\")");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Built-in function 'eval' is not allowed for security reasons.", approval.Explanation);
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("exec")]
    [InlineData("compile")]
    [InlineData("__import__")]
    [InlineData("open")]
    [InlineData("input")]
    public async Task every_default_disallowed_builtin_escalates_even_when_allowed(string builtin)
    {
        var approver = Default(functions: ["print", builtin]);

        var approval = await DecideAsync(approver, $"{builtin}(\"x\")");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal($"Built-in function '{builtin}' is not allowed for security reasons.", approval.Explanation);
    }

    [Fact]
    public async Task the_allowed_functions_are_listed_in_the_order_given_without_duplicates()
    {
        var approver = Default(functions: ["len", "print", "len"]);

        var approval = await DecideAsync(approver, "eval(\"1\")");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Function 'eval' is not in the allowed list. Allowed functions: len, print", approval.Explanation);
    }

    [Fact]
    public async Task nested_bare_name_calls_are_checked_too()
    {
        var approval = await DecideAsync(Default(), "print(len(\"abc\"))");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Function 'len' is not in the allowed list. Allowed functions: print", approval.Explanation);
    }

    [Fact]
    public async Task a_function_definition_is_not_a_call()
    {
        var approval = await DecideAsync(Default(), "def helper(x):\n    return x");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Fact]
    public async Task calling_a_locally_defined_function_outside_the_allowlist_escalates()
    {
        var approval = await DecideAsync(Default(), "def helper(x):\n    return x\n\nhelper(1)");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Function 'helper' is not in the allowed list. Allowed functions: print", approval.Explanation);
    }

    [Fact]
    public async Task assigning_a_dunder_attribute_escalates_as_system_state_modification()
    {
        var approval = await DecideAsync(Default(), "x.__class__ = y");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Modification of system state (dunder attributes) is not allowed.", approval.Explanation);
    }

    [Fact]
    public async Task assigning_a_dunder_attribute_is_approved_when_system_state_modification_is_allowed()
    {
        var approver = Default(allowSystemStateModification: true);

        var approval = await DecideAsync(approver, "x.__class__ = y");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Theory]
    [InlineData("x.__class__ == y")]
    [InlineData("x.__dict__ += {}")]
    [InlineData("a = b.__class__")]
    [InlineData("obj.__dict__[\"k\"] = 1")]
    [InlineData("x._private = 1")]
    [InlineData("__all__ = []")]
    public async Task only_a_plain_assignment_to_a_dunder_attribute_is_a_system_state_modification(string code)
    {
        // ast.Assign with an ast.Attribute target: a comparison, an AugAssign, a Subscript target, a Name target or
        // a single-underscore attribute never match.
        var approval = await DecideAsync(Default(), code);

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Theory]
    [InlineData("(obj.__a__) = 1")]
    [InlineData("((obj.__a__)) = 1")]
    [InlineData("(a).__b__ = 1")]
    [InlineData("(a, b).__c__ = 1")]
    [InlineData("a[0].__dict__ = {}")]
    [InlineData("a = b.__c__ = 2")]
    [InlineData("x = 1; y.__z__ = 2")]
    [InlineData("if c: x.__a__ = 1")]
    [InlineData("for i in a, b: x.__a__ = 1")]
    [InlineData("x = 1\nif y:\n    z.__k__ = 2")]
    public async Task an_attribute_target_is_found_the_way_ast_finds_it(string code)
    {
        // Each is an ast.Assign whose target is an ast.Attribute with a dunder attr (verified against ast.parse):
        // grouping parentheses, an attribute of any primary, a chained assignment and a compound statement's body.
        var approval = await DecideAsync(Default(functions: ["f"]), code);

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Modification of system state (dunder attributes) is not allowed.", approval.Explanation);
    }

    [Theory]
    [InlineData("x, obj.__dict__ = 1, {}")]
    [InlineData("(a.__b__, c) = 1, 2")]
    [InlineData("a.__b__, = 1,")]
    [InlineData("[a.__b__] = [1]")]
    [InlineData("a.__x__: int = 1")]
    [InlineData("x = y = z.__a__")]
    [InlineData("while a.__b__: c = 1")]
    public async Task a_tuple_list_or_annotated_target_is_not_an_attribute_assignment(string code)
    {
        // Python's targets are an ast.Tuple, ast.List or ast.AnnAssign here (verified against ast.parse), so the
        // approver approves; the dunder attribute on the right-hand side or in a condition is a read.
        var approval = await DecideAsync(Default(), code);

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Theory]
    [InlineData("print(\"import os\")")]
    [InlineData("print('eval(\"1\")')")]
    [InlineData("# import os\nprint(\"hi\")")]
    [InlineData("print(\"hi\")  # eval(\"1\")")]
    [InlineData("'''import os\neval(\"1\")\nx.__class__ = y'''")]
    [InlineData("x = f\"{name}\"")]
    public async Task strings_and_comments_are_not_code(string code)
    {
        var approval = await DecideAsync(Default(), code);

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Theory]
    [InlineData("x = 1")]
    [InlineData("x = .5 + 1.e5")]
    [InlineData("obj.run()")]
    [InlineData("\"abc\".upper()")]
    [InlineData("class Helper(Base):\n    pass")]
    [InlineData("f = lambda: 1")]
    [InlineData("print(end=\"\")")]
    [InlineData("for i in range:\n    print(i)")]
    public async Task code_without_imports_bare_calls_or_dunder_assignments_is_approved(string code)
    {
        var approval = await DecideAsync(Default(), code);

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // decisions: custom builtins and sensitive modules
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task custom_disallowed_builtins_replace_the_defaults()
    {
        var approver = Default(functions: ["print", "open"], disallowedBuiltins: new HashSet<string> { "print" });

        Assert.Equal("Built-in function 'print' is not allowed for security reasons.", (await DecideAsync(approver, "print(1)")).Explanation);
        Assert.Equal(ApprovalDecision.Approve, (await DecideAsync(approver, "open(\"f\")")).Decision);
    }

    [Fact]
    public async Task custom_sensitive_modules_replace_the_defaults()
    {
        var approver = Default(modules: ["math", "os"], sensitiveModules: new HashSet<string> { "math" });

        Assert.Equal("Module 'math' is considered sensitive and not allowed.", (await DecideAsync(approver, "import math")).Explanation);
        Assert.Equal(ApprovalDecision.Approve, (await DecideAsync(approver, "import os")).Decision);
    }

    [Fact]
    public async Task empty_sets_fall_back_to_the_defaults_as_in_python()
    {
        // Python's `disallowed_builtins or {...}` treats an empty set as "not given".
        var approver = Default(
            modules: ["math", "os"],
            functions: ["print", "eval"],
            disallowedBuiltins: new HashSet<string>(),
            sensitiveModules: new HashSet<string>());

        Assert.Equal("Built-in function 'eval' is not allowed for security reasons.", (await DecideAsync(approver, "eval(\"1\")")).Explanation);
        Assert.Equal("Module 'os' is considered sensitive and not allowed.", (await DecideAsync(approver, "import os")).Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // decisions: empty code, syntax errors and the first argument
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public async Task empty_or_blank_code_is_rejected(string code)
    {
        var approval = await DecideAsync(Default(), code);

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Empty code", approval.Explanation);
    }

    [Fact]
    public async Task an_unclosed_bracket_is_rejected_as_invalid_syntax()
    {
        var approval = await DecideAsync(Default(), "print(");

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Invalid Python syntax: '(' was never closed (<unknown>, line 1)", approval.Explanation);
    }

    [Theory]
    [InlineData("print(\"abc)")]
    [InlineData("print('abc)")]
    public async Task an_unterminated_string_is_rejected_as_invalid_syntax(string code)
    {
        var approval = await DecideAsync(Default(), code);

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Invalid Python syntax: unterminated string literal (detected at line 1) (<unknown>, line 1)", approval.Explanation);
    }

    [Fact]
    public async Task a_stray_line_continuation_is_rejected_as_invalid_syntax()
    {
        var approval = await DecideAsync(Default(), "x = 1 \\ 2");

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Invalid Python syntax: unexpected character after line continuation character (<unknown>, line 1)", approval.Explanation);
    }

    [Fact]
    public async Task the_first_argument_is_read_whatever_its_name()
    {
        var approval = await DecideAsync(Default(), PythonCall("print(\"hi\")", argument: "source"));

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Fact]
    public async Task surrounding_whitespace_is_stripped_before_the_code_is_scanned()
    {
        var approval = await DecideAsync(Default(), "  \n print(\"hi\")  \n");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Fact]
    public async Task a_null_first_argument_is_pythons_str_none_and_parses()
    {
        // str(None) is "None", which ast.parse accepts as a bare constant expression.
        var call = new ToolCall("1", "python", new JsonObject { ["code"] = null });

        var approval = await DecideAsync(Default(), call);

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Python code is approved.", approval.Explanation);
    }

    [Fact]
    public async Task a_call_with_no_arguments_is_rejected_as_empty()
    {
        // Deviation documented on ApproverSupport.FirstArgumentText: Python's next(iter(...)) would error the sample.
        var call = new ToolCall("1", "python", new JsonObject());

        var approval = await DecideAsync(Default(), call);

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Empty code", approval.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // params (what the log's config.approval records)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void params_carry_only_the_explicit_arguments()
    {
        Assert.Equal("""{"allowed_modules":["math"],"allowed_functions":["print"]}""", Default().Params.ToJsonString());

        var explicitArguments = ExampleApprovers.PythonAllowlist(
            ["math"],
            ["print"],
            disallowedBuiltins: new HashSet<string> { "exec" },
            sensitiveModules: new HashSet<string> { "socket" },
            allowSystemStateModification: true);
        Assert.Equal(
            """{"allowed_modules":["math"],"allowed_functions":["print"],"disallowed_builtins":["exec"],"sensitive_modules":["socket"],"allow_system_state_modification":true}""",
            explicitArguments.Params.ToJsonString());
    }

    [Fact]
    public async Task params_and_decisions_are_a_copy_of_the_arguments()
    {
        var modules = new List<string> { "math" };
        var approver = ExampleApprovers.PythonAllowlist(modules, ["print"]);
        modules.Add("shutil");

        Assert.Equal("""{"allowed_modules":["math"],"allowed_functions":["print"]}""", approver.Params.ToJsonString());
        Assert.Equal(ApprovalDecision.Escalate, (await DecideAsync(approver, "import shutil")).Decision);
    }

    [Fact]
    public void null_lists_are_argument_null_exceptions()
    {
        Assert.Throws<ArgumentNullException>(() => ExampleApprovers.PythonAllowlist(null!, ["print"]));
        Assert.Throws<ArgumentNullException>(() => ExampleApprovers.PythonAllowlist(["math"], null!));
    }

    // ----------------------------------------------------------------------------------------------------------
    // registry
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_registry_creates_a_python_allowlist_from_policy_params()
    {
        ExampleApprovers.Register();
        var parameters = new JsonObject
        {
            ["allowed_modules"] = new JsonArray("math"),
            ["allowed_functions"] = new JsonArray("print"),
            ["disallowed_builtins"] = new JsonArray("print"),
            ["sensitive_modules"] = new JsonArray("math"),
            ["allow_system_state_modification"] = true,
        };

        var approver = ApproverRegistry.Create("python_allowlist", parameters);

        Assert.Equal("python_allowlist", approver.Name);
        Assert.Equal(parameters.ToJsonString(), approver.Params.ToJsonString());
        Assert.Equal("Built-in function 'print' is not allowed for security reasons.", (await DecideAsync(approver, "print(1)")).Explanation);
        Assert.Equal("Module 'math' is considered sensitive and not allowed.", (await DecideAsync(approver, "import math")).Explanation);
        Assert.Equal(ApprovalDecision.Approve, (await DecideAsync(approver, "x.__class__ = y")).Decision);
    }

    [Fact]
    public async Task the_registry_factory_defaults_the_optional_params()
    {
        var approver = ExampleApprovers.PythonAllowlistFromParams(new JsonObject
        {
            ["allowed_functions"] = new JsonArray("print"),
            ["allowed_modules"] = new JsonArray("math"),
        });

        Assert.Equal("""{"allowed_modules":["math"],"allowed_functions":["print"]}""", approver.Params.ToJsonString());
        Assert.Equal("Python code is approved.", (await DecideAsync(approver, "print(\"hi\")")).Explanation);
        Assert.Equal("Module 'os' is not in the allowed list. Allowed modules: math", (await DecideAsync(approver, "import os")).Explanation);
        Assert.Equal("Modification of system state (dunder attributes) is not allowed.", (await DecideAsync(approver, "x.__class__ = y")).Explanation);

        // The default disallowed builtins and sensitive modules apply when the policy entry omits them.
        var permissive = ExampleApprovers.PythonAllowlistFromParams(new JsonObject
        {
            ["allowed_modules"] = new JsonArray("math", "sys"),
            ["allowed_functions"] = new JsonArray("print", "eval"),
        });
        Assert.Equal("Built-in function 'eval' is not allowed for security reasons.", (await DecideAsync(permissive, "eval(\"1\")")).Explanation);
        Assert.Equal("Module 'sys' is considered sensitive and not allowed.", (await DecideAsync(permissive, "import sys")).Explanation);
    }

    [Fact]
    public async Task an_explicit_false_for_allow_system_state_modification_is_recorded_in_params()
    {
        var approver = ExampleApprovers.PythonAllowlistFromParams(new JsonObject
        {
            ["allowed_modules"] = new JsonArray("math"),
            ["allowed_functions"] = new JsonArray("print"),
            ["allow_system_state_modification"] = false,
        });

        Assert.Equal(
            """{"allowed_modules":["math"],"allowed_functions":["print"],"allow_system_state_modification":false}""",
            approver.Params.ToJsonString());
        Assert.Equal(ApprovalDecision.Escalate, (await DecideAsync(approver, "x.__class__ = y")).Decision);
    }

    [Fact]
    public async Task a_null_optional_param_is_pythons_none_and_defaults()
    {
        var approver = ExampleApprovers.PythonAllowlistFromParams(new JsonObject
        {
            ["allowed_modules"] = new JsonArray("math", "sys"),
            ["allowed_functions"] = new JsonArray("print", "eval"),
            ["disallowed_builtins"] = null,
            ["sensitive_modules"] = null,
            ["allow_system_state_modification"] = null,
        });

        Assert.Equal(
            """{"allowed_modules":["math","sys"],"allowed_functions":["print","eval"],"disallowed_builtins":null,"sensitive_modules":null,"allow_system_state_modification":null}""",
            approver.Params.ToJsonString());
        Assert.Equal("Built-in function 'eval' is not allowed for security reasons.", (await DecideAsync(approver, "eval(\"1\")")).Explanation);
        Assert.Equal("Module 'sys' is considered sensitive and not allowed.", (await DecideAsync(approver, "import sys")).Explanation);
        Assert.Equal(ApprovalDecision.Escalate, (await DecideAsync(approver, "x.__class__ = y")).Decision);
    }

    [Fact]
    public void an_unknown_param_is_an_argument_exception()
    {
        var parameters = new JsonObject
        {
            ["allowed_modules"] = new JsonArray("math"),
            ["allowed_functions"] = new JsonArray("print"),
            ["bogus"] = 1,
        };

        var error = Assert.Throws<ArgumentException>(() => ExampleApprovers.PythonAllowlistFromParams(parameters));
        Assert.Contains("bogus", error.Message);
    }

    [Fact]
    public void an_unknown_param_through_the_registry_is_an_argument_exception()
    {
        ExampleApprovers.Register();
        var parameters = new JsonObject
        {
            ["allowed_modules"] = new JsonArray("math"),
            ["allowed_functions"] = new JsonArray("print"),
            ["bogus"] = 1,
        };

        Assert.Throws<ArgumentException>(() => ApproverRegistry.Create("python_allowlist", parameters));
    }

    [Theory]
    [InlineData("""{"allowed_functions": ["print"]}""", "allowed_modules")]
    [InlineData("""{"allowed_modules": ["math"]}""", "allowed_functions")]
    [InlineData("""{}""", "allowed_modules")]
    public void a_missing_required_param_is_an_argument_exception(string json, string missing)
    {
        var parameters = JsonNode.Parse(json)!.AsObject();

        var error = Assert.Throws<ArgumentException>(() => ExampleApprovers.PythonAllowlistFromParams(parameters));
        Assert.Contains(missing, error.Message);
    }

    [Theory]
    [InlineData("""{"allowed_modules": "math", "allowed_functions": ["print"]}""")]
    [InlineData("""{"allowed_modules": ["math", 1], "allowed_functions": ["print"]}""")]
    [InlineData("""{"allowed_modules": ["math"], "allowed_functions": "print"}""")]
    [InlineData("""{"allowed_modules": ["math"], "allowed_functions": ["print"], "disallowed_builtins": "eval"}""")]
    [InlineData("""{"allowed_modules": ["math"], "allowed_functions": ["print"], "sensitive_modules": {"os": true}}""")]
    [InlineData("""{"allowed_modules": ["math"], "allowed_functions": ["print"], "allow_system_state_modification": "yes"}""")]
    public void wrongly_typed_params_are_argument_exceptions(string json)
    {
        var parameters = JsonNode.Parse(json)!.AsObject();

        Assert.Throws<ArgumentException>(() => ExampleApprovers.PythonAllowlistFromParams(parameters));
    }

    // ----------------------------------------------------------------------------------------------------------
    // approval.json (the JSON form of approval.yaml, copied next to the test assembly)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_example_policy_file_yields_the_python_allowlist_of_approval_yaml()
    {
        ExampleApprovers.Register();
        var path = Path.Combine(AppContext.BaseDirectory, "approval", "approval.json");
        Assert.True(File.Exists(path), $"approval.json was not copied next to the test assembly: {path}");

        var policies = ApprovalPolicies.FromFile(path);

        var policy = Assert.Single(policies, candidate => candidate.Approver.Name == "python_allowlist");
        Assert.Equal("*python*", Assert.Single(policy.Tools));
        Assert.True(policy.ToolsAsString);

        // The python policy carries approval.yaml's allowed_functions/allowed_modules and decides like the direct construction.
        var python = policy.Approver;
        Assert.Equal("""{"allowed_modules":["math"],"allowed_functions":["print"]}""", python.Params.ToJsonString());
        Assert.Equal("Python code is approved.", (await DecideAsync(python, "print(\"hello\")")).Explanation);
        Assert.Equal("Python code is approved.", (await DecideAsync(python, "import math\nprint(math.factorial(5))")).Explanation);
        Assert.Equal(
            "Module 'shutil' is not in the allowed list. Allowed modules: math",
            (await DecideAsync(python, "import shutil\nshutil.rmtree('/tmp/x')")).Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // PythonSyntax (the scanner standing in for ast.parse + ast.walk)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void scan_reports_each_import_alias_by_its_full_dotted_name()
    {
        var scan = PythonSyntax.Scan("import os.path as p, math\nfrom ..pkg.sub import thing as t\nfrom . import sibling");

        Assert.True(scan.IsValid);
        Assert.Null(scan.Error);
        Assert.Equal<PythonNode>([Import("os.path"), Import("math"), ImportFrom("pkg.sub"), ImportFrom("None")], scan.Nodes);
    }

    [Fact]
    public void scan_reports_bare_name_calls_but_not_attribute_calls_or_definitions()
    {
        var scan = PythonSyntax.Scan("def f(x):\n    return g(x.h(1), len(x))\n\nclass C(Base):\n    pass\n\nC().run()\nf(2)");

        Assert.True(scan.IsValid);
        Assert.Equal<PythonNode>([Call("g"), Call("len"), Call("C"), Call("f")], scan.Nodes);
    }

    [Fact]
    public void scan_does_not_report_keywords_as_calls()
    {
        // `not (x)` and `yield from gen()` parse as UnaryOp and YieldFrom: only gen is an ast.Call with a Name func.
        var scan = PythonSyntax.Scan("y = not (x)\nyield from gen()\nawait fetch()");

        Assert.True(scan.IsValid);
        Assert.Equal<PythonNode>([Call("gen"), Call("fetch")], scan.Nodes);
    }

    [Fact]
    public void scan_reports_only_plain_assignments_to_dunder_attributes()
    {
        Assert.Equal<PythonNode>([Dunder("__class__")], PythonSyntax.Scan("x.__class__ = y").Nodes);
        Assert.Equal<PythonNode>([Dunder("__dict__")], PythonSyntax.Scan("a.b.__dict__ = {}").Nodes);
        Assert.Empty(PythonSyntax.Scan("x.__class__ == y").Nodes);
        Assert.Empty(PythonSyntax.Scan("x.__dict__ += {}").Nodes);
        Assert.Empty(PythonSyntax.Scan("x = y.__class__").Nodes);
        Assert.Empty(PythonSyntax.Scan("x.__dict__[\"k\"] = 1").Nodes);
        Assert.Empty(PythonSyntax.Scan("x._p = 1").Nodes);
        Assert.Empty(PythonSyntax.Scan("__all__ = []").Nodes);
    }

    [Fact]
    public void scan_decides_dunder_targets_per_assignment_statement()
    {
        // The target is what stands between the statement start and the `=`, stripped of grouping parentheses:
        // a tuple target is not an attribute; a parenthesised or chained attribute target is.
        Assert.Empty(PythonSyntax.Scan("x, obj.__dict__ = 1, {}").Nodes);
        Assert.Empty(PythonSyntax.Scan("(a.__b__, c) = 1, 2").Nodes);
        Assert.Empty(PythonSyntax.Scan("a.__x__: int = 1").Nodes);
        Assert.Equal<PythonNode>([Dunder("__a__")], PythonSyntax.Scan("(obj.__a__) = 1").Nodes);
        Assert.Equal<PythonNode>([Dunder("__c__")], PythonSyntax.Scan("a = b.__c__ = 2").Nodes);
        Assert.Equal<PythonNode>([Dunder("__b__"), Dunder("__d__")], PythonSyntax.Scan("a.__b__ = c.__d__ = 1").Nodes);
        Assert.Equal<PythonNode>([Call("f"), Dunder("__class__")], PythonSyntax.Scan("f(x).__class__ = y").Nodes);
        Assert.Equal<PythonNode>([Dunder("__z__")], PythonSyntax.Scan("x = 1; y.__z__ = 2").Nodes);
        Assert.Equal<PythonNode>([Dunder("__a__")], PythonSyntax.Scan("if c: x.__a__ = 1").Nodes);
    }

    [Fact]
    public void scan_reports_findings_in_source_order()
    {
        // Deviation documented on PythonSyntax: ast.walk is breadth-first; the scanner reports in source order.
        var scan = PythonSyntax.Scan("import math\nx.__class__ = y\nprint(len(x))\nfrom os import path");

        Assert.Equal<PythonNode>([Import("math"), Dunder("__class__"), Call("print"), Call("len"), ImportFrom("os")], scan.Nodes);
    }

    [Fact]
    public void blanking_replaces_strings_and_comments_with_spaces_keeping_length_and_newlines()
    {
        const string source = "x = \"a # b\"  # c\ny = 'd'";

        var blanked = PythonSyntax.BlankStringsAndComments(source);

        Assert.Equal(source.Length, blanked.Length);
        var lines = blanked.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("x =", lines[0].TrimEnd());
        Assert.Equal(16, lines[0].Length);
        Assert.Equal("y =", lines[1].TrimEnd());
        Assert.Equal(7, lines[1].Length);
    }

    [Fact]
    public void blanking_spans_triple_quoted_strings_with_embedded_quotes_and_newlines()
    {
        const string source = "s = \"\"\"a 'b' \"c\"\n\"d\"\n\"\"\"\nprint(s)";

        var blanked = PythonSyntax.BlankStringsAndComments(source);

        Assert.Equal(source.Length, blanked.Length);
        var lines = blanked.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("s =", lines[0].TrimEnd());
        Assert.Equal(string.Empty, lines[1].Trim());
        Assert.Equal(string.Empty, lines[2].Trim());
        Assert.Equal("print(s)", lines[3]);
        Assert.Equal<PythonNode>([Call("print")], PythonSyntax.Scan(source).Nodes);
    }

    [Theory]
    [InlineData("x = r'\\d+'")]
    [InlineData("x = b\"bytes\"")]
    [InlineData("x = rb'\\x00'")]
    [InlineData("x = f'{name}'")]
    [InlineData("x = u'text'")]
    [InlineData("x = BR'raw'")]
    [InlineData("x = Rf\"\"\"{a}\n{b}\"\"\"")]
    public void blanking_recognises_string_prefixes(string source)
    {
        var blanked = PythonSyntax.BlankStringsAndComments(source);

        Assert.Equal(source.Length, blanked.Length);
        Assert.Equal("x =", blanked.TrimEnd());
        Assert.Empty(PythonSyntax.Scan(source).Nodes);
    }

    [Fact]
    public void blanking_does_not_mistake_an_identifier_before_a_quote_for_a_prefix()
    {
        // `print` is longer than any prefix: the call is reported and only the literal is blanked.
        var blanked = PythonSyntax.BlankStringsAndComments("print'x'");

        Assert.Equal("print   ", blanked);
    }

    [Theory]
    [InlineData("print('it\\'s')")]
    [InlineData("print(\"say \\\"hi\\\"\")")]
    [InlineData("print('a\\\\')")]
    [InlineData("print(r'\\'')")]
    public void blanking_honours_escaped_quotes_inside_strings(string source)
    {
        var scan = PythonSyntax.Scan(source);

        Assert.True(scan.IsValid);
        Assert.Equal<PythonNode>([Call("print")], scan.Nodes);
    }

    [Fact]
    public void expressions_inside_f_strings_are_blanked_with_the_string()
    {
        // Deviation documented on PythonSyntax: the eval nested in the f-string is not reported.
        var scan = PythonSyntax.Scan("print(f\"{eval('1')}\")");

        Assert.True(scan.IsValid);
        Assert.Equal<PythonNode>([Call("print")], scan.Nodes);
    }

    [Fact]
    public void a_backslash_line_continuation_joins_lines()
    {
        var scan = PythonSyntax.Scan("total = 1 + \\\n    2\nprint(total)");

        Assert.True(scan.IsValid);
        Assert.Equal<PythonNode>([Call("print")], scan.Nodes);
    }

    [Fact]
    public void a_backslash_line_continuation_inside_a_string_continues_the_string()
    {
        const string source = "s = 'abc\\\ndef'\nprint(s)";

        var blanked = PythonSyntax.BlankStringsAndComments(source);
        var scan = PythonSyntax.Scan(source);

        Assert.Equal(source.Length, blanked.Length);
        Assert.Equal(3, blanked.Split('\n').Length);
        Assert.True(scan.IsValid);
        Assert.Equal<PythonNode>([Call("print")], scan.Nodes);
    }

    [Fact]
    public void newlines_inside_brackets_do_not_end_a_statement()
    {
        var scan = PythonSyntax.Scan("print(\n    len(\n        x))");

        Assert.True(scan.IsValid);
        Assert.Equal<PythonNode>([Call("print"), Call("len")], scan.Nodes);
    }

    [Theory]
    [InlineData("print(", "'(' was never closed (<unknown>, line 1)")]
    [InlineData("x = 1\nprint(", "'(' was never closed (<unknown>, line 2)")]
    [InlineData("print(]", "closing parenthesis ']' does not match opening parenthesis '(' (<unknown>, line 1)")]
    [InlineData("x = 1)", "unmatched ')' (<unknown>, line 1)")]
    [InlineData("print('abc)", "unterminated string literal (detected at line 1) (<unknown>, line 1)")]
    [InlineData("x = 1\ny = \"abc", "unterminated string literal (detected at line 2) (<unknown>, line 2)")]
    [InlineData("print('it\\'s)", "unterminated string literal (detected at line 1); perhaps you escaped the end quote? (<unknown>, line 1)")]
    [InlineData("s = \"\"\"abc\n", "unterminated triple-quoted string literal (detected at line 2) (<unknown>, line 1)")]
    [InlineData("x = 1 \\ 2", "unexpected character after line continuation character (<unknown>, line 1)")]
    public void scan_reports_syntax_errors_with_cpythons_messages(string source, string error)
    {
        var scan = PythonSyntax.Scan(source);

        Assert.False(scan.IsValid);
        Assert.Equal(error, scan.Error);
        Assert.Empty(scan.Nodes);
    }

    [Fact]
    public void scan_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => PythonSyntax.Scan(null!));
        Assert.Throws<ArgumentNullException>(() => PythonSyntax.BlankStringsAndComments(null!));
    }
}
