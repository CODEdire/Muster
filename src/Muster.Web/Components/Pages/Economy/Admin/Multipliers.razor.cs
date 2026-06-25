using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Membership;
using Muster.Domain.Entities.Tracking;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Tracking;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy.Admin;

public partial class Multipliers
{
    // TrackingManager + Admin (implicit) — multipliers govern tracking rewards.
    protected override GuildAccessTier RequiredAccess => GuildAccessTier.TrackingManager;

    private AddInput Add { get; set; } = new();

    private IReadOnlyList<RewardMultiplier> _multipliers = [];
    private IReadOnlyList<RoleOption> _roles = [];
    private string _zoneId = "UTC";

    private bool _saving;
    private Guid? _busyId;

    // Day pills wired to per-day bools on AddInput. Order matches Discord/ISO (Sun..Sat).
    private List<DayPill> DayPills = new();

    protected override async Task LoadAsync()
    {
        _multipliers = await Mults.ListAsync(GuildId);
        _roles = await AdminRead.GetRolesAsync(GuildId);
        _zoneId = await Guilds.GetGuildZoneIdAsync(GuildId) ?? "UTC";

        DayPills =
        [
            new("Sun", () => Add.Sun, v => Add.Sun = v),
            new("Mon", () => Add.Mon, v => Add.Mon = v),
            new("Tue", () => Add.Tue, v => Add.Tue = v),
            new("Wed", () => Add.Wed, v => Add.Wed = v),
            new("Thu", () => Add.Thu, v => Add.Thu = v),
            new("Fri", () => Add.Fri, v => Add.Fri = v),
            new("Sat", () => Add.Sat, v => Add.Sat = v),
        ];
    }

    private async Task ToggleAsync(Guid id, bool enabled)
    {
        _busyId = id;
        StateHasChanged();
        try
        {
            var r = await Mults.SetEnabledAsync(GuildId, UserId, id, enabled);
            if (!r.IsError) { await Audit.RecordMultiplierToggleAsync(GuildId, UserId, id, enabled); }
            Message = r.Message;
            await LoadAsync();
        }
        finally
        {
            _busyId = null;
            StateHasChanged();
        }
    }

    private async Task RemoveAsync(Guid id)
    {
        _busyId = id;
        StateHasChanged();
        try
        {
            var removed = _multipliers.FirstOrDefault(m => m.Id == id);
            var r = await Mults.RemoveAsync(GuildId, UserId, id);
            if (!r.IsError)
            {
                await Audit.RecordMultiplierRemovedAsync(
                    GuildId, UserId, id, removed?.Name ?? "",
                    removed?.Factor ?? 0, removed?.Scope ?? MultiplierScope.None);
            }
            Message = r.Message;
            await LoadAsync();
        }
        finally
        {
            _busyId = null;
            StateHasChanged();
        }
    }

    private async Task AddAsync()
    {
        var scope = (Add.ScopeVoice ? MultiplierScope.BackgroundVoice : 0)
            | (Add.ScopeMessages ? MultiplierScope.Messages : 0)
            | (Add.ScopeSessions ? MultiplierScope.Sessions : 0)
            | (Add.ScopeQuests ? MultiplierScope.Quests : 0);

        _saving = true;
        StateHasChanged();

        try
        {
            CommandResult<Guid> result;
            switch (Add.Kind)
            {
                case "recurring":
                    if (Add.StartTime is not { } st || Add.EndTime is not { } et)
                    {
                        Message = "Enter valid start/end times.";
                        return;
                    }
                    var days = (Add.Sun ? WeekDays.Sunday : 0) | (Add.Mon ? WeekDays.Monday : 0) | (Add.Tue ? WeekDays.Tuesday : 0)
                        | (Add.Wed ? WeekDays.Wednesday : 0) | (Add.Thu ? WeekDays.Thursday : 0) | (Add.Fri ? WeekDays.Friday : 0) | (Add.Sat ? WeekDays.Saturday : 0);
                    if (days == 0)
                    {
                        Message = "Pick at least one day.";
                        return;
                    }
                    result = await Mults.AddRecurringAsync(GuildId, UserId, Add.Name, Add.Factor, scope, days, st, et, minPeople: Add.MinPeople, minMinutes: Add.MinMinutes);
                    break;

                case "role":
                    if (Add.RoleId == 0)
                    {
                        Message = "Pick a role.";
                        return;
                    }
                    result = await Mults.AddRoleAsync(GuildId, UserId, Add.Name, Add.Factor, scope, Add.RoleId);
                    break;

                default: // one-off
                    if (Add.StartLocal is not { } sLocal || Add.EndLocal is not { } eLocal)
                    {
                        Message = "Enter valid start/end date-times.";
                        return;
                    }
                    result = await Mults.AddOneOffAsync(GuildId, UserId, Add.Name, Add.Factor, scope, ToUtc(sLocal), ToUtc(eLocal), minPeople: Add.MinPeople, minMinutes: Add.MinMinutes);
                    break;
            }

            if (!result.IsError)
            {
                await Audit.RecordMultiplierAddedAsync(GuildId, UserId, result.Value, Add.Name ?? "", Add.Factor, scope);
                ResetForm();
            }
            Message = result.Message;
            await LoadAsync();
        }
        finally
        {
            _saving = false;
            StateHasChanged();
        }
    }

    private void ResetForm()
    {
        Add = new AddInput();
        // Rebuild day pills bound to the new Add instance.
        DayPills =
        [
            new("Sun", () => Add.Sun, v => Add.Sun = v),
            new("Mon", () => Add.Mon, v => Add.Mon = v),
            new("Tue", () => Add.Tue, v => Add.Tue = v),
            new("Wed", () => Add.Wed, v => Add.Wed = v),
            new("Thu", () => Add.Thu, v => Add.Thu = v),
            new("Fri", () => Add.Fri, v => Add.Fri = v),
            new("Sat", () => Add.Sat, v => Add.Sat = v),
        ];
    }

