// Self-export: every feature globally publishes its own namespace from its own folder so the rest of
// the project (Program.cs, sibling features) can resolve its types — extension methods, modules,
// renderers — without explicit using directives. Centralized once per feature, lives with the feature.
global using Muster.Bot.Musters;
global using Muster.Bot.Musters.Rendering;
global using Muster.Bot.Musters.Modules;
global using Muster.Bot.Musters.Handlers;
global using Muster.Bot.Musters.Autocomplete;
global using Muster.Bot.Musters.BackgroundServices;
