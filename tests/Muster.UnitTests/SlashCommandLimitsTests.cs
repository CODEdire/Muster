using System.Reflection;
using System.Text.RegularExpressions;
using NetCord.Services.ApplicationCommands;
using Xunit;

namespace Muster.UnitTests;

/// <summary>
/// Guards Discord's slash-command registration limits and the <c>/quest</c> sub-command tree. Exceeding the limits
/// (or misbuilding the group hierarchy) makes the bot's command-registration hosted service throw at startup
/// (400 Invalid Form Body), which fails the whole host — so these are asserted at build/test time, not discovered
/// in production.
/// </summary>
public class SlashCommandLimitsTests
{
    // Discord: command/option names 1-32 chars, lowercase regex; descriptions 1-100 chars; ≤25 options each.
    private static readonly Regex NameRegex = new("^[-_a-z0-9]{1,32}$", RegexOptions.Compiled);

    private static Assembly BotAssembly => typeof(Muster.Bot.Modules.QuestModule).Assembly;

    /// <summary>Reads Name/Description off either a <see cref="SlashCommandAttribute"/> or a
    /// <see cref="SubSlashCommandAttribute"/> (groups + sub-commands), since the quest tree uses both.</summary>
    private static (string? Name, string? Description)? CommandNaming(MemberInfo member)
    {
        Attribute? attr = member.GetCustomAttribute<SlashCommandAttribute>();
        attr ??= member.GetCustomAttribute<SubSlashCommandAttribute>();
        if (attr is null)
        {
            return null;
        }

        var t = attr.GetType();
        return ((string?)t.GetProperty("Name")?.GetValue(attr), (string?)t.GetProperty("Description")?.GetValue(attr));
    }

    [Fact]
    public void AllSlashCommands_RespectDiscordLimits()
    {
        var failures = new List<string>();
        var commandCount = 0;

        foreach (var type in BotAssembly.GetTypes())
        {
            // Class-level attribute → a command group (e.g. /quest, /quest review).
            if (CommandNaming(type) is { } group)
            {
                Validate(group.Name, group.Description, type.Name, failures);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (CommandNaming(method) is not { } cmd)
                {
                    continue;
                }

                commandCount++;
                var where = $"{type.Name}.{method.Name}";
                Validate(cmd.Name, cmd.Description, where, failures);

                var options = method.GetParameters()
                    .Select(p => p.GetCustomAttribute<SlashCommandParameterAttribute>())
                    .Where(a => a is not null)
                    .ToList();

                if (options.Count > 25)
                {
                    failures.Add($"{where}: {options.Count} options (max 25).");
                }

                foreach (var option in options)
                {
                    var optName = (string?)option!.GetType().GetProperty("Name")?.GetValue(option);
                    var optDesc = (string?)option.GetType().GetProperty("Description")?.GetValue(option);

                    if (optName is not null && !NameRegex.IsMatch(optName))
                    {
                        failures.Add($"{where}: option name '{optName}' must be 1-32 chars, lowercase [-_a-z0-9].");
                    }

                    if (optDesc is not null && optDesc.Length is < 1 or > 100)
                    {
                        failures.Add($"{where}: option '{optName}' description must be 1-100 chars (was {optDesc.Length}).");
                    }
                }
            }
        }

        Assert.True(commandCount > 0, "No [SlashCommand]/[SubSlashCommand] methods were discovered — the reflection guard is not actually checking anything.");
        Assert.True(failures.Count == 0, "Discord slash-command limit violations:\n" + string.Join("\n", failures));

        static void Validate(string? name, string? description, string where, List<string> failures)
        {
            if (name is null || !NameRegex.IsMatch(name))
            {
                failures.Add($"{where}: command name '{name}' must be 1-32 chars, lowercase [-_a-z0-9].");
            }

            if (description is not { Length: >= 1 and <= 100 })
            {
                failures.Add($"{where}: command description must be 1-100 chars (was {description?.Length.ToString() ?? "null"}).");
            }
        }
    }

    /// <summary>
    /// Builds the NetCord command tree exactly as the bot does at startup (minus the Discord call). This catches a
    /// misbuilt group hierarchy — e.g. nested <c>[SubSlashCommand]</c> classes being double-registered as top-level
    /// commands, or the build throwing — before it can fail the host. Asserts the quest surface collapses into a
    /// single <c>/quest</c> command with its sub-groups absorbed, and the old flat <c>/quest-*</c> commands are gone.
    /// </summary>
    [Fact]
    public void QuestCommands_BuildAsSingleGroupTree()
    {
        // Two type args (the autocomplete context) match how the bot's host registers commands, since several
        // modules use autocomplete providers (quest/currency).
        var service = new ApplicationCommandService<ApplicationCommandContext, AutocompleteInteractionContext>(
            ApplicationCommandServiceConfiguration<ApplicationCommandContext>.Default);

        // Throws here if the /quest group + nested review/intake sub-groups are misdeclared.
        service.AddModules(BotAssembly);

        var names = service.GetCommands().Select(c => c.Name).ToList();

        Assert.Single(names, n => n == "quest");
        Assert.DoesNotContain("quest-post", names);  // old flat command retired
        Assert.DoesNotContain("review", names);       // nested sub-group, not a top-level command
        Assert.DoesNotContain("intake", names);
    }
}
