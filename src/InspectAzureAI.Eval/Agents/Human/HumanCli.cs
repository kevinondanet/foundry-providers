using InspectAzureAI.Eval.Agents.Human.Commands;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Agents.Human;

/// <summary>
/// Port of <c>agent/_human/agent.py</c> <c>human_cli</c>: an agent that installs the <c>task</c> command set in
/// the sample's default sandbox, prints the login command, and serves the commands a person runs there until
/// they submit or quit. One human agent interaction runs at a time (a lock serialises samples). Deviation:
/// Python's <c>answer: bool | str</c> is <c>answer</c> plus <c>answerPattern</c>; the Textual panel and the VS
/// Code login links are not ported (the <see cref="ConsoleHumanAgentView"/> is the only view); Python's
/// <c>no_events()</c> suppression of sandbox events has no counterpart here.
/// </summary>
public static class HumanCli
{
    /// <summary>The registry name of the agent.</summary>
    public const string AgentName = "human_cli";

    /// <summary>The model name the answer is recorded under (<c>ModelOutput.from_content("human_agent", ...)</c>).</summary>
    public const string ModelName = "human_agent";

    /// <summary>
    /// Port of <c>human_cli(answer, intermediate_scoring, record_session, user, instructions, bashrc)</c>.
    /// </summary>
    /// <param name="answer">Is an explicit answer required for this task, or is it scored based on files in the container?</param>
    /// <param name="answerPattern">A regex the answer must match from its start (implies <paramref name="answer"/>).</param>
    /// <param name="intermediateScoring">Allow the human agent to check their score while working (<c>task score</c>).</param>
    /// <param name="recordSession">Record all user commands and outputs in the sandbox bash session.</param>
    /// <param name="user">User to login as. Defaults to the sandbox environment's default user.</param>
    /// <param name="instructions">Additional instructions beyond the default task command instructions.</param>
    /// <param name="bashrc">Additional content to include in the .bashrc file for the human cli shell.</param>
    /// <param name="view">Where to report progress (defaults to a <see cref="ConsoleHumanAgentView"/> on the console).</param>
    /// <param name="pollingInterval">How often to poll the sandbox for commands (defaults to the sandbox's service interval).</param>
    public static AgentDef Agent(
        bool answer = true,
        string? answerPattern = null,
        bool intermediateScoring = false,
        bool recordSession = true,
        string? user = null,
        string? instructions = null,
        string? bashrc = null,
        IHumanAgentView? view = null,
        TimeSpan? pollingInterval = null)
    {
        // we can only run one human agent interaction at a time (use lock to enforce)
        var agentLock = new SemaphoreSlim(1, 1);

        async Task<AgentState> Execute(AgentState state, CancellationToken cancellationToken)
        {
            await agentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // ensure that we have a sandbox to work with
                ISandboxEnvironment sandbox;
                try
                {
                    sandbox = SampleContext.Require().Sandbox();
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException("Human agent must run in a task with a sandbox.", ex);
                }

                SandboxConnection connection;
                try
                {
                    connection = await sandbox.ConnectionAsync(user, cancellationToken).ConfigureAwait(false);
                }
                catch (NotSupportedException ex)
                {
                    throw new InvalidOperationException("Human agent must run with a sandbox that supports connections.", ex);
                }

                // create agent commands
                var commands = HumanAgentCommands.Create(state, sandbox, answer, answerPattern, intermediateScoring, recordSession, instructions);

                // install agent tools
                await HumanAgentInstall.InstallAsync(sandbox, user, commands, bashrc, recordSession, cancellationToken).ConfigureAwait(false);

                // hookup the view ui
                var activeView = view ?? new ConsoleHumanAgentView();
                activeView.Connect(connection);

                // run sandbox service
                return await HumanAgentService.RunAsync(sandbox, user, state, commands, activeView, pollingInterval, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                agentLock.Release();
            }
        }

        return new AgentDef(AgentName, "Human CLI agent for tasks that run in a sandbox.", Execute);
    }
}
