using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Agents;

/// <summary>
/// Port of <c>agent/_types.py</c> <c>AgentAttempts</c>: how many submissions an agent gets, the message shown
/// after an incorrect one, and how a score maps to a number (1.0 = correct; null = the default value_to_float).
/// </summary>
public sealed record AgentAttempts(
    int Attempts = 1,
    string IncorrectMessage = "Your submission was incorrect. Please proceed and attempt to find the correct answer.",
    Func<ScoreValue, double>? ScoreValue = null);
