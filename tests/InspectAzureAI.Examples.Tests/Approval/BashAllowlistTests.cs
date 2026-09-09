using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Examples.Approval;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Approval;

// Declared inside the namespace so that `Approval` names the record rather than the enclosing
// InspectAzureAI.Examples.Approval namespace (see the same alias in BashAllowlist.cs).
using Approval = InspectAzureAI.Eval.Approval.Approval;

/// <summary>
/// Tests for the port of <c>examples/approval/approval.py</c> <c>bash_allowlist</c> (<see cref="ExampleApprovers.BashAllowlist"/>),
/// its registry factory, the policy file <c>approval.json</c> and the <see cref="Shlex"/> port of Python's <c>shlex.split</c>.
/// Decision explanations are asserted verbatim: they are what the Python original writes to the log.
/// </summary>
public sealed class BashAllowlistTests
{
    private static readonly string[] DefaultAllowed = ["ls", "echo", "cat"];

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    /// <summary>A bash tool call the way <c>SandboxTools.Bash</c> emits it (argument <c>cmd</c>).</summary>
    private static ToolCall BashCall(string command, string argument = "cmd") =>
        new("1", "bash", new JsonObject { [argument] = command });

    private static Task<Approval> DecideAsync(ApproverDef approver, ToolCall call) =>
        approver.Approve(string.Empty, call, ToolCallViews.Default(call), Array.Empty<ChatMessage>(), CancellationToken.None);

    private static Task<Approval> DecideAsync(ApproverDef approver, string command) =>
        DecideAsync(approver, BashCall(command));

    private static ApproverDef Default(bool allowSudo = false, IReadOnlyDictionary<string, IReadOnlyList<string>>? rules = null) =>
        ExampleApprovers.BashAllowlist(DefaultAllowed, allowSudo, rules);

    // ----------------------------------------------------------------------------------------------------------
    // decisions
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void bash_allowlist_is_named_after_the_python_approver()
    {
        Assert.Equal("bash_allowlist", Default().Name);
    }

    [Fact]
    public async Task an_allowed_command_is_approved_with_the_python_explanation()
    {
        var approval = await DecideAsync(Default(), "ls -la");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Command 'ls -la' is approved.", approval.Explanation);
        Assert.Null(approval.Modified);
    }

    [Fact]
    public async Task a_command_outside_the_allowlist_escalates_listing_the_allowed_commands()
    {
        var approval = await DecideAsync(Default(), "rm -rf /tmp/x");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Command 'rm' is not in the allowed list. Allowed commands: ls, echo, cat", approval.Explanation);
    }

    [Fact]
    public async Task the_allowed_commands_are_listed_in_the_order_given_without_duplicates()
    {
        var approver = ExampleApprovers.BashAllowlist(["cat", "ls", "cat", "echo"]);

        var approval = await DecideAsync(approver, "rm x");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Command 'rm' is not in the allowed list. Allowed commands: cat, ls, echo", approval.Explanation);
    }

    [Theory]
    [InlineData("cat a | grep b", "|")]
    [InlineData("echo $(id)", "$, (, )")]
    [InlineData("ls; rm -rf /", ";")]
    [InlineData("ls && echo done", "&")]
    [InlineData("cat < in > out", ">, <")]
    [InlineData("echo `id`", "`")]
    public async Task shell_metacharacters_are_rejected_naming_them_in_python_order(string command, string characters)
    {
        var approval = await DecideAsync(Default(), command);

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal($"Command contains potentially dangerous characters: {characters}", approval.Explanation);
    }

