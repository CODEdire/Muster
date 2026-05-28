using Muster.Domain;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.IntegrationTests.TestSupport;

/// <summary>Stub <see cref="ITrackingAuthorizer"/> that permits every action — for unit-style tests where the
/// role check isn't the system under test. Use <see cref="AlwaysDenyTrackingAuthorizer"/> to assert the gate
/// fires.</summary>
internal sealed class AlwaysAllowTrackingAuthorizer : ITrackingAuthorizer
{
    public Task<bool> AuthorizeAsync(GuildActor actor, ulong subjectUserId, TrackingPermission action, CancellationToken ct = default) =>
        Task.FromResult(true);

    public bool Allows(GuildActor actor, bool isStaff, ulong subjectUserId, TrackingPermission action) => true;
}

/// <summary>Stub that denies everything — for tests asserting the authorizer-gate path returns Forbidden.</summary>
internal sealed class AlwaysDenyTrackingAuthorizer : ITrackingAuthorizer
{
    public Task<bool> AuthorizeAsync(GuildActor actor, ulong subjectUserId, TrackingPermission action, CancellationToken ct = default) =>
        Task.FromResult(false);

    public bool Allows(GuildActor actor, bool isStaff, ulong subjectUserId, TrackingPermission action) => false;
}
