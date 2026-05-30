using System.Reflection;
using Muster.Bot.Tracking.Services;
using Xunit;

namespace Muster.Bot.UnitTests.Tracking.Services;

/// <summary>
/// VoiceRoster.Snapshot reads NetCord's <c>GatewayClient.Cache.Guilds</c> directly, which is sealed +
/// gateway-populated and can't be constructed in a unit test. The real behavior (snapshot of cached voice
/// states, with bot/muted/deafened flags) is exercised by integration tests against a real gateway.
///
/// This file pins the API contract so a refactor that breaks the static signature or shape is caught: the
/// caller — the background scheduler — depends on the exact static-class + IReadOnlyDictionary shape.
/// </summary>
public class VoiceRosterTests
{
    [Fact]
    public void IsStaticClass()
    {
        var t = typeof(VoiceRoster);
        Assert.True(t.IsAbstract && t.IsSealed, "VoiceRoster must be a static class — schedulers call it without DI.");
    }

    [Fact]
    public void Snapshot_HasExpectedSignature()
    {
        var method = typeof(VoiceRoster).GetMethod(nameof(VoiceRoster.Snapshot),
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.True(typeof(System.Collections.IEnumerable).IsAssignableFrom(method!.ReturnType),
            "Snapshot must return an enumerable (IReadOnlyDictionary) so callers can iterate.");

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal("client", parameters[0].Name);
        Assert.Equal("guildId", parameters[1].Name);
        Assert.Equal(typeof(ulong), parameters[1].ParameterType);
    }
}
