namespace InspectAzureAI.Examples.Runner;

/// <summary>
/// Placeholder credentials for <c>--fake</c> runs. Some engine providers (the Tavily and Exa web search providers,
/// for one) validate their API-key variable before the first call and have no injectable key, so an offline run that
/// drives them through a fake <see cref="HttpMessageHandler"/> needs the variable set to something. This sets it for
/// the process only when it is absent, so a real key in the environment is never overwritten.
/// </summary>
public static class FakeSecrets
{
    /// <summary>Sets <paramref name="variable"/> to <paramref name="placeholder"/> for this process when it is unset or empty; returns whether it did.</summary>
    public static bool EnsurePlaceholder(string variable, string placeholder)
    {
        ArgumentException.ThrowIfNullOrEmpty(variable);
        ArgumentException.ThrowIfNullOrEmpty(placeholder);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        {
            return false;
        }

        Environment.SetEnvironmentVariable(variable, placeholder);
        return true;
    }
}
