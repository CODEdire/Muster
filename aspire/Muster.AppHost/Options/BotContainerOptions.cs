namespace Muster.AppHost.Options;

/// <summary>Container App sizing for the bot. Bound from <c>BotContainerOptions</c> (appsettings / user-secrets /
/// App Configuration). Replica count is intentionally NOT configurable — the Discord gateway is a singleton, so
/// the bot is always pinned to min = max = 1 (a second replica's identify is rejected). Only CPU/memory/grace
/// are tunable.</summary>
public record BotContainerOptions
{
    public double Cpu { get; init; } = 0.25;

    public string Memory { get; init; } = "0.5Gi";

    public int TerminationGracePeriodSeconds { get; init; } = 60;
}
