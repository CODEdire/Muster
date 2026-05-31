// Domain entities live in feature sub-namespaces; import them all here so existing references resolve.
global using Muster.Domain.Entities.Guilds;
global using Muster.Domain.Entities.Members;
global using Muster.Domain.Entities.Operations;
global using Muster.Domain.Entities.Quests;
global using Muster.Domain.Entities.Events;
global using Muster.Domain.Entities.Tracking;
global using Muster.Domain.Entities.Musters;
global using Muster.Domain.Entities.Currencies;

// Bot is organized feature/function (Muster.Bot.<Feature>.<Function>); mirror the project's own
// per-feature global usings so tests resolve handlers, modules, schedulers, renderers without
// per-file using churn.
global using Muster.Bot.Economy;
global using Muster.Bot.Economy.Autocomplete;
global using Muster.Bot.Economy.BackgroundServices;
global using Muster.Bot.Economy.Handlers;
global using Muster.Bot.Economy.Modules;
global using Muster.Bot.Questing;
global using Muster.Bot.Questing.Autocomplete;
global using Muster.Bot.Questing.BackgroundServices;
global using Muster.Bot.Questing.Handlers;
global using Muster.Bot.Questing.Modules;
global using Muster.Bot.Questing.Rendering;
global using Muster.Bot.Tracking;
global using Muster.Bot.Tracking.Autocomplete;
global using Muster.Bot.Tracking.BackgroundServices;
global using Muster.Bot.Tracking.Handlers;
global using Muster.Bot.Tracking.Modules;
global using Muster.Bot.Tracking.Services;
global using Muster.Bot.Musters;
global using Muster.Bot.Membership;
global using Muster.Bot.Membership.Handlers;
global using Muster.Bot.Membership.Modules;
global using Muster.Bot.Platform;
global using Muster.Bot.Platform.Autocomplete;
global using Muster.Bot.Platform.BackgroundServices;
global using Muster.Bot.Config;
global using Muster.Bot.Seasons;
global using Muster.Bot.Ops;
