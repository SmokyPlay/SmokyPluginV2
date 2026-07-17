namespace SmokyPluginV2.Handlers
{
    using System;
    using System.Collections.Generic;

    using Exiled.API.Features;
    using Exiled.Events.EventArgs.Player;

    using MEC;

    using PlayerRoles;

    using PlayerEvents = Exiled.Events.Handlers.Player;
    using ServerEvents = Exiled.Events.Handlers.Server;

    /// <summary>
    /// Spawns players who join an active round for the first time near its beginning.
    /// </summary>
    internal sealed class LateJoinSpawnHandler
    {
        private readonly HashSet<string> playersSeenThisRound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Random random = new Random();

        private int roundGeneration;
        private bool isRegistered;
        private bool invalidChancesWereLogged;
        private double lastMainWaveTime = double.NaN;
        private MainWaveTeam lastMainWaveTeam;

        /// <summary>
        /// Starts tracking round and verification events.
        /// </summary>
        public void Register()
        {
            if (isRegistered)
                return;

            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.RoundStarted += OnRoundStarted;
            ServerEvents.RestartingRound += OnRestartingRound;
            PlayerEvents.Verified += OnVerified;
            isRegistered = true;

            // This covers enabling or reloading the plugin in the middle of a round.
            if (IsActiveRound())
                BeginRoundTracking();
        }

        /// <summary>
        /// Stops tracking events and invalidates pending delayed spawns.
        /// </summary>
        public void Unregister()
        {
            if (!isRegistered)
                return;

            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.RoundStarted -= OnRoundStarted;
            ServerEvents.RestartingRound -= OnRestartingRound;
            PlayerEvents.Verified -= OnVerified;
            isRegistered = false;
            ResetTracking();
        }

        private static bool IsActiveRound() => Round.IsStarted && !Round.IsEnded;

        private void OnWaitingForPlayers() => ResetTracking();

        private void OnRestartingRound() => ResetTracking();

        private void OnRoundStarted() => BeginRoundTracking();

        private void BeginRoundTracking()
        {
            roundGeneration++;
            playersSeenThisRound.Clear();
            invalidChancesWereLogged = false;
            lastMainWaveTime = double.NaN;
            lastMainWaveTeam = MainWaveTeam.None;

            foreach (Player player in Player.List)
                RememberPlayer(player);
        }

        private void ResetTracking()
        {
            roundGeneration++;
            playersSeenThisRound.Clear();
            invalidChancesWereLogged = false;
            lastMainWaveTime = double.NaN;
            lastMainWaveTeam = MainWaveTeam.None;
        }

        /// <summary>
        /// Opens the squad late-join window after a main respawn wave.
        /// </summary>
        /// <param name="isChaos">Whether the spawned main wave belongs to Chaos Insurgency.</param>
        internal void OnMainWaveSpawned(bool isChaos)
        {
            if (!isRegistered || !IsActiveRound())
                return;

            lastMainWaveTime = Round.ElapsedTime.TotalSeconds;
            lastMainWaveTeam = isChaos ? MainWaveTeam.Chaos : MainWaveTeam.Ntf;
            Log.Debug($"[Late Join] Opened the {lastMainWaveTeam} main-wave join window at {lastMainWaveTime:0.0}s.");
        }

        private void OnVerified(VerifiedEventArgs ev)
        {
            if (!IsActiveRound() || ev.Player is null)
                return;

            string userId = ev.Player.UserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                Log.Warn($"[Late Join] {ev.Player.Nickname} has no UserId after verification and cannot receive a late-join role.");
                return;
            }

            // Add before checking time or chances: every player gets at most one attempt per round.
            if (!playersSeenThisRound.Add(userId))
            {
                Log.Debug($"[Late Join] {userId} has already participated in this round; no role was assigned.");
                return;
            }

            LateJoinSpawnSettings settings = Plugin.Instance?.Config?.LateJoinSpawn;
            if (settings is null || !settings.IsEnabled)
                return;

            double elapsedSeconds = Round.ElapsedTime.TotalSeconds;
            if (!TryChooseJoinRole(settings, elapsedSeconds, out RoleTypeId role))
            {
                Log.Debug($"[Late Join] {userId} joined at {elapsedSeconds:0.0}s outside an active spawn window.");
                return;
            }

            Player player = ev.Player;
            int scheduledRoundGeneration = roundGeneration;
            float delay = Math.Max(0, settings.SpawnDelaySeconds);

            Timing.CallDelayed(delay, () => SpawnIfStillEligible(player, userId, role, scheduledRoundGeneration));
        }

        private void SpawnIfStillEligible(Player player, string userId, RoleTypeId role, int scheduledRoundGeneration)
        {
            if (!isRegistered || scheduledRoundGeneration != roundGeneration || !IsActiveRound())
                return;

            if (player is null || !player.IsConnected || !string.Equals(player.UserId, userId, StringComparison.OrdinalIgnoreCase))
                return;

            // Do not overwrite a role assigned by the game, an event, or an administrator while waiting.
            if (player.Role.Type != RoleTypeId.Spectator)
            {
                Log.Debug($"[Late Join] {userId} is already {player.Role.Type}; the scheduled {role} spawn was skipped.");
                return;
            }

            player.Role.Set(role);
            Log.Info($"[Late Join] Spawned {player.Nickname} ({userId}) as {role}.");
        }

        private bool TryChooseRole(LateJoinSpawnSettings settings, out RoleTypeId role)
        {
            double classD = Math.Max(0, settings.ClassDChance);
            double facilityGuard = Math.Max(0, settings.FacilityGuardChance);
            double scientist = Math.Max(0, settings.ScientistChance);
            double total = classD + facilityGuard + scientist;

            if (total <= 0 || double.IsNaN(total) || double.IsInfinity(total))
            {
                if (!invalidChancesWereLogged)
                {
                    Log.Warn("[Late Join] All role chances are zero or invalid. Late-join spawning is disabled for this round.");
                    invalidChancesWereLogged = true;
                }

                role = RoleTypeId.Spectator;
                return false;
            }

            double roll = random.NextDouble() * total;
            if (roll < classD)
            {
                role = RoleTypeId.ClassD;
                return true;
            }

            if (roll < classD + facilityGuard)
            {
                role = RoleTypeId.FacilityGuard;
                return true;
            }

            role = RoleTypeId.Scientist;
            return true;
        }

        private bool TryChooseJoinRole(LateJoinSpawnSettings settings, double elapsedSeconds, out RoleTypeId role)
        {
            double mainWaveWindow = Math.Max(0, settings.MainWaveJoinTimeSeconds);
            double timeSinceMainWave = elapsedSeconds - lastMainWaveTime;
            if (settings.SpawnAfterMainWaves &&
                lastMainWaveTeam != MainWaveTeam.None &&
                !double.IsNaN(lastMainWaveTime) &&
                timeSinceMainWave >= 0 &&
                timeSinceMainWave <= mainWaveWindow)
            {
                role = lastMainWaveTeam == MainWaveTeam.Chaos
                    ? RoleTypeId.ChaosRifleman
                    : RoleTypeId.NtfPrivate;
                return true;
            }

            // Once a main wave has arrived, the opening-round role distribution is permanently closed.
            // This also covers artificially spawned early waves during testing or administration.
            if (lastMainWaveTeam != MainWaveTeam.None)
            {
                role = RoleTypeId.Spectator;
                return false;
            }

            double initialWindow = Math.Max(0, settings.MaxJoinTimeSeconds);
            if (elapsedSeconds <= initialWindow)
                return TryChooseRole(settings, out role);

            role = RoleTypeId.Spectator;
            return false;
        }

        private void RememberPlayer(Player player)
        {
            if (player is not null && !string.IsNullOrWhiteSpace(player.UserId))
                playersSeenThisRound.Add(player.UserId);
        }

        private enum MainWaveTeam
        {
            None,
            Ntf,
            Chaos,
        }
    }
}