    // Plain-English summary of the current Add form — live-updates as the user types so they see the effective
    // rule before clicking save.
    private string PreviewWhen()
    {
        var scope = ScopeWordsForPreview();
        var sb = new System.Text.StringBuilder();
        sb.Append($"×{Add.Factor:0.##} on {scope}");

        switch (Add.Kind)
        {
            case "oneoff":
                if (Add.StartLocal is { } s && Add.EndLocal is { } e)
                {
                    sb.Append($" from {s:yyyy-MM-dd HH:mm} to {e:yyyy-MM-dd HH:mm} ({_zoneId})");
                }
                else
                {
                    sb.Append(" — pick start & end above");
                }
                break;

            case "recurring":
                var days = string.Join("/", DayPills.Where(d => d.GetVal()).Select(d => d.Label));
                if (string.IsNullOrEmpty(days)) { sb.Append(" — pick at least one day"); break; }
                if (Add.StartTime is not { } st || Add.EndTime is not { } et)
                {
                    sb.Append($" every {days} — pick times");
                }
                else
                {
                    sb.Append($" every {days} {st:HH\\:mm}–{et:HH\\:mm} ({_zoneId})");
                }
                break;

            case "role":
                if (Add.RoleId == 0) { sb.Append(" — pick a role"); break; }
                var name = _roles.FirstOrDefault(r => r.RoleId == Add.RoleId)?.Name ?? Add.RoleId.ToString();
                sb.Append($" while a member holds @{name}");
                break;
        }

        if (Add.Kind != "role" && (Add.MinPeople > 0 || Add.MinMinutes > 0))
        {
            var conds = new List<string>();
            if (Add.MinPeople > 0) conds.Add($"≥{Add.MinPeople} people in channel");
            if (Add.MinMinutes > 0) conds.Add($"channel active ≥{Add.MinMinutes}m");
            sb.Append($"; only when {string.Join(" + ", conds)}");
        }

        return sb.ToString();
    }

    private string ScopeWordsForPreview()
    {
        var parts = new List<string>();
        if (Add.ScopeVoice) parts.Add("voice");
        if (Add.ScopeMessages) parts.Add("messages");
        if (Add.ScopeSessions) parts.Add("sessions");
        if (Add.ScopeQuests) parts.Add("quests");
        return parts.Count == 0 ? "nothing — pick at least one scope above" : string.Join(" + ", parts);
    }

    private DateTimeOffset ToUtc(DateTime local)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(_zoneId); }
        catch { zone = TimeZoneInfo.Utc; }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
    }

    private string When(RewardMultiplier m) => m.Kind switch
    {
        MultiplierKind.OneOff => $"{m.StartsAt?.UtcDateTime:yyyy-MM-dd HH:mm}–{m.EndsAt?.UtcDateTime:HH:mm} UTC",
        MultiplierKind.Recurring => $"{m.Days} {m.StartTime}–{m.EndTime}",
        MultiplierKind.Role => $"<@&{m.RoleId}>",
        _ => "—",
    };

    private static MarkupString ScopePills(MultiplierScope s)
    {
        if (s == MultiplierScope.All) { return new MarkupString("<span class='pill'>all</span>"); }
        var parts = new List<string>();
        if (s.HasFlag(MultiplierScope.BackgroundVoice)) parts.Add("voice");
        if (s.HasFlag(MultiplierScope.Messages)) parts.Add("messages");
        if (s.HasFlag(MultiplierScope.Sessions)) parts.Add("sessions");
        if (s.HasFlag(MultiplierScope.Quests)) parts.Add("quests");
        return new MarkupString(string.Join("", parts.Select(p => $"<span class='pill'>{p}</span>")));
    }

    private static string Conditions(RewardMultiplier m)
    {
        var parts = new List<string>();
        if (m.MinPeopleInChannel > 0) parts.Add($"≥{m.MinPeopleInChannel} ppl");
        if (m.MinMinutes > 0) parts.Add($"≥{m.MinMinutes}m active");
        return parts.Count > 0 ? string.Join(", ", parts) : "—";
    }

    public class AddInput
    {
        public string? Name { get; set; }
        public decimal Factor { get; set; } = 2m;
        public string Kind { get; set; } = "oneoff";
        public bool ScopeVoice { get; set; } = true;
        public bool ScopeMessages { get; set; } = true;
        public bool ScopeSessions { get; set; } = true;
        public bool ScopeQuests { get; set; }
        public int MinPeople { get; set; }
        public int MinMinutes { get; set; }
        // Typed bindings — Blazor's @bind on <input type="datetime-local"> wants DateTime?, time wants TimeOnly?.
        // String round-trips would need EditForm's InputText; we're using plain <input> for the interactive UX.
        public DateTime? StartLocal { get; set; }
        public DateTime? EndLocal { get; set; }
        public bool Sun { get; set; }
        public bool Mon { get; set; }
        public bool Tue { get; set; }
        public bool Wed { get; set; }
        public bool Thu { get; set; }
        public bool Fri { get; set; }
        public bool Sat { get; set; }
        public TimeOnly? StartTime { get; set; }
        public TimeOnly? EndTime { get; set; }
        public ulong RoleId { get; set; }
    }

    public class DayPill(string label, Func<bool> get, Action<bool> set)
    {
        public string Label { get; } = label;
        public Func<bool> GetVal { get; } = get;
        public Action<bool> SetVal { get; } = set;
        public void Toggle() => SetVal(!GetVal());
    }
}
