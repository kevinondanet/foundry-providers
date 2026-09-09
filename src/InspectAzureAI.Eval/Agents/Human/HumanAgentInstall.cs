using System.Text;
using InspectAzureAI.Eval.Agents.Human.Commands;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Agents.Human;

/// <summary>
/// Port of <c>agent/_human/install.py</c>: generates the <c>task.py</c> CLI (argparse over the commands' CLI
/// halves, calling the <c>human_agent</c> sandbox service), the <c>.bashrc</c> fragment (the <c>task</c> alias
/// and completions, optional <c>script</c> session recording, instructions on first login, <c>task start</c>)
/// and <c>install.sh</c>, and runs them in the sandbox: the files are written to a staging directory with
/// <c>tee</c>, <c>install.sh</c> copies <c>task.py</c> to <c>/opt/human_agent</c> and appends the fragment to the
/// login user's <c>.bashrc</c>, and the staging directory is removed. A second install (the <c>mkdir</c> of
/// <c>/opt/human_agent</c> failing) is a no-op.
/// </summary>
public static class HumanAgentInstall
{
    public const string InstallDir = "human_agent_install";

    public const string HumanAgentDir = "/opt/human_agent";

    public const string TaskPy = "task.py";

    public const string InstallSh = "install.sh";

    public const string Bashrc = ".bashrc";

    public const string RecordSessionDir = "/var/tmp/user-sessions";