    [Fact]
    public async Task dangerous_characters_are_checked_even_when_quoted()
    {
        // Python checks the raw command text, not the tokens: a quoted pipe still rejects.
        var approval = await DecideAsync(Default(), "echo 'a | b'");

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Command contains potentially dangerous characters: |", approval.Explanation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task an_empty_or_blank_command_is_rejected(string command)
    {
        var approval = await DecideAsync(Default(), command);

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Empty command", approval.Explanation);
    }

    [Fact]
    public async Task a_call_with_no_arguments_is_rejected_as_empty()
    {
        // Deviation documented on ApproverSupport.FirstArgumentText: Python's next(iter(...)) would error the sample.
        var call = new ToolCall("1", "bash", new JsonObject());

        var approval = await DecideAsync(Default(), call);

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Empty command", approval.Explanation);
    }

    [Theory]
    [InlineData(null, "None")]
    [InlineData(true, "True")]
    [InlineData(false, "False")]
    public async Task a_non_string_first_argument_reads_as_pythons_str(bool? argument, string command)
    {
        // str(None) / str(True) become the command text, which is then not an allowed command.
        var call = new ToolCall("1", "bash", new JsonObject { ["cmd"] = argument });

        var approval = await DecideAsync(Default(), call);

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal($"Command '{command}' is not in the allowed list. Allowed commands: ls, echo, cat", approval.Explanation);
    }

    [Fact]
    public async Task the_first_argument_is_read_whatever_its_name()
    {
        var approval = await DecideAsync(Default(), BashCall("ls -la", argument: "command"));

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Command 'ls -la' is approved.", approval.Explanation);
    }

    [Fact]
    public async Task surrounding_whitespace_is_stripped_before_the_command_is_quoted_back()
    {
        var approval = await DecideAsync(Default(), "  ls -la  ");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Command 'ls -la' is approved.", approval.Explanation);
    }

    [Theory]
    [InlineData("echo 'unclosed")]
    [InlineData("echo \"unclosed")]
    public async Task an_unclosed_quote_is_rejected_as_invalid_syntax(string command)
    {
        var approval = await DecideAsync(Default(), command);

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Invalid command syntax: No closing quotation", approval.Explanation);
    }

    [Fact]
    public async Task a_trailing_backslash_is_rejected_as_invalid_syntax()
    {
        var approval = await DecideAsync(Default(), "echo abc\\");

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Invalid command syntax: No escaped character", approval.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // sudo
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sudo ls")]
    [InlineData("sudo")]
    public async Task sudo_is_rejected_by_default(string command)
    {
        var approval = await DecideAsync(Default(), command);

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("sudo is not allowed", approval.Explanation);
    }

    [Fact]
    public async Task sudo_with_an_allowed_command_is_approved_when_sudo_is_allowed()
    {
        var approval = await DecideAsync(Default(allowSudo: true), "sudo ls");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Command 'sudo ls' is approved.", approval.Explanation);
    }

    [Fact]
    public async Task bare_sudo_is_an_invalid_sudo_command_when_sudo_is_allowed()
    {
        var approval = await DecideAsync(Default(allowSudo: true), "sudo");

        Assert.Equal(ApprovalDecision.Reject, approval.Decision);
        Assert.Equal("Invalid sudo command", approval.Explanation);
    }

    [Fact]
    public async Task sudo_with_a_disallowed_command_escalates_on_the_real_command()
    {
        var approval = await DecideAsync(Default(allowSudo: true), "sudo rm -rf /tmp/x");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("Command 'rm' is not in the allowed list. Allowed commands: ls, echo, cat", approval.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // command-specific rules
    // ----------------------------------------------------------------------------------------------------------

    private static ApproverDef GitStatusOnly() =>
        ExampleApprovers.BashAllowlist(
            ["git", "ls"],
            commandSpecificRules: new Dictionary<string, IReadOnlyList<string>> { ["git"] = ["status"] });

    [Fact]
    public async Task a_subcommand_outside_the_command_rules_escalates()
    {
        var approval = await DecideAsync(GitStatusOnly(), "git commit -m x");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("git subcommand 'commit' is not allowed. Allowed subcommands: status", approval.Explanation);
    }

    [Fact]
    public async Task an_allowed_subcommand_is_approved()
    {
        var approval = await DecideAsync(GitStatusOnly(), "git status");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Command 'git status' is approved.", approval.Explanation);
    }

    [Fact]
    public async Task a_ruled_command_with_no_subcommand_is_approved()
    {
        var approval = await DecideAsync(GitStatusOnly(), "git");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Command 'git' is approved.", approval.Explanation);
    }

    [Fact]
    public async Task rules_only_apply_to_the_command_they_name()
    {
        var approval = await DecideAsync(GitStatusOnly(), "ls commit");

        Assert.Equal(ApprovalDecision.Approve, approval.Decision);
        Assert.Equal("Command 'ls commit' is approved.", approval.Explanation);
    }

    [Fact]
    public async Task sudo_rules_apply_after_sudo_is_stripped()
    {
        var approver = ExampleApprovers.BashAllowlist(
            ["git"],
            allowSudo: true,
            commandSpecificRules: new Dictionary<string, IReadOnlyList<string>> { ["git"] = ["status"] });

        var approval = await DecideAsync(approver, "sudo git push");

        Assert.Equal(ApprovalDecision.Escalate, approval.Decision);
        Assert.Equal("git subcommand 'push' is not allowed. Allowed subcommands: status", approval.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // params (what the log's config.approval records)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void params_carry_only_the_explicit_arguments()
    {
        var defaults = Default();
        Assert.Equal("""{"allowed_commands":["ls","echo","cat"]}""", defaults.Params.ToJsonString());

        var explicitArguments = ExampleApprovers.BashAllowlist(
            ["git"],
            allowSudo: true,
            commandSpecificRules: new Dictionary<string, IReadOnlyList<string>> { ["git"] = ["status", "log"] });
        Assert.Equal(
            """{"allowed_commands":["git"],"allow_sudo":true,"command_specific_rules":{"git":["status","log"]}}""",
            explicitArguments.Params.ToJsonString());
    }

    [Fact]
    public void params_are_a_copy_of_the_arguments()
    {
        var allowed = new List<string> { "ls" };
        var approver = ExampleApprovers.BashAllowlist(allowed);
        allowed.Add("rm");

        Assert.Equal("""{"allowed_commands":["ls"]}""", approver.Params.ToJsonString());
    }

    // ----------------------------------------------------------------------------------------------------------
    // registry
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void register_makes_bash_allowlist_available_and_is_idempotent()
    {
        ExampleApprovers.Register();
        ExampleApprovers.Register();

        Assert.True(ApproverRegistry.IsRegistered("bash_allowlist"));
        Assert.True(ApproverRegistry.IsRegistered("python_allowlist"));
    }

    [Fact]
    public async Task the_registry_creates_a_bash_allowlist_from_policy_params()
    {
        ExampleApprovers.Register();
        var parameters = new JsonObject
        {
            ["allowed_commands"] = new JsonArray("git"),
            ["allow_sudo"] = true,
            ["command_specific_rules"] = new JsonObject { ["git"] = new JsonArray("status") },
        };

        var approver = ApproverRegistry.Create("bash_allowlist", parameters);

        Assert.Equal("bash_allowlist", approver.Name);
        Assert.Equal(parameters.ToJsonString(), approver.Params.ToJsonString());
        Assert.Equal(ApprovalDecision.Approve, (await DecideAsync(approver, "sudo git status")).Decision);
        Assert.Equal(ApprovalDecision.Escalate, (await DecideAsync(approver, "git push")).Decision);
        Assert.Equal(ApprovalDecision.Escalate, (await DecideAsync(approver, "ls")).Decision);
    }

    [Fact]
    public async Task the_registry_factory_defaults_the_optional_params()
    {
        var approver = ExampleApprovers.BashAllowlistFromParams(new JsonObject { ["allowed_commands"] = new JsonArray("ls", "echo", "cat") });

        Assert.Equal("""{"allowed_commands":["ls","echo","cat"]}""", approver.Params.ToJsonString());
        Assert.Equal("sudo is not allowed", (await DecideAsync(approver, "sudo ls")).Explanation);
        Assert.Equal(ApprovalDecision.Approve, (await DecideAsync(approver, "ls -la")).Decision);
    }

    [Fact]
    public async Task an_explicit_false_for_allow_sudo_is_recorded_in_params()
    {
        // registry_params records every parameter the policy entry gives, an explicit default included.
        var approver = ExampleApprovers.BashAllowlistFromParams(new JsonObject
        {
            ["allowed_commands"] = new JsonArray("ls"),
            ["allow_sudo"] = false,
        });

        Assert.Equal("""{"allowed_commands":["ls"],"allow_sudo":false}""", approver.Params.ToJsonString());
        Assert.Equal("sudo is not allowed", (await DecideAsync(approver, "sudo ls")).Explanation);
    }

    [Fact]
    public async Task a_null_optional_param_is_pythons_none_and_defaults()
    {
        var approver = ExampleApprovers.BashAllowlistFromParams(new JsonObject
        {
            ["allowed_commands"] = new JsonArray("git"),
            ["allow_sudo"] = null,
            ["command_specific_rules"] = null,
        });

        Assert.Equal("""{"allowed_commands":["git"],"allow_sudo":null,"command_specific_rules":null}""", approver.Params.ToJsonString());
        Assert.Equal("sudo is not allowed", (await DecideAsync(approver, "sudo git status")).Explanation);
        Assert.Equal("Command 'git push' is approved.", (await DecideAsync(approver, "git push")).Explanation);
    }

    [Fact]
    public void an_unknown_param_is_an_argument_exception()
    {
        var parameters = new JsonObject { ["allowed_commands"] = new JsonArray("ls"), ["bogus"] = 1 };

        var error = Assert.Throws<ArgumentException>(() => ExampleApprovers.BashAllowlistFromParams(parameters));
        Assert.Contains("bogus", error.Message);
    }

    [Fact]
    public void an_unknown_param_through_the_registry_is_an_argument_exception()
    {
        ExampleApprovers.Register();
        var parameters = new JsonObject { ["allowed_commands"] = new JsonArray("ls"), ["bogus"] = 1 };

        Assert.Throws<ArgumentException>(() => ApproverRegistry.Create("bash_allowlist", parameters));
    }

    [Fact]
    public void missing_allowed_commands_is_an_argument_exception()
    {
        var error = Assert.Throws<ArgumentException>(() => ExampleApprovers.BashAllowlistFromParams(new JsonObject()));
        Assert.Contains("allowed_commands", error.Message);
    }

    [Theory]
    [InlineData("""{"allowed_commands": "ls"}""")]
    [InlineData("""{"allowed_commands": ["ls", 1]}""")]
    [InlineData("""{"allowed_commands": ["ls"], "allow_sudo": "yes"}""")]
    [InlineData("""{"allowed_commands": ["ls"], "command_specific_rules": ["git"]}""")]
    [InlineData("""{"allowed_commands": ["ls"], "command_specific_rules": {"git": "status"}}""")]
    public void wrongly_typed_params_are_argument_exceptions(string json)
    {
        var parameters = JsonNode.Parse(json)!.AsObject();

        Assert.Throws<ArgumentException>(() => ExampleApprovers.BashAllowlistFromParams(parameters));
    }

    // ----------------------------------------------------------------------------------------------------------
    // approval.json (the JSON form of approval.yaml, copied next to the test assembly)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_example_policy_file_yields_the_three_python_policies()
    {
        ExampleApprovers.Register();
        var path = Path.Combine(AppContext.BaseDirectory, "approval", "approval.json");
        Assert.True(File.Exists(path), $"approval.json was not copied next to the test assembly: {path}");

        var policies = ApprovalPolicies.FromFile(path);

        Assert.Equal(3, policies.Count);
        Assert.Equal(["bash_allowlist", "python_allowlist", "human"], policies.Select(policy => policy.Approver.Name));
        Assert.Equal("*bash*", Assert.Single(policies[0].Tools));
        Assert.Equal("*python*", Assert.Single(policies[1].Tools));
        Assert.Equal("*", Assert.Single(policies[2].Tools));
        Assert.All(policies, policy => Assert.True(policy.ToolsAsString));

        // The bash policy carries approval.yaml's allowed_commands and decides like the direct construction.
        var bash = policies[0].Approver;
        Assert.Equal("""{"allowed_commands":["ls","echo","cat"]}""", bash.Params.ToJsonString());
        Assert.Equal("Command 'ls -la' is approved.", (await DecideAsync(bash, "ls -la")).Explanation);
        Assert.Equal(
            "Command 'rm' is not in the allowed list. Allowed commands: ls, echo, cat",
            (await DecideAsync(bash, "rm -rf /tmp/x")).Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // Shlex (port of shlex.split)
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ls -la", new[] { "ls", "-la" })]
    [InlineData("  ls   -la\t/tmp\n", new[] { "ls", "-la", "/tmp" })]
    [InlineData("echo 'hello world'", new[] { "echo", "hello world" })]
    [InlineData("echo \"hello world\"", new[] { "echo", "hello world" })]
    [InlineData("echo 'it\"s'", new[] { "echo", "it\"s" })]
    [InlineData("echo \"it's\"", new[] { "echo", "it's" })]
    public void shlex_splits_on_whitespace_and_honours_quotes(string command, string[] expected)
    {
        Assert.Equal(expected, Shlex.Split(command));
    }

    [Theory]
    [InlineData("echo hello\\ world", new[] { "echo", "hello world" })]
    [InlineData("echo \\\"x\\\"", new[] { "echo", "\"x\"" })]
    [InlineData("echo \\'x\\'", new[] { "echo", "'x'" })]
    [InlineData("echo a\\\\b", new[] { "echo", "a\\b" })]
    [InlineData("echo a\\\nb", new[] { "echo", "a\nb" })]
    public void shlex_backslash_outside_quotes_escapes_the_next_character(string command, string[] expected)
    {
        Assert.Equal(expected, Shlex.Split(command));
    }

    [Theory]
    [InlineData("echo \"a\\\"b\"", new[] { "echo", "a\"b" })]
    [InlineData("echo \"a\\\\b\"", new[] { "echo", "a\\b" })]
    [InlineData("echo \"a\\nb\"", new[] { "echo", "a\\nb" })]
    [InlineData("echo \"a\\$b\"", new[] { "echo", "a\\$b" })]
    [InlineData("echo \"a\\'b\"", new[] { "echo", "a\\'b" })]
    public void shlex_backslash_inside_double_quotes_escapes_only_quote_and_backslash(string command, string[] expected)
    {
        Assert.Equal(expected, Shlex.Split(command));
    }

    [Theory]
    [InlineData("echo 'a\\b'", new[] { "echo", "a\\b" })]
    [InlineData("echo 'a\\'", new[] { "echo", "a\\" })]
    public void shlex_backslash_inside_single_quotes_is_literal(string command, string[] expected)
    {
        Assert.Equal(expected, Shlex.Split(command));
    }

    [Theory]
    [InlineData("echo a'b'c\"d\"e", new[] { "echo", "abcde" })]
    [InlineData("echo \"foo\"bar", new[] { "echo", "foobar" })]
    [InlineData("echo foo'bar baz'", new[] { "echo", "foobar baz" })]
    [InlineData("echo 'a'\"b\"", new[] { "echo", "ab" })]
    public void shlex_joins_adjacent_quoted_and_unquoted_parts(string command, string[] expected)
    {
        Assert.Equal(expected, Shlex.Split(command));
    }

    [Theory]
    [InlineData("echo ''", new[] { "echo", "" })]
    [InlineData("echo \"\"", new[] { "echo", "" })]
    [InlineData("'' ''", new[] { "", "" })]
    public void shlex_empty_quotes_yield_an_empty_token(string command, string[] expected)
    {
        Assert.Equal(expected, Shlex.Split(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t\r\n")]
    public void shlex_of_blank_input_is_empty(string command)
    {
        Assert.Empty(Shlex.Split(command));
    }

    [Theory]
    [InlineData("echo 'abc")]
    [InlineData("echo \"abc")]
    [InlineData("echo \"abc\\\"")]
    public void shlex_reports_an_unclosed_quote_with_pythons_message(string command)
    {
        var error = Assert.Throws<FormatException>(() => Shlex.Split(command));
        Assert.Equal("No closing quotation", error.Message);
    }

    [Theory]
    [InlineData("echo abc\\")]
    [InlineData("\\")]
    public void shlex_reports_a_trailing_backslash_with_pythons_message(string command)
    {
        var error = Assert.Throws<FormatException>(() => Shlex.Split(command));
        Assert.Equal("No escaped character", error.Message);
    }

    [Fact]
    public void shlex_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => Shlex.Split(null!));
    }
}
