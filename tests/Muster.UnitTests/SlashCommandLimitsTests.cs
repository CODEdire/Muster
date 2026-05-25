using System.Reflection;
using System.Text.RegularExpressions;
using NetCord.Services.ApplicationCommands;
using Xunit;

namespace Muster.UnitTests;

/// <summary>
/// Guards Discord's slash-command registration limits. Exceeding them makes the bot's command-registration
/// hosted service throw at startup (400 Invalid Form Body), which fails the whole host — so these are
/// asserted at build/test time, not discovered in production.
/// </summary>
public class SlashCommandLimitsTests
{
    // Discord: command/option names 1-32 chars, lowercase regex; descriptions 1-100 chars; ≤25 options each.
    private static readonly Regex NameRegex = new("^[-_a-z0-9]{1,32}$", RegexOptions.Compiled);

    [Fact]
    public void AllSlashCommands_RespectDiscordLimits()
    {
        var assembly = typeof(Muster.Bot.Modules.QuestModule).Assembly;
        var failures = new List<string>();
        var commandCount = 0;

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var command = method.GetCustomAttribute<SlashCommandAttribute>();
                if (command is null)
                {
                    continue;
                }

                commandCount++;
                var where = $"{type.Name}.{method.Name}";
                var name = (string?)command.GetType().GetProperty("Name")?.GetValue(command);
                var description = (string?)command.GetType().GetProperty("Description")?.GetValue(command);

                if (name is null || !NameRegex.IsMatch(name))
                {
                    failures.Add($"{where}: command name '{name}' must be 1-32 chars, lowercase [-_a-z0-9].");
                }

                if (description is not { Length: >= 1 and <= 100 })
                {
                    failures.Add($"{where}: command description must be 1-100 chars (was {description?.Length.ToString() ?? "null"}).");
                }

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

        Assert.True(commandCount > 0, "No [SlashCommand] methods were discovered — the reflection guard is not actually checking anything.");
        Assert.True(failures.Count == 0, "Discord slash-command limit violations:\n" + string.Join("\n", failures));
    }
}