    /// <summary>Port of <c>install_human_agent</c>; returns false when the agent was already installed.</summary>
    public static async Task<bool> InstallAsync(
        ISandboxEnvironment sandbox,
        string? user,
        IReadOnlyList<HumanAgentCommand> commands,
        string? bashrcContent,
        bool recordSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(commands);

        // see if we have already installed
        var created = await sandbox.ExecAsync(["mkdir", HumanAgentDir], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created.Success)
        {
            return false;
        }

        if (string.IsNullOrEmpty(user))
        {
            user = (await sandbox.ExecAsync(["whoami"], cancellationToken: cancellationToken).ConfigureAwait(false)).Stdout.Trim();
        }

        if (user != "root")
        {
            await CheckedExecAsync(sandbox, ["chown", user, HumanAgentDir], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // setup installation directory
        await CheckedExecAsync(sandbox, ["mkdir", "-p", InstallDir], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (user != "root")
        {
            await CheckedExecAsync(sandbox, ["chown", user, InstallDir], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // generate task.py
        await CheckedWriteFileAsync(sandbox, $"{InstallDir}/{TaskPy}", TaskScript(commands), executable: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        // generate .bashrc
        await CheckedWriteFileAsync(sandbox, $"{InstallDir}/{Bashrc}", BashrcScript(commands, bashrcContent, recordSession), executable: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        // write and run installation script
        await CheckedWriteFileAsync(sandbox, $"{InstallDir}/{InstallSh}", InstallScript(user), executable: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        await CheckedExecAsync(sandbox, ["bash", $"./{InstallSh}"], cwd: InstallDir, cancellationToken: cancellationToken).ConfigureAwait(false);
        await CheckedExecAsync(sandbox, ["rm", "-rf", InstallDir], cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Port of <c>human_agent_commands(commands)</c>: the generated <c>task.py</c>.</summary>
    public static string TaskScript(IReadOnlyList<HumanAgentCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        // filter out hidden commands
        var cli = commands.Where(command => command.RunsIn(HumanAgentCommandContext.Cli)).ToList();

        // standard imports (including any dependencies that call methods carry)
        const string Imports = """

            import argparse
            import sys
            from argparse import Namespace
            from pathlib import Path

            sys.path.append("/var/tmp/sandbox-services/human_agent")
            from human_agent import call_human_agent

            def format_time(t):
                minutes, seconds = divmod(t, 60)
                hours, minutes = divmod(minutes, 60)
                return f"{hours:.0f}:{minutes:02.0f}:{seconds:02.0f}"

            """;

        // command handler source code
        var handlers = string.Join(
            "\n\n",
            cli.Select(command =>
                (command.CliSource ?? throw new InvalidOperationException($"Human agent command '{command.Name}' runs in the cli context but has no CliSource."))
                .TrimEnd('\n') + "\n"));

        // parse commands
        var parsers = new List<string>();
        foreach (var command in cli)
        {
            parsers.Add($"{command.Name}_parser = subparsers.add_parser(\"{command.Name}\", help=\"{command.Description}\")\n");
            foreach (var arg in command.CliArgs)
            {
                var extras = arg.Name.StartsWith("--", StringComparison.Ordinal)
                    ? "action=\"store_true\", default=False"
                    : $"nargs={(arg.Required ? "1" : "\"?\"")}";
                parsers.Add($"{command.Name}_parser.add_argument(\"{arg.Name}\", {extras}, help=\"{arg.Description}\")");
            }
        }

        var parse = "\nparser = argparse.ArgumentParser(description=\"Human agent tools.\")\nsubparsers = parser.add_subparsers(dest=\"command\")\n"
            + "\n" + string.Join("\n", parsers);

        // dispatch commands
        var dispatchers = cli
            .Select((command, index) => $"{(index == 0 ? "if" : "elif")} command == \"{command.Name}\": {command.Name}(args)")
            .Append("else: parser.print_help()");
        var dispatch = "\nargs = parser.parse_args()\ncommand = args.command\ndelattr(args, 'command')\n" + string.Join("\n", dispatchers);

        return string.Join("\n", [Imports, handlers, parse, dispatch]) + "\n";
    }

    /// <summary>Port of <c>human_agent_bashrc</c>: the fragment appended to the login user's <c>.bashrc</c>.</summary>
    public static string BashrcScript(IReadOnlyList<HumanAgentCommand> commands, string? bashrcContent, bool recordSession)
    {
        ArgumentNullException.ThrowIfNull(commands);

        // only run in interactive terminals
        const string TerminalCheck = """


            ### Inspect Human Agent Setup #########################################=

            # only run if shell is interactive
            case $- in
                *i*) ;;
                *) return ;;
            esac

            # only run if attached to a terminal
            if ! tty -s; then
                return
            fi

            """;

        // shell alias and completions
        var commandNames = string.Join(" ", commands.Where(command => command.RunsIn(HumanAgentCommandContext.Cli)).Select(command => command.Name));
        var shell = $$"""

            # shell alias for human agent commands
            alias task='python3 {{HumanAgentDir}}/{{TaskPy}}'

            # completion handler
            _task_completion() {
                local cur
                cur="${COMP_WORDS[COMP_CWORD]}"
                if [ "$COMP_CWORD" -eq 1 ]; then
                    local commands="{{commandNames}}"

                    # Generate completion matches
                    COMPREPLY=($(compgen -W "${commands}" -- ${cur}))
                fi
            }
            complete -F _task_completion task

            """;

        if (!string.IsNullOrEmpty(bashrcContent))
        {
            shell = $"{shell}\n\n{bashrcContent}";
        }

        // session recording
        var recording = recordSession
            ? $$"""

                # record human agent session transcript
                if [ -z "$SCRIPT_RUNNING" ]; then
                    export SCRIPT_RUNNING=1
                    LOGDIR={{RecordSessionDir}}
                    mkdir -p "$LOGDIR"
                    TIMESTAMP=$(date +%Y%m%d_%H%M%S)
                    INPUTFILE="$LOGDIR/$(whoami)_$TIMESTAMP.input"
                    OUTPUTFILE="$LOGDIR/$(whoami)_$TIMESTAMP.output"
                    TIMINGFILE="$LOGDIR/$(whoami)_$TIMESTAMP.timing"
                    exec script -q -f -m advanced -I "$INPUTFILE" -O "$OUTPUTFILE" -T "$TIMINGFILE" -c "bash --login -i"
                fi

                """
            : "";

        // display task instructions
        const string Instructions = """
            if [ -z "$INSTRUCTIONS_SHOWN" ]; then
                export INSTRUCTIONS_SHOWN=1
                task instructions > ~/instructions.txt
                cat ~/instructions.txt
            fi

            """;

        const string Clock = "task start\n";

        return string.Join("\n", [TerminalCheck, shell, recording, Instructions, Clock]);
    }

    /// <summary>Port of <c>human_agent_install_sh</c>.</summary>
    public static string InstallScript(string? user) => $$"""

        #!/usr/bin/env bash

        # create installation directory
        HUMAN_AGENT="{{HumanAgentDir}}"
        mkdir -p $HUMAN_AGENT

        # copy command script
        cp {{TaskPy}} $HUMAN_AGENT

        # get user's home directory
        USER="{{user ?? ""}}"
        if [ -z "$USER" ]; then
            USER=$(whoami)
        fi
        USER_HOME=$(getent passwd $USER | cut -d: -f6)

        # append to user's .bashrc
        cat {{Bashrc}} >> $USER_HOME/{{Bashrc}}

        """;

    /// <summary>Port of <c>checked_exec</c>: a failed command is an <see cref="InvalidOperationException"/> carrying its stderr.</summary>
    internal static async Task<string> CheckedExecAsync(
        ISandboxEnvironment sandbox,
        IReadOnlyList<string> cmd,
        string? input = null,
        string? cwd = null,
        string? user = null,
        CancellationToken cancellationToken = default)
    {
        var result = await sandbox.ExecAsync(cmd, input: input, cwd: cwd, user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error executing command {string.Join(" ", cmd)}: {result.Stderr}");
        }

        return result.Stdout;
    }

    /// <summary>Port of <c>checked_write_file</c>: <c>tee</c> the contents, optionally <c>chown</c> and <c>chmod +x</c>.</summary>
    internal static async Task CheckedWriteFileAsync(
        ISandboxEnvironment sandbox,
        string file,
        string contents,
        bool executable = false,
        string? user = null,
        CancellationToken cancellationToken = default)
    {
        await CheckedExecAsync(sandbox, ["tee", "--", file], input: contents, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (user is not null)
        {
            await CheckedExecAsync(sandbox, ["chown", user, file], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (executable)
        {
            await CheckedExecAsync(sandbox, ["chmod", "+x", file], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
