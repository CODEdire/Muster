using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot;

/// <summary>
/// Coalesces and serializes voice reconciles per guild. Discord delivers a burst of voice-state events when a
/// channel fills (a raid forming, an event starting); reconciling the whole guild on every event would be a
/// thundering herd. Instead we <see cref="Schedule"/> a debounced run (one reconcile shortly after a burst
/// settles) and hold a per-guild lock so the debounced run, the periodic sweep, and session-start scans never
/// overlap for the same guild — which would race the minute bookkeeping (the ledger is already idempotent, the
/// accrual counters are not). The 5-minute sweep is the backstop if continuous churn ever starves a debounce.
/// </summary>
public sealed class GuildReconcileCoordinator(
    IServiceScopeFactory scopeFactory, GatewayClient client, ILogger<GuildReconcileCoordinator> logger)
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _pending = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _locks = new();

    /// <summary>Queue a debounced reconcile for the guild (a burst of events collapses into one run).</summary>
    public void Schedule(ulong guildId)
    {
        var cts = new CancellationTokenSource();
        // Supersede any pending run for this guild; the cancelled run disposes its own CTS.
        _pending.AddOrUpdate(guildId, cts, (_, old) =>
        {
            old.Cancel();
            return cts;
        });

        _ = DebouncedRunAsync(guildId, cts);
    }

    private async Task DebouncedRunAsync(ulong guildId, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DebounceWindow, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer event
        }
        finally
        {
            cts.Dispose();
        }

        _pending.TryRemove(new KeyValuePair<ulong, CancellationTokenSource>(guildId, cts));
        await ReconcileNowAsync(guildId);
    }

    /// <summary>Reconcile immediately, serialized per guild (used by the sweep and by session-start scans).</summary>
    public async Task ReconcileNowAsync(ulong guildId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var roster = VoiceRoster.Snapshot(client, guildId);
            await scope.ServiceProvider.GetRequiredService<TrackingSessionService>().ReconcileSessionsAsync(guildId, roster, ct: ct);
            await scope.ServiceProvider.GetRequiredService<BackgroundTrackingService>().ReconcileGuildAsync(guildId, roster, ct: ct);
            await RefreshChannelNamesAsync(scope.ServiceProvider.GetRequiredService<MusterDbContext>(), guildId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Voice reconcile failed for guild {GuildId}.", guildId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Keep stored channel names current from the gateway cache (covers web-added channels + renames).</summary>
    private async Task RefreshChannelNamesAsync(MusterDbContext db, ulong guildId, CancellationToken ct)
    {
        if (!client.Cache.Guilds.TryGetValue(guildId, out var guild))
        {
            return;
        }

        var changed = false;

        foreach (var tc in await db.ListTrackedChannelsAsync(guildId, ct))
        {
            if (guild.Channels.TryGetValue(tc.ChannelId, out var ch) && ch.Name is { Length: > 0 } name && tc.ChannelName != name)
            {
                tc.ChannelName = name;
                changed = true;
            }
        }

        foreach (var session in await db.ListActiveSessionsAsync(guildId, ct))
        {
            if (guild.Channels.TryGetValue(session.VoiceChannelId, out var ch) && ch.Name is { Length: > 0 } name && session.VoiceChannelName != name)
            {
                session.VoiceChannelName = name;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
