// Closed generic alias for the ephemeral message reply that command modules return.
global using Reply = NetCord.Rest.InteractionCallbackProperties<NetCord.Rest.InteractionMessageProperties>;

// Domain entities live in feature sub-namespaces (Muster.Domain.Entities.<Feature>); import them all here.
global using Muster.Domain.Entities.Guilds;
global using Muster.Domain.Entities.Members;
global using Muster.Domain.Entities.Operations;
global using Muster.Domain.Entities.Quests;
global using Muster.Domain.Entities.Events;
global using Muster.Domain.Entities.Tracking;
global using Muster.Domain.Entities.Musters;
global using Muster.Domain.Entities.Currencies;
