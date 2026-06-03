namespace Muster.Infrastructure.Services.Tracking;

/// <summary>Platform-wide tracking-retention policy, bound from configuration (section <c>Tracking</c>).</summary>
public sealed class TrackingRetentionOptions
{
    /// <summary>Platform ceiling for a guild's raw activity-retention window, in days. 0 = no platform limit
    /// (a guild may keep raw activity forever). When &gt; 0 it caps every guild's configured value.</summary>
    public int MaxActivityRetentionDays { get; set; }
}
