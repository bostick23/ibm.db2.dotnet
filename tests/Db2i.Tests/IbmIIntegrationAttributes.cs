namespace Db2i.Tests;

// xUnit 2.x supports discovery-time skips. Keep opt-in checks in attributes so a
// missing configuration is visible as skipped instead of a successful test.
public sealed class IbmIIntegrationFactAttribute : FactAttribute
{
    public IbmIIntegrationFactAttribute(
        string connectionVariable = "DB2I_TEST_TCP_CONNECTION_STRING",
        string? enabledVariable = null)
    {
        Skip = IbmIIntegrationConfiguration.GetSkipReason(connectionVariable, enabledVariable);
    }
}

public sealed class IbmIIntegrationTheoryAttribute : TheoryAttribute
{
    public IbmIIntegrationTheoryAttribute(
        string connectionVariable = "DB2I_TEST_TCP_CONNECTION_STRING",
        string? enabledVariable = null)
    {
        Skip = IbmIIntegrationConfiguration.GetSkipReason(connectionVariable, enabledVariable);
    }
}

internal static class IbmIIntegrationConfiguration
{
    internal static string? GetSkipReason(string connectionVariable, string? enabledVariable)
    {
        if (enabledVariable is not null)
        {
            // Explicitly enabled tests must validate all required configuration
            // in their bodies and fail if it is incomplete.
            return string.Equals(Environment.GetEnvironmentVariable(enabledVariable)?.Trim(),
                "true", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Set {enabledVariable}=true to run this real IBM i test.";
        }

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(connectionVariable))
            ? $"Set {connectionVariable} to run this real IBM i test."
            : null;
    }
}
