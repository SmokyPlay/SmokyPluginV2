namespace SmokyPluginV2.Handlers
{
    using System;
    using System.Collections.Generic;

    using MEC;

    using Exiled.API.Enums;
    using Exiled.Events.EventArgs.Map;
    using Exiled.Events.EventArgs.Player;
    using Exiled.Events.EventArgs.Scp079;
    using Exiled.Events.EventArgs.Scp106;
    using Exiled.Events.EventArgs.Scp914;
    using Exiled.Events.EventArgs.Server;
    using Exiled.Events.EventArgs.Warhead;

    using SmokyPluginV2.Discord;

    using GamePlayer = Exiled.API.Features.Player;
    using GameLog = Exiled.API.Features.Log;
    using GameServer = Exiled.API.Features.Server;
    using MapEvents = Exiled.Events.Handlers.Map;
    using PlayerEvents = Exiled.Events.Handlers.Player;
    using Scp079Events = Exiled.Events.Handlers.Scp079;
    using Scp106Events = Exiled.Events.Handlers.Scp106;
    using Scp914Events = Exiled.Events.Handlers.Scp914;
    using ServerEvents = Exiled.Events.Handlers.Server;
    using WarheadEvents = Exiled.Events.Handlers.Warhead;
    using WarheadFeature = Exiled.API.Features.Warhead;

    internal sealed class DiscordGameEventHandler
    {
        private static bool roundStartLogged;
        private readonly HashSet<int> leavingPlayerIds = new HashSet<int>();

        private DiscordLogService Logs => DiscordLogService.Current;

        private DiscordEventLogs Config => DiscordLogService.EventSettings;

        public void Register()
        {
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.RoundStarted += OnRoundStarted;
            ServerEvents.RoundEnded += OnRoundEnded;
            ServerEvents.ReportingCheater += OnReportingCheater;
            ServerEvents.LocalReporting += OnLocalReporting;

            PlayerEvents.Verified += OnVerified;
            PlayerEvents.Left += OnLeft;
            PlayerEvents.Hurting += OnHurting;
            PlayerEvents.Dying += OnDying;
            PlayerEvents.ChangingRole += OnChangingRole;
            PlayerEvents.ChangingGroup += OnChangingGroup;
            PlayerEvents.ChangingItem += OnChangingItem;
            PlayerEvents.UsedItem += OnUsedItem;
            PlayerEvents.ThrownProjectile += OnThrownProjectile;
            PlayerEvents.PickingUpItem += OnPickingUpItem;
            PlayerEvents.DroppedItem += OnDroppedItem;
            PlayerEvents.ReloadedWeapon += OnReloadedWeapon;
            PlayerEvents.EnteringPocketDimension += OnEnteringPocketDimension;
            PlayerEvents.EscapingPocketDimension += OnEscapingPocketDimension;
            PlayerEvents.IntercomSpeaking += OnIntercomSpeaking;
            PlayerEvents.Handcuffing += OnHandcuffing;
            PlayerEvents.RemovedHandcuffs += OnRemovedHandcuffs;
            PlayerEvents.InteractingDoor += OnInteractingDoor;
            PlayerEvents.InteractingElevator += OnInteractingElevator;
            PlayerEvents.InteractingLocker += OnInteractingLocker;
            PlayerEvents.TriggeringTesla += OnTriggeringTesla;
            PlayerEvents.UnlockingGenerator += OnUnlockingGenerator;
            PlayerEvents.OpeningGenerator += OnOpeningGenerator;
            PlayerEvents.ClosingGenerator += OnClosingGenerator;
            PlayerEvents.ActivatingGenerator += OnActivatingGenerator;
            PlayerEvents.StoppingGenerator += OnStoppingGenerator;

            MapEvents.GeneratorActivating += OnGeneratorActivating;
            MapEvents.Decontaminating += OnDecontaminating;
            WarheadEvents.Starting += OnWarheadStarting;
            WarheadEvents.Stopping += OnWarheadStopping;
            WarheadEvents.Detonating += OnWarheadDetonating;

            Scp914Events.Activating += OnScp914Activating;
            Scp914Events.ChangingKnobSetting += OnScp914ChangingKnob;
            Scp914Events.UpgradingPickup += OnScp914UpgradingPickup;
            Scp914Events.UpgradingInventoryItem += OnScp914UpgradingInventoryItem;

            Scp079Events.GainingExperience += OnScp079GainingExperience;
            Scp079Events.GainingLevel += OnScp079GainingLevel;
            Scp079Events.InteractingTesla += OnScp079InteractingTesla;

            Scp106Events.Teleporting += OnScp106Teleporting;
            Scp106Events.Stalking += OnScp106Stalking;
            Scp106Events.ExitStalking += OnScp106ExitStalking;
        }

        public void Unregister()
        {
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.RoundEnded -= OnRoundEnded;
            ServerEvents.ReportingCheater -= OnReportingCheater;
            ServerEvents.LocalReporting -= OnLocalReporting;

            PlayerEvents.Verified -= OnVerified;
            PlayerEvents.Left -= OnLeft;
            PlayerEvents.Hurting -= OnHurting;
            PlayerEvents.Dying -= OnDying;
            PlayerEvents.ChangingRole -= OnChangingRole;
            PlayerEvents.ChangingGroup -= OnChangingGroup;
            PlayerEvents.ChangingItem -= OnChangingItem;
            PlayerEvents.UsedItem -= OnUsedItem;
            PlayerEvents.ThrownProjectile -= OnThrownProjectile;
            PlayerEvents.PickingUpItem -= OnPickingUpItem;
            PlayerEvents.DroppedItem -= OnDroppedItem;
            PlayerEvents.ReloadedWeapon -= OnReloadedWeapon;
            PlayerEvents.EnteringPocketDimension -= OnEnteringPocketDimension;
            PlayerEvents.EscapingPocketDimension -= OnEscapingPocketDimension;
            PlayerEvents.IntercomSpeaking -= OnIntercomSpeaking;
            PlayerEvents.Handcuffing -= OnHandcuffing;
            PlayerEvents.RemovedHandcuffs -= OnRemovedHandcuffs;
            PlayerEvents.InteractingDoor -= OnInteractingDoor;
            PlayerEvents.InteractingElevator -= OnInteractingElevator;
            PlayerEvents.InteractingLocker -= OnInteractingLocker;
            PlayerEvents.TriggeringTesla -= OnTriggeringTesla;
            PlayerEvents.UnlockingGenerator -= OnUnlockingGenerator;
            PlayerEvents.OpeningGenerator -= OnOpeningGenerator;
            PlayerEvents.ClosingGenerator -= OnClosingGenerator;
            PlayerEvents.ActivatingGenerator -= OnActivatingGenerator;
            PlayerEvents.StoppingGenerator -= OnStoppingGenerator;

            MapEvents.GeneratorActivating -= OnGeneratorActivating;
            MapEvents.Decontaminating -= OnDecontaminating;
            WarheadEvents.Starting -= OnWarheadStarting;
            WarheadEvents.Stopping -= OnWarheadStopping;
            WarheadEvents.Detonating -= OnWarheadDetonating;

            Scp914Events.Activating -= OnScp914Activating;
            Scp914Events.ChangingKnobSetting -= OnScp914ChangingKnob;
            Scp914Events.UpgradingPickup -= OnScp914UpgradingPickup;
            Scp914Events.UpgradingInventoryItem -= OnScp914UpgradingInventoryItem;

            Scp079Events.GainingExperience -= OnScp079GainingExperience;
            Scp079Events.GainingLevel -= OnScp079GainingLevel;
            Scp079Events.InteractingTesla -= OnScp079InteractingTesla;

            Scp106Events.Teleporting -= OnScp106Teleporting;
            Scp106Events.Stalking -= OnScp106Stalking;
            Scp106Events.ExitStalking -= OnScp106ExitStalking;
        }

        private void Game(bool enabled, string message)
        {
            if (enabled)
                Logs?.LogGameLine(message);
        }

        private static string P(GamePlayer player)
        {
            if (player is null)
                return "Dedicated Server (server) [None]";

            return $"{DiscordLogService.Escape(player.Nickname)} ({DiscordLogService.Escape(player.UserId)}) [{player.Role.Type}]";
        }

        private void OnWaitingForPlayers()
        {
            roundStartLogged = false;
            leavingPlayerIds.Clear();
            Game(Config.WaitingForPlayers, ":hourglass: Waiting for players...");
            Logs?.UpdatePresence(0);
        }

        internal static void LogRoundStartingEarly()
        {
            DiscordLogService logs = DiscordLogService.Current;
            DiscordEventLogs config = DiscordLogService.EventSettings;
            if (roundStartLogged || logs is null || config?.RoundStarted != true)
                return;

            roundStartLogged = true;
            GameLog.Info($"[Discord Events] Round started with {GameServer.PlayerCount} player(s).");
            logs.LogGameLine($":arrow_forward: Round starting: {GameServer.PlayerCount} players in round.");
        }

        private void OnRoundStarted() => LogRoundStartingEarly();

        private void OnRoundEnded(RoundEndedEventArgs ev) =>
            Game(Config.RoundEnded, $":stop_button: Round ended: {ev.LeadingTeam} - Players online {GameServer.PlayerCount}/{GameServer.MaxPlayerCount}.");

        private void OnReportingCheater(ReportingCheaterEventArgs ev) => LogReport(ev.Player, ev.Target, ev.Reason);

        private void OnLocalReporting(LocalReportingEventArgs ev) => LogReport(ev.Player, ev.Target, ev.Reason);

        private void LogReport(GamePlayer issuer, GamePlayer target, string reason) =>
            Game(Config.PlayerReported, $":incoming_envelope: **Cheater report filled: {P(issuer)} reported {P(target)} for {DiscordLogService.Escape(reason)}.**");

        private void OnVerified(VerifiedEventArgs ev)
        {
            leavingPlayerIds.Remove(ev.Player.Id);
            string address = Plugin.Instance.Config.Discord.LogIpAddresses ? ev.Player.IPAddress : "REDACTED";
            Game(Config.PlayerJoined, $":arrow_right: **{DiscordLogService.Escape(ev.Player.Nickname)} ({DiscordLogService.Escape(ev.Player.UserId)}) [{DiscordLogService.Escape(address)}] has joined the game.**");
            Logs?.UpdatePresence();
        }

        private void OnLeft(LeftEventArgs ev)
        {
            if (ev.Player is not null)
                leavingPlayerIds.Add(ev.Player.Id);

            Game(Config.PlayerLeft, $":arrow_left: **{P(ev.Player)} has left the server.**");
            Logs?.UpdatePresence(Math.Max(0, GameServer.PlayerCount - 1));
        }

        private void OnHurting(HurtingEventArgs ev) =>
            Game(Config.PlayerHurt, $":crossed_swords: **{P(ev.Attacker)} has damaged {P(ev.Player)} for {ev.DamageHandler.Damage:0.##} with {ev.DamageHandler.Type}.**");

        private void OnDying(DyingEventArgs ev)
        {
            if (!ev.IsAllowed || IsLeaving(ev.Player))
                return;

            Game(Config.PlayerDied, $":skull_crossbones: **{P(ev.Attacker)} killed {P(ev.Player)} with {ev.DamageHandler.Type}.**");
        }

        private void OnChangingRole(ChangingRoleEventArgs ev)
        {
            if (!ev.IsAllowed || IsLeaving(ev.Player) || ev.Reason == SpawnReason.Destroyed)
                return;

            Game(Config.PlayerChangedRole, $":mens: {P(ev.Player)} has been changed to a {ev.NewRole}.");
        }

        private bool IsLeaving(GamePlayer player) =>
            player is not null && leavingPlayerIds.Contains(player.Id);

        private void OnChangingGroup(ChangingGroupEventArgs ev)
        {
            string badge = ev.NewGroup?.BadgeText ?? "None";
            string color = ev.NewGroup?.BadgeColor ?? "None";
            Game(Config.PlayerChangedGroup, $"{P(ev.Player)} has been assigned to the **{DiscordLogService.Escape(badge)} ({DiscordLogService.Escape(color)})** group.");
        }

        private void OnChangingItem(ChangingItemEventArgs ev)
        {
            string oldItem = ev.Player.CurrentItem?.Type.ToString() ?? "None";
            string newItem = ev.Item?.Type.ToString() ?? "None";
            Game(Config.PlayerChangedItem, $"{P(ev.Player)} changed the item in their hand: {oldItem} :arrow_right: {newItem}.");
        }

        private void OnUsedItem(UsedItemEventArgs ev) =>
            Game(Config.PlayerUsedItem, $":medical_symbol: {P(ev.Player)} used {ev.Usable?.Type}.");

        private void OnThrownProjectile(ThrownProjectileEventArgs ev) =>
            Game(Config.PlayerThrewProjectile, $":boom: {P(ev.Player)} threw a {ev.Throwable?.Type}.");

        private void OnPickingUpItem(PickingUpItemEventArgs ev) =>
            Game(Config.PlayerPickedUpItem, $"{P(ev.Player)} has picked up **{ev.Pickup?.Type}**.");

        private void OnDroppedItem(DroppedItemEventArgs ev) =>
            Game(Config.PlayerDroppedItem, $"{P(ev.Player)} has dropped **{ev.Pickup?.Type}**.");

        private void OnReloadedWeapon(ReloadedWeaponEventArgs ev) =>
            Game(Config.PlayerReloadedWeapon, $":arrows_counterclockwise: {P(ev.Player)} has reloaded their {ev.Firearm?.Type} weapon.");

        private void OnEnteringPocketDimension(EnteringPocketDimensionEventArgs ev) =>
            Game(Config.PlayerEnteredPocketDimension, $":door: {P(ev.Player)} has entered the pocket dimension.");

        private void OnEscapingPocketDimension(EscapingPocketDimensionEventArgs ev) =>
            Game(Config.PlayerEscapedPocketDimension, $":high_brightness: {P(ev.Player)} has escaped the pocket dimension.");

        private void OnIntercomSpeaking(IntercomSpeakingEventArgs ev) =>
            Game(Config.PlayerUsedIntercom, $":loud_sound: {P(ev.Player)} has started using the intercom.");

        private void OnHandcuffing(HandcuffingEventArgs ev) =>
            Game(Config.PlayerHandcuffed, $":lock: {P(ev.Target)} has been handcuffed by {P(ev.Player)}.");

        private void OnRemovedHandcuffs(RemovedHandcuffsEventArgs ev) =>
            Game(Config.PlayerRemovedHandcuffs, $":unlock: {P(ev.Target)} has been freed by {P(ev.Player)}.");

        private void OnInteractingDoor(InteractingDoorEventArgs ev)
        {
            string action = ev.Door?.IsOpen == true ? "closed" : "opened";
            Game(Config.PlayerInteractedDoor, $":door: {P(ev.Player)} has {action} {ev.Door?.Type} door.");
        }

        private void OnInteractingElevator(InteractingElevatorEventArgs ev) =>
            Game(Config.PlayerInteractedElevator, $":elevator: {P(ev.Player)} has called an elevator.");

        private void OnInteractingLocker(InteractingLockerEventArgs ev) =>
            Game(Config.PlayerInteractedLocker, $"{P(ev.Player)} has opened a locker.");

        private void OnTriggeringTesla(TriggeringTeslaEventArgs ev) =>
            Game(Config.PlayerTriggeredTesla, $":zap: {P(ev.Player)} has triggered a tesla gate.");

        internal static void LogSuccessfulWarheadPanelAccess(GamePlayer player)
        {
            DiscordLogService logs = DiscordLogService.Current;
            DiscordEventLogs config = DiscordLogService.EventSettings;
            if (logs is null || config?.PlayerAccessedWarheadPanel != true)
                return;

            logs.LogGameLine($":key: {P(player)} has accessed the Alpha-warhead detonation button cover.");
        }

        private void OnUnlockingGenerator(UnlockingGeneratorEventArgs ev) =>
            Game(Config.GeneratorUnlocked, $":unlock: {P(ev.Player)} has unlocked a generator door.");

        private void OnOpeningGenerator(OpeningGeneratorEventArgs ev) =>
            Game(Config.GeneratorOpened, $"{P(ev.Player)} has opened a generator.");

        private void OnClosingGenerator(ClosingGeneratorEventArgs ev) =>
            Game(Config.GeneratorClosed, $"{P(ev.Player)} has closed a generator.");

        private void OnActivatingGenerator(ActivatingGeneratorEventArgs ev) =>
            Game(Config.GeneratorActivationStarted, $":calling: {P(ev.Player)} has started activating a generator.");

        private void OnStoppingGenerator(StoppingGeneratorEventArgs ev) =>
            Game(Config.GeneratorActivationStopped, $"{P(ev.Player)} has stopped activating a generator.");

        private void OnGeneratorActivating(GeneratorActivatingEventArgs ev) =>
            Game(Config.GeneratorActivated, $":white_check_mark: Generator {ev.Generator} has finished its activation.");

        private void OnDecontaminating(DecontaminatingEventArgs ev) =>
            Game(Config.DecontaminationStarted, ":biohazard: **Decontamination has begun.**");

        private void OnWarheadStarting(StartingEventArgs ev)
        {
            int remaining = Math.Max(0, (int)Math.Ceiling(WarheadFeature.DetonationTimer));
            string message = ev.Player is null
                ? $":radioactive: **Alpha-warhead countdown initiated, detonation in: {remaining}.**"
                : $":radioactive: **{P(ev.Player)} started the alpha-warhead countdown, detonation in: {remaining}.**";
            Game(Config.WarheadStarted, message);
        }

        private void OnWarheadStopping(StoppingEventArgs ev)
        {
            string message = ev.Player is null
                ? ":no_entry: **Warhead detonation sequence canceled.**"
                : $":no_entry: **{P(ev.Player)} canceled warhead detonation sequence.**";
            Game(Config.WarheadStopped, message);
        }

        private void OnWarheadDetonating(DetonatingEventArgs ev) =>
            Game(Config.WarheadDetonated, ":radioactive: **The Alpha-warhead has detonated.**");

        private void OnScp914Activating(ActivatingEventArgs ev) =>
            Game(Config.Scp914Activated, $":gear: {P(ev.Player)} has activated SCP-914 on setting {ev.KnobSetting}.");

        private void OnScp914ChangingKnob(ChangingKnobSettingEventArgs ev) =>
            Game(Config.Scp914KnobChanged, $":gear: {P(ev.Player)} has changed the SCP-914 knob to {ev.KnobSetting}.");

        private void OnScp914UpgradingPickup(UpgradingPickupEventArgs ev) =>
            Game(Config.Scp914UpgradedItem, $":gear: SCP-914 has processed: **{ev.Pickup?.Type}**");

        private void OnScp914UpgradingInventoryItem(UpgradingInventoryItemEventArgs ev) =>
            Game(Config.Scp914UpgradedItem, $":gear: SCP-914 has processed: **{ev.Item?.Type}** carried by {P(ev.Player)}.");

        private void OnScp079GainingExperience(GainingExperienceEventArgs ev) =>
            Game(Config.Scp079GainedExperience, $"{P(ev.Player)} has gained {ev.Amount} XP ({ev.GainType}).");

        private void OnScp079GainingLevel(GainingLevelEventArgs ev) =>
            Game(Config.Scp079GainedLevel, $"{P(ev.Player)} has gained a level: {Math.Max(0, ev.NewLevel - 1)} :arrow_right: {ev.NewLevel}.");

        private void OnScp079InteractingTesla(InteractingTeslaEventArgs ev) =>
            Game(Config.Scp079UsedTesla, $":zap: {P(ev.Player)} has activated a tesla gate as SCP-079.");

        private void OnScp106Teleporting(TeleportingEventArgs ev) =>
            Game(Config.Scp106Teleported, $":cyclone: {P(ev.Player)} has teleported using Hunter's Atlas.");

        private void OnScp106Stalking(StalkingEventArgs ev)
        {
            if (!ev.IsAllowed || ev.Scp106 is null)
                return;

            GamePlayer player = ev.Player;
            Exiled.API.Features.Roles.Scp106Role role = ev.Scp106;
            Timing.CallDelayed(0.1f, () =>
            {
                if (role.IsStalking)
                    Game(Config.Scp106EnteredStalking, $":footprints: {P(player)} has entered Stalk mode.");
            });
        }

        private void OnScp106ExitStalking(ExitStalkingEventArgs ev)
        {
            if (!ev.IsAllowed || ev.Scp106 is null)
                return;

            GamePlayer player = ev.Player;
            Exiled.API.Features.Roles.Scp106Role role = ev.Scp106;
            Timing.CallDelayed(0.1f, () =>
            {
                if (!role.IsStalking)
                    Game(Config.Scp106ExitedStalking, $":footprints: {P(player)} has left Stalk mode.");
            });
        }
    }
}
