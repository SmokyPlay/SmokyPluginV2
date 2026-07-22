namespace SmokyPluginV2.Statistics
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    using Exiled.API.Enums;
    using Exiled.API.Features;
    using Exiled.Events.EventArgs.Map;
    using Exiled.Events.EventArgs.Player;
    using Exiled.Events.EventArgs.Scp049;
    using Exiled.Events.EventArgs.Scp079;
    using Exiled.Events.EventArgs.Scp330;
    using Exiled.Events.EventArgs.Server;
    using Exiled.Events.EventArgs.Warhead;

    using MEC;

    using PlayerRoles;
    using Respawning.Waves;

    using SmokyPluginV2.Database;

    using PlayerHandlers = Exiled.Events.Handlers.Player;
    using ServerHandlers = Exiled.Events.Handlers.Server;
    using MapHandlers = Exiled.Events.Handlers.Map;
    using Scp049Handlers = Exiled.Events.Handlers.Scp049;
    using Scp079Handlers = Exiled.Events.Handlers.Scp079;
    using Scp330Handlers = Exiled.Events.Handlers.Scp330;
    using WarheadHandlers = Exiled.Events.Handlers.Warhead;

    internal sealed class StatisticsService : IDisposable
    {
        private readonly MariaDbService database;
        private readonly BlockingCollection<Action> writeQueue = new BlockingCollection<Action>(5000);
        private readonly Thread writerThread;
        private readonly Dictionary<string, RoundPlayerState> players = new Dictionary<string, RoundPlayerState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingEscape> pendingEscapes = new Dictionary<string, PendingEscape>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Generator, string> generatorActivators = new Dictionary<Generator, string>();
        private DateTime roundStartedUtc;
        private bool roundActive;
        private bool recordingPaused;
        private bool systemRebootCounted;
        private bool warheadDetonated;
        private bool lastWarheadWasAutomatic;
        private string lastWarheadStarterUserId;
        private string rebootStarterUserId;
        private string lastTesla079UserId;
        private DateTime lastTesla079AtUtc;

        public StatisticsService(MariaDbService database)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            writerThread = new Thread(ProcessWrites)
            {
                IsBackground = true,
                Name = "SmokyPluginV2 statistics writer",
            };
            writerThread.Start();
        }

        public bool IsRoundActive => roundActive;

        public bool IsRecording => roundActive && !recordingPaused;

        public bool IsPausedForCurrentRound => roundActive && recordingPaused;

        public void Register()
        {
            ServerHandlers.WaitingForPlayers += OnWaitingForPlayers;
            ServerHandlers.RoundStarted += OnRoundStarted;
            ServerHandlers.RoundEnded += OnRoundEnded;
            ServerHandlers.RestartingRound += OnRestartingRound;
            PlayerHandlers.Verified += OnVerified;
            PlayerHandlers.Left += OnLeft;
            PlayerHandlers.Died += OnDied;
            PlayerHandlers.ChangingRole += OnChangingRole;
            PlayerHandlers.Escaping += OnEscaping;
            PlayerHandlers.Escaped += OnEscaped;
            PlayerHandlers.EnteringPocketDimension += OnEnteringPocketDimension;
            PlayerHandlers.EscapingPocketDimension += OnEscapingPocketDimension;
            PlayerHandlers.ActivatingGenerator += OnActivatingGenerator;
            MapHandlers.GeneratorActivating += OnGeneratorActivated;
            Scp049Handlers.FinishingRecall += OnFinishingRecall;
            Scp079Handlers.InteractingTesla += OnInteractingTesla;
            Scp079Handlers.Recontained += OnScp079Recontained;
            Scp330Handlers.EatenScp330 += OnCandyEaten;
            WarheadHandlers.Starting += OnWarheadStarting;
            WarheadHandlers.Stopping += OnWarheadStopping;
            WarheadHandlers.Detonated += OnWarheadDetonated;
        }

        public void Dispose()
        {
            if (IsRecording)
                FlushAllIntervals(DateTime.UtcNow, false);
            ServerHandlers.WaitingForPlayers -= OnWaitingForPlayers;
            ServerHandlers.RoundStarted -= OnRoundStarted;
            ServerHandlers.RoundEnded -= OnRoundEnded;
            ServerHandlers.RestartingRound -= OnRestartingRound;
            PlayerHandlers.Verified -= OnVerified;
            PlayerHandlers.Left -= OnLeft;
            PlayerHandlers.Died -= OnDied;
            PlayerHandlers.ChangingRole -= OnChangingRole;
            PlayerHandlers.Escaping -= OnEscaping;
            PlayerHandlers.Escaped -= OnEscaped;
            PlayerHandlers.EnteringPocketDimension -= OnEnteringPocketDimension;
            PlayerHandlers.EscapingPocketDimension -= OnEscapingPocketDimension;
            PlayerHandlers.ActivatingGenerator -= OnActivatingGenerator;
            MapHandlers.GeneratorActivating -= OnGeneratorActivated;
            Scp049Handlers.FinishingRecall -= OnFinishingRecall;
            Scp079Handlers.InteractingTesla -= OnInteractingTesla;
            Scp079Handlers.Recontained -= OnScp079Recontained;
            Scp330Handlers.EatenScp330 -= OnCandyEaten;
            WarheadHandlers.Starting -= OnWarheadStarting;
            WarheadHandlers.Stopping -= OnWarheadStopping;
            WarheadHandlers.Detonated -= OnWarheadDetonated;
            DeactivateRound();
            writeQueue.CompleteAdding();
            bool writerStopped = writerThread.Join(TimeSpan.FromSeconds(10));
            if (!writerStopped)
                Log.Warn("[Statistics] MariaDB writer did not stop within 10 seconds; queued updates may finish during shutdown.");
            else
                writeQueue.Dispose();
        }

        public bool ToggleRecording(out bool isRecording, out string response)
        {
            if (!roundActive)
            {
                isRecording = false;
                response = "Сейчас нет активного раунда. В лобби и после завершения раунда статистика всегда отключена.";
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (!recordingPaused)
            {
                FlushAllIntervals(now, false);
                recordingPaused = true;
                isRecording = false;
                response = "Запись статистики остановлена до повторного выполнения команды или начала следующего раунда.";
            }
            else
            {
                recordingPaused = false;
                foreach (Player player in OnlinePlayers())
                    ResumeState(GetState(player), player, now);
                isRecording = true;
                response = "Запись статистики возобновлена. Пропущенный промежуток не будет учтён.";
            }
            return true;
        }

        public bool TryClearPlayerStatistics(string userId, Player onlinePlayer, out bool existed, out string error)
        {
            existed = false;
            error = null;
            if (string.IsNullOrWhiteSpace(userId))
            {
                error = "Не указан Steam ID игрока.";
                return false;
            }

            bool databaseSucceeded = false;
            bool databaseExisted = false;
            string databaseError = null;
            ManualResetEventSlim completed = new ManualResetEventSlim(false);
            if (writeQueue.IsAddingCompleted || !writeQueue.TryAdd(() =>
            {
                try
                {
                    databaseSucceeded = database.TryClearPlayerStatistics(userId, out databaseExisted, out databaseError);
                }
                finally
                {
                    completed.Set();
                }
            }))
            {
                completed.Dispose();
                error = "Очередь записи статистики недоступна.";
                return false;
            }

            if (!completed.Wait(TimeSpan.FromSeconds(15)))
            {
                error = "MariaDB не завершила очистку статистики за 15 секунд. Результат операции неизвестен; проверьте статистику перед повтором.";
                return false;
            }
            completed.Dispose();

            if (!databaseSucceeded)
            {
                error = databaseError;
                return false;
            }

            existed = databaseExisted;
            ResetRoundTracking(userId, onlinePlayer, DateTime.UtcNow);
            return true;
        }

        public void OnWaveSpawned(SpawnableWaveBase wave)
        {
            if (!IsRecording || wave == null)
                return;
            string type = wave.GetType().Name;
            bool chaos = type.IndexOf("Chaos", StringComparison.OrdinalIgnoreCase) >= 0;
            bool mtf = type.IndexOf("Ntf", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("Mtf", StringComparison.OrdinalIgnoreCase) >= 0;
            bool reinforcement = type.IndexOf("Mini", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("Reinforcement", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!chaos && !mtf)
                return;
            SafeServerUpdate(new ServerStatDelta().Increment(
                chaos
                    ? reinforcement ? "chaos_reinforcement_waves" : "chaos_main_waves"
                    : reinforcement ? "mtf_reinforcement_waves" : "mtf_main_waves"));
        }

        private void OnWaitingForPlayers() => DeactivateRound();

        private void OnRestartingRound() => DeactivateRound();

        private void OnRoundStarted()
        {
            players.Clear();
            pendingEscapes.Clear();
            generatorActivators.Clear();
            recordingPaused = false;
            systemRebootCounted = false;
            warheadDetonated = false;
            lastWarheadWasAutomatic = false;
            lastWarheadStarterUserId = null;
            rebootStarterUserId = null;
            lastTesla079UserId = null;
            roundStartedUtc = DateTime.UtcNow;
            roundActive = true;
            foreach (Player player in OnlinePlayers())
                ResumeState(GetState(player), player, roundStartedUtc);
            Log.Info("[Statistics] Recording started for the new round; any RA pause from the previous round was reset.");
        }

        private void OnRoundEnded(RoundEndedEventArgs ev)
        {
            bool shouldRecord = IsRecording;
            roundActive = false;
            if (!shouldRecord)
            {
                players.Clear();
                pendingEscapes.Clear();
                return;
            }

            DateTime now = DateTime.UtcNow;
            FlushAllIntervals(now, true);
            List<Player> connected = OnlinePlayers().ToList();
            foreach (Player player in connected)
            {
                RoundPlayerState state = GetState(player);
                PlayerStatDelta delta = new PlayerStatDelta()
                    .Increment("rounds_completed")
                    .Max("best_human_kills_round", state.HumanKillsThisRound)
                    .Max("best_scp_kills_round", state.ScpKillsThisRound);
                SafePlayerUpdate(player.UserId, player.Nickname, delta, now);
            }

            long duration = Math.Max(0L, (long)Math.Round((now - roundStartedUtc).TotalSeconds));
            ServerStatDelta server = new ServerStatDelta()
                .Increment("rounds_completed")
                .Increment("total_round_seconds", duration)
                .Max("longest_round_seconds", duration);
            switch (ev.LeadingTeam)
            {
                case LeadingTeam.FacilityForces: server.Increment("foundation_wins"); break;
                case LeadingTeam.ChaosInsurgency: server.Increment("chaos_wins"); break;
                case LeadingTeam.Anomalies: server.Increment("scp_wins"); break;
                default: server.Increment("draws"); break;
            }
            SafeServerUpdate(server);
            players.Clear();
            pendingEscapes.Clear();
        }

        private void OnVerified(VerifiedEventArgs ev)
        {
            if (!IsRealPlayer(ev.Player))
                return;
            SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname, new PlayerStatDelta(), DateTime.UtcNow);
            if (IsRecording)
                ResumeState(GetState(ev.Player), ev.Player, DateTime.UtcNow);
        }

        private void OnLeft(LeftEventArgs ev)
        {
            if (!IsRealPlayer(ev.Player))
                return;
            DateTime now = DateTime.UtcNow;
            if (IsRecording && players.TryGetValue(ev.Player.UserId, out RoundPlayerState state))
            {
                FlushStateIntervals(state, now, true);
                players.Remove(ev.Player.UserId);
            }
            SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname, new PlayerStatDelta(), now);
        }

        private void OnChangingRole(ChangingRoleEventArgs ev)
        {
            if (!IsRecording || !IsRealPlayer(ev.Player))
                return;
            DateTime now = DateTime.UtcNow;
            RoundPlayerState state = GetState(ev.Player);
            RoleCategory newCategory = Classify(ev.NewRole);
            bool categoryChanged = state.Category != newCategory;
            FlushRoleInterval(state, now);
            if (categoryChanged)
                FinalizeLife(state, now);
            state.Category = newCategory;
            state.RoleIntervalStartedUtc = IsTracked(newCategory) ? now : (DateTime?)null;
            if (categoryChanged)
                state.LifeIntervalStartedUtc = IsLiving(newCategory) ? now : (DateTime?)null;
        }

        private void OnDied(DiedEventArgs ev)
        {
            if (!IsRecording || !IsRealPlayer(ev.Player))
                return;
            DateTime now = DateTime.UtcNow;
            RoundPlayerState victimState = GetState(ev.Player);
            victimState.Category = Classify(ev.TargetOldRole);
            FlushRoleInterval(victimState, now);
            FinalizeLife(victimState, now);
            FinishPocket(victimState, now);
            victimState.Category = RoleCategory.Spectator;
            victimState.RoleIntervalStartedUtc = now;

            RoleCategory victim = Classify(ev.TargetOldRole);
            PlayerStatDelta victimDelta = new PlayerStatDelta();
            if (victim == RoleCategory.Human)
                victimDelta.Increment("human_deaths");
            else if (victim == RoleCategory.Scp)
                victimDelta.Increment("scp_deaths");
            if (!victimDelta.IsEmpty)
                SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname, victimDelta, now);

            Player attacker = ev.Attacker;
            if (IsRealPlayer(attacker) && attacker != ev.Player)
            {
                RoleCategory attackerCategory = Classify(attacker.Role.Type);
                PlayerStatDelta attackerDelta = new PlayerStatDelta();
                if (victim == RoleCategory.Human && attackerCategory == RoleCategory.Human)
                {
                    attackerDelta.Increment("human_kills_as_human");
                    GetState(attacker).HumanKillsThisRound++;
                }
                else if (victim == RoleCategory.Human && attackerCategory == RoleCategory.Scp)
                {
                    attackerDelta.Increment("human_kills_as_scp");
                    GetState(attacker).ScpKillsThisRound++;
                }
                else if (victim == RoleCategory.Scp && attackerCategory == RoleCategory.Human)
                {
                    attackerDelta.Increment("scps_destroyed");
                }
                if (!attackerDelta.IsEmpty)
                    SafePlayerUpdate(attacker.UserId, attacker.Nickname, attackerDelta, now);
            }

            if (victim == RoleCategory.Human &&
                ev.DamageHandler?.Type.ToString().IndexOf("Tesla", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !string.IsNullOrWhiteSpace(lastTesla079UserId) &&
                (now - lastTesla079AtUtc).TotalSeconds <= 8)
            {
                SafePlayerUpdate(lastTesla079UserId, null, new PlayerStatDelta().Increment("tesla_kills_as_079"), now);
            }
        }

        private void OnEscaping(EscapingEventArgs ev)
        {
            if (!IsRecording || !ev.IsAllowed || !IsRealPlayer(ev.Player))
                return;
            pendingEscapes[ev.Player.UserId] = new PendingEscape
            {
                Scenario = ev.EscapeScenario,
                CufferUserId = ev.Player.Cuffer?.UserId,
                CufferNickname = ev.Player.Cuffer?.Nickname,
            };
        }

        private void OnEscaped(EscapedEventArgs ev)
        {
            if (!IsRecording || !IsRealPlayer(ev.Player))
                return;
            DateTime now = DateTime.UtcNow;
            pendingEscapes.TryGetValue(ev.Player.UserId, out PendingEscape pending);
            pendingEscapes.Remove(ev.Player.UserId);
            EscapeScenario scenario = ev.EscapeScenario;
            long seconds = Math.Max(0, ev.EscapeTime);
            PlayerStatDelta delta = new PlayerStatDelta();
            switch (scenario)
            {
                case EscapeScenario.ClassD:
                    delta.Increment("classd_escapes_uncuffed").Min("fastest_classd_escape_uncuffed_seconds", seconds);
                    break;
                case EscapeScenario.CuffedClassD:
                    delta.Increment("classd_escapes_cuffed").Min("fastest_classd_escape_cuffed_seconds", seconds);
                    CreditEscort(pending, "classd_escorted", now);
                    break;
                case EscapeScenario.Scientist:
                    delta.Increment("scientist_escapes_uncuffed").Min("fastest_scientist_escape_uncuffed_seconds", seconds);
                    break;
                case EscapeScenario.CuffedScientist:
                    delta.Increment("scientist_escapes_cuffed").Min("fastest_scientist_escape_cuffed_seconds", seconds);
                    CreditEscort(pending, "scientist_escorted", now);
                    break;
            }
            if (!delta.IsEmpty)
                SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname, delta, now);
        }

        private void OnEnteringPocketDimension(EnteringPocketDimensionEventArgs ev)
        {
            if (!IsRecording || !ev.IsAllowed || !IsRealPlayer(ev.Player))
                return;
            RoundPlayerState state = GetState(ev.Player);
            if (!state.InPocket)
            {
                state.InPocket = true;
                state.PocketIntervalStartedUtc = DateTime.UtcNow;
            }
            SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname, new PlayerStatDelta().Increment("pocket_entries"), DateTime.UtcNow);
        }

        private void OnEscapingPocketDimension(EscapingPocketDimensionEventArgs ev)
        {
            if (!IsRecording || !ev.IsAllowed || !IsRealPlayer(ev.Player))
                return;
            DateTime now = DateTime.UtcNow;
            RoundPlayerState state = GetState(ev.Player);
            long stay = FinishPocket(state, now);
            SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname,
                new PlayerStatDelta().Increment("pocket_escapes").Max("longest_pocket_seconds", stay), now);
        }

        private void OnActivatingGenerator(ActivatingGeneratorEventArgs ev)
        {
            if (IsRecording && ev.IsAllowed && ev.Generator != null && IsRealPlayer(ev.Player))
                generatorActivators[ev.Generator] = ev.Player.UserId;
        }

        private void OnGeneratorActivated(GeneratorActivatingEventArgs ev)
        {
            if (!IsRecording || !ev.IsAllowed || ev.Generator == null)
                return;
            Player activator = ev.Generator.LastActivator;
            string userId = IsRealPlayer(activator) ? activator.UserId : generatorActivators.TryGetValue(ev.Generator, out string remembered) ? remembered : null;
            if (string.IsNullOrWhiteSpace(userId))
                return;
            SafePlayerUpdate(userId, activator?.Nickname, new PlayerStatDelta().Increment("generators_activated"), DateTime.UtcNow);
            Timing.CallDelayed(0f, () => CheckSystemRebootStarted(userId, activator?.Nickname));
        }

        private void CheckSystemRebootStarted(string userId, string nickname)
        {
            if (!IsRecording || systemRebootCounted || Generator.List.Count == 0 || Generator.List.Any(generator => !generator.IsEngaged))
                return;
            systemRebootCounted = true;
            rebootStarterUserId = userId;
            SafePlayerUpdate(userId, nickname, new PlayerStatDelta().Increment("system_reboots_started"), DateTime.UtcNow);
        }

        private void OnFinishingRecall(FinishingRecallEventArgs ev)
        {
            if (!IsRecording || !ev.IsAllowed || !IsRealPlayer(ev.Player))
                return;
            string userId = ev.Player.UserId;
            string nickname = ev.Player.Nickname;
            Player target = ev.Target;
            Timing.CallDelayed(0f, () =>
            {
                if (IsRecording && target != null && target.Role.Type == RoleTypeId.Scp0492)
                    SafePlayerUpdate(userId, nickname, new PlayerStatDelta().Increment("zombies_created"), DateTime.UtcNow);
            });
        }

        private void OnInteractingTesla(InteractingTeslaEventArgs ev)
        {
            if (!IsRecording || !ev.IsAllowed || !IsRealPlayer(ev.Player))
                return;
            lastTesla079UserId = ev.Player.UserId;
            lastTesla079AtUtc = DateTime.UtcNow;
        }

        private void OnScp079Recontained(RecontainedEventArgs ev)
        {
            if (!IsRecording)
                return;
            Player credited = IsRealPlayer(ev.Attacker) ? ev.Attacker : null;
            string userId = credited?.UserId ?? rebootStarterUserId;
            if (!string.IsNullOrWhiteSpace(userId))
                SafePlayerUpdate(userId, credited?.Nickname, new PlayerStatDelta().Increment("scps_destroyed"), DateTime.UtcNow);
        }

        private void OnCandyEaten(EatenScp330EventArgs ev)
        {
            if (!IsRecording || !IsRealPlayer(ev.Player) || ev.Candy == null || ev.Candy.GetType().Name.IndexOf("Pink", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname, new PlayerStatDelta().Increment("pink_candies_eaten"), DateTime.UtcNow);
        }

        private void OnWarheadStarting(StartingEventArgs ev)
        {
            if (!IsRecording || !ev.IsAllowed)
                return;
            lastWarheadWasAutomatic = ev.IsAuto || !IsRealPlayer(ev.Player);
            lastWarheadStarterUserId = lastWarheadWasAutomatic ? null : ev.Player.UserId;
            if (!lastWarheadWasAutomatic)
                SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname, new PlayerStatDelta().Increment("warhead_countdowns_started"), DateTime.UtcNow);
        }

        private void OnWarheadStopping(StoppingEventArgs ev)
        {
            if (!IsRecording || !ev.IsAllowed || !IsRealPlayer(ev.Player))
                return;
            SafePlayerUpdate(ev.Player.UserId, ev.Player.Nickname, new PlayerStatDelta().Increment("warhead_countdowns_stopped"), DateTime.UtcNow);
            lastWarheadStarterUserId = null;
        }

        private void OnWarheadDetonated()
        {
            if (!IsRecording || warheadDetonated)
                return;
            warheadDetonated = true;
            if (!string.IsNullOrWhiteSpace(lastWarheadStarterUserId))
                SafePlayerUpdate(lastWarheadStarterUserId, null, new PlayerStatDelta().Increment("warhead_detonations"), DateTime.UtcNow);
            bool wasPlayerStarted = !lastWarheadWasAutomatic && !string.IsNullOrWhiteSpace(lastWarheadStarterUserId);
            ServerStatDelta delta = new ServerStatDelta()
                .Increment("warhead_detonations")
                .Increment(wasPlayerStarted ? "player_warhead_detonations" : "automatic_warhead_detonations");
            SafeServerUpdate(delta);
        }

        private void CreditEscort(PendingEscape escape, string column, DateTime now)
        {
            if (escape != null && !string.IsNullOrWhiteSpace(escape.CufferUserId))
                SafePlayerUpdate(escape.CufferUserId, escape.CufferNickname, new PlayerStatDelta().Increment(column), now);
        }

        private void FlushAllIntervals(DateTime now, bool finalizeLives)
        {
            foreach (RoundPlayerState state in players.Values.ToList())
                FlushStateIntervals(state, now, finalizeLives);
        }

        private void FlushStateIntervals(RoundPlayerState state, DateTime now, bool finalizeLife)
        {
            FlushRoleInterval(state, now);
            if (finalizeLife)
                FinalizeLife(state, now);
            else if (state.LifeIntervalStartedUtc.HasValue)
                state.LifeSeconds += ElapsedSeconds(state.LifeIntervalStartedUtc.Value, now);
            if (state.InPocket)
            {
                long stay = AccumulatePocket(state, now);
                SafePlayerUpdate(state.UserId, state.Nickname, new PlayerStatDelta().Max("longest_pocket_seconds", stay), now);
            }
            state.RoleIntervalStartedUtc = null;
            state.LifeIntervalStartedUtc = null;
            state.PocketIntervalStartedUtc = null;
        }

        private void FlushRoleInterval(RoundPlayerState state, DateTime now)
        {
            if (!state.RoleIntervalStartedUtc.HasValue)
                return;
            long seconds = ElapsedSeconds(state.RoleIntervalStartedUtc.Value, now);
            string column = state.Category == RoleCategory.Human ? "human_seconds" : state.Category == RoleCategory.Scp ? "scp_seconds" : state.Category == RoleCategory.Spectator ? "spectator_seconds" : null;
            if (column != null && seconds > 0)
                SafePlayerUpdate(state.UserId, state.Nickname, new PlayerStatDelta().Increment(column, seconds), now);
            state.RoleIntervalStartedUtc = now;
        }

        private void FinalizeLife(RoundPlayerState state, DateTime now)
        {
            if (state.LifeIntervalStartedUtc.HasValue)
                state.LifeSeconds += ElapsedSeconds(state.LifeIntervalStartedUtc.Value, now);
            if (state.LifeSeconds > 0)
            {
                string column = state.Category == RoleCategory.Human ? "longest_human_life_seconds" : state.Category == RoleCategory.Scp ? "longest_scp_life_seconds" : null;
                if (column != null)
                    SafePlayerUpdate(state.UserId, state.Nickname, new PlayerStatDelta().Max(column, state.LifeSeconds), now);
            }
            state.LifeSeconds = 0;
            state.LifeIntervalStartedUtc = null;
        }

        private long AccumulatePocket(RoundPlayerState state, DateTime now)
        {
            if (state.PocketIntervalStartedUtc.HasValue)
                state.PocketSeconds += ElapsedSeconds(state.PocketIntervalStartedUtc.Value, now);
            state.PocketIntervalStartedUtc = now;
            return state.PocketSeconds;
        }

        private long FinishPocket(RoundPlayerState state, DateTime now)
        {
            if (!state.InPocket)
                return 0;
            long seconds = AccumulatePocket(state, now);
            state.InPocket = false;
            state.PocketSeconds = 0;
            state.PocketIntervalStartedUtc = null;
            return seconds;
        }

        private void ResumeState(RoundPlayerState state, Player player, DateTime now)
        {
            state.Nickname = player.Nickname;
            state.Category = Classify(player.Role.Type);
            state.RoleIntervalStartedUtc = IsTracked(state.Category) ? now : (DateTime?)null;
            state.LifeIntervalStartedUtc = IsLiving(state.Category) ? now : (DateTime?)null;
            if (state.InPocket)
                state.PocketIntervalStartedUtc = now;
        }

        private RoundPlayerState GetState(Player player)
        {
            if (!players.TryGetValue(player.UserId, out RoundPlayerState state))
            {
                state = new RoundPlayerState { UserId = player.UserId, Nickname = player.Nickname, Category = Classify(player.Role.Type) };
                players[player.UserId] = state;
            }
            else
            {
                state.Nickname = player.Nickname;
            }
            return state;
        }

        private void DeactivateRound()
        {
            roundActive = false;
            recordingPaused = false;
            players.Clear();
            pendingEscapes.Clear();
            generatorActivators.Clear();
        }

        private void ResetRoundTracking(string userId, Player onlinePlayer, DateTime now)
        {
            players.Remove(userId);
            pendingEscapes.Remove(userId);
            foreach (Generator generator in generatorActivators
                .Where(pair => string.Equals(pair.Value, userId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList())
            {
                generatorActivators.Remove(generator);
            }

            if (string.Equals(lastWarheadStarterUserId, userId, StringComparison.OrdinalIgnoreCase))
                lastWarheadStarterUserId = null;
            if (string.Equals(rebootStarterUserId, userId, StringComparison.OrdinalIgnoreCase))
                rebootStarterUserId = null;
            if (string.Equals(lastTesla079UserId, userId, StringComparison.OrdinalIgnoreCase))
                lastTesla079UserId = null;

            if (IsRecording && IsRealPlayer(onlinePlayer))
                ResumeState(GetState(onlinePlayer), onlinePlayer, now);
        }

        private void SafePlayerUpdate(string userId, string nickname, PlayerStatDelta delta, DateTime now)
        {
            EnqueueWrite(() => database.UpdatePlayerStatistics(userId, nickname, delta, now), "player " + userId);
        }

        private void SafeServerUpdate(ServerStatDelta delta)
        {
            EnqueueWrite(() => database.UpdateServerStatistics(delta), "server statistics");
        }

        private void EnqueueWrite(Action action, string description)
        {
            if (writeQueue.IsAddingCompleted || !writeQueue.TryAdd(action))
                Log.Error($"[Statistics] MariaDB write queue is full; dropped update for {description}.");
        }

        private void ProcessWrites()
        {
            foreach (Action action in writeQueue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Log.Error($"[Statistics] Failed to persist a queued MariaDB update:\n{exception}");
                }
            }
        }

        private static IEnumerable<Player> OnlinePlayers() => Player.List.Where(IsRealPlayer);

        private static bool IsRealPlayer(Player player) => player != null && player.IsConnected && !player.IsHost && !string.IsNullOrWhiteSpace(player.UserId);

        private static long ElapsedSeconds(DateTime start, DateTime end) => Math.Max(0L, (long)Math.Round((end - start).TotalSeconds));

        private static bool IsTracked(RoleCategory category) => category == RoleCategory.Human || category == RoleCategory.Scp || category == RoleCategory.Spectator;

        private static bool IsLiving(RoleCategory category) => category == RoleCategory.Human || category == RoleCategory.Scp;

        private static RoleCategory Classify(RoleTypeId role)
        {
            if (role == RoleTypeId.Spectator || role == RoleTypeId.Overwatch)
                return RoleCategory.Spectator;
            if (role == RoleTypeId.None || role == RoleTypeId.Destroyed || role == RoleTypeId.Tutorial || role == RoleTypeId.Filmmaker)
                return RoleCategory.Ignored;
            return role.GetTeam() == Team.SCPs ? RoleCategory.Scp : RoleCategory.Human;
        }

        private sealed class RoundPlayerState
        {
            public string UserId { get; set; }
            public string Nickname { get; set; }
            public RoleCategory Category { get; set; }
            public DateTime? RoleIntervalStartedUtc { get; set; }
            public DateTime? LifeIntervalStartedUtc { get; set; }
            public long LifeSeconds { get; set; }
            public bool InPocket { get; set; }
            public DateTime? PocketIntervalStartedUtc { get; set; }
            public long PocketSeconds { get; set; }
            public long HumanKillsThisRound { get; set; }
            public long ScpKillsThisRound { get; set; }
        }

        private sealed class PendingEscape
        {
            public EscapeScenario Scenario { get; set; }
            public string CufferUserId { get; set; }
            public string CufferNickname { get; set; }
        }

        private enum RoleCategory
        {
            Ignored,
            Human,
            Scp,
            Spectator,
        }
    }
}
