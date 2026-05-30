// Self-export: every feature globally publishes its own namespace from its own folder so the rest of
// the project (Program.cs, sibling features) can resolve its types — extension methods, modules,
// renderers — without explicit using directives. Centralized once per feature, lives with the feature.
global using Muster.Bot.Questing;
global using Muster.Bot.Questing.Rendering;
global using Muster.Bot.Questing.Modules;
global using Muster.Bot.Questing.Handlers;
global using Muster.Bot.Questing.BackgroundServices;
global using Muster.Bot.Questing.Autocomplete;