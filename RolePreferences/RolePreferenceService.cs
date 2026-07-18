namespace SmokyPluginV2.RolePreferences
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Exiled.API.Extensions;
    using Exiled.API.Features;
    using Exiled.Events.EventArgs.Player;

    using MEC;

    using PlayerRoles;
    using PlayerRoles.RoleAssign;

    using PlayerEvents = Exiled.Events.Handlers.Player;
    using ServerEvents = Exiled.Events.Handlers.Server;

    internal sealed class RolePreferenceService
    {
        private readonly Dictionary<ReferenceHub, RolePreferenceSelection> towerSelections = new Dictionary<ReferenceHub, RolePreferenceSelection>();
        private readonly HashSet<ReferenceHub> reservedHumanPreferenceWinners = new HashSet<ReferenceHub>();
        private readonly Random random = new Random();
        private readonly RolePreferenceSettings settings;
        private readonly RolePreferenceTowerService tower;

        private bool isRegistered;
        private bool runtimePatchesAvailable;
        private bool roleAssignmentInProgress;

        public RolePreferenceService(RolePreferenceSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (settings.Tower?.IsEnabled == true)
                tower = new RolePreferenceTowerService(this, settings.Tower);
        }

        public void Register()
        {
            if (isRegistered)
                return;

            ValidateSettings();
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            PlayerEvents.Verified += OnVerified;
            PlayerEvents.Left += OnLeft;
            PlayerEvents.ChangingGroup += OnChangingGroup;
            ServerEvents.RestartingRound += OnRestartingRound;
            isRegistered = true;

            if (tower is null)
                Log.Warn("[Role Preferences] Tower selector is disabled, so role preferences will not be collected.");
            else
                Log.Info("[Role Preferences] Tower role selector has been enabled.");
        }

        public void Unregister()
        {
            if (!isRegistered)
                return;

            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            PlayerEvents.Verified -= OnVerified;
            PlayerEvents.Left -= OnLeft;
            PlayerEvents.ChangingGroup -= OnChangingGroup;
            ServerEvents.RestartingRound -= OnRestartingRound;

            tower?.StopLobby();
            towerSelections.Clear();
            reservedHumanPreferenceWinners.Clear();
            roleAssignmentInProgress = false;
            isRegistered = false;
        }

        internal void SetRuntimePatchesAvailable(bool available)
        {
            runtimePatchesAvailable = available;
            if (!available)
                tower?.StopLobby();
        }

        internal void BeginRoleAssignment()
        {
            if (!isRegistered)
                return;

            roleAssignmentInProgress = true;
            tower?.BeginRoleAssignment();
        }

        internal void EndRoleAssignment()
        {
            tower?.EndRoleAssignment();
            roleAssignmentInProgress = false;
        }

        internal bool ShouldIncludeTutorial(ReferenceHub hub) =>
            isRegistered &&
            runtimePatchesAvailable &&
            roleAssignmentInProgress &&
            tower?.Contains(hub) == true &&
            hub?.roleManager?.CurrentRole?.RoleTypeId == RoleTypeId.Tutorial;

        public bool TrySpawnPreferredScps(int targetCount)
        {
            if (!isRegistered || targetCount <= 0 || towerSelections.Count == 0)
                return false;

            bool assignmentStarted = false;
            try
            {
                List<ReferenceHub> eligible = ReferenceHub.AllHubs.Where(RoleAssigner.CheckPlayer).ToList();
                if (!eligible.Any(player => GetCategory(player) != RolePreferenceCategory.None))
                    return false;

                List<RoleTypeId> scpRoles = RoleAssignmentAccess.GenerateScpRoles(targetCount);
                List<ReferenceHub> vanillaSelection;
                List<ReferenceHub> selected;
                using (ScpTicketsLoader tickets = new ScpTicketsLoader())
                {
                    vanillaSelection = BuildVanillaScpSelection(eligible, targetCount, tickets);
                    selected = SelectScpPlayers(eligible, vanillaSelection, targetCount);
                    UpdateScpTickets(eligible, selected, tickets);
                }

                if (selected.Count != targetCount || scpRoles.Count != targetCount)
                    throw new InvalidOperationException($"Built {selected.Count} SCP player(s) and {scpRoles.Count} role(s) for a target of {targetCount}.");

                assignmentStarted = true;
                while (scpRoles.Count > 0 && selected.Count > 0)
                {
                    RoleTypeId role = scpRoles[0];
                    scpRoles.RemoveAt(0);
                    RoleAssignmentAccess.AssignScp(selected, role, scpRoles);
                }

                if (scpRoles.Count != 0 || selected.Count != 0)
                    throw new InvalidOperationException($"SCP assignment ended with {selected.Count} player(s) and {scpRoles.Count} role(s) unconsumed.");

                Log.Info($"[Role Preferences] Pre-spawn SCP assignment completed for {targetCount} slot(s). Active: {FormatActivePreferences(eligible)}.");
                return true;
            }
            catch (Exception exception)
            {
                Log.Error($"[Role Preferences] Pre-spawn SCP assignment failed{(assignmentStarted ? " after role assignment began; vanilla fallback is unsafe" : "; vanilla fallback will be used")}:\n{exception}");
                return assignmentStarted;
            }
        }

        public bool TrySpawnPreferredHumans(Team[] humanQueue, int queueLength)
        {
            if (!isRegistered || humanQueue is null || humanQueue.Length == 0 || queueLength <= 0 || towerSelections.Count == 0)
                return false;

            bool assignmentStarted = false;
            try
            {
                List<ReferenceHub> players = ReferenceHub.AllHubs.Where(RoleAssigner.CheckPlayer).ToList();
                if (!players.Any(player => IsHumanPreference(GetCategory(player))))
                    return false;

                List<RoleTypeId> roles = RoleAssignmentAccess.GenerateHumanRoles(humanQueue, queueLength, players.Count);
                List<HumanAssignment> assignments = BuildHumanAssignments(players, roles);
                if (assignments.Count != players.Count)
                    throw new InvalidOperationException($"Built {assignments.Count} human assignment(s) for {players.Count} player(s).");

                assignmentStarted = true;
                foreach (HumanAssignment assignment in assignments)
                {
                    assignment.Player.roleManager.ServerSetRole(assignment.Role, RoleChangeReason.RoundStart);
                    RoleAssignmentAccess.RegisterHumanRole(assignment.Player.authManager.UserId, assignment.Role);
                }

                int satisfied = assignments.Count(assignment => CategoryMatchesRole(GetCategory(assignment.Player), assignment.Role));
                Log.Info($"[Role Preferences] Pre-spawn human assignment completed for {assignments.Count} player(s), satisfying {satisfied} preference(s). Assignments: {FormatHumanAssignments(assignments)}.");
                return true;
            }
            catch (Exception exception)
            {
                Log.Error($"[Role Preferences] Pre-spawn human assignment failed{(assignmentStarted ? " after role assignment began; vanilla fallback is unsafe" : "; vanilla fallback will be used")}:\n{exception}");
                return assignmentStarted;
            }
        }

        private List<ReferenceHub> SelectScpPlayers(
            List<ReferenceHub> eligible,
            List<ReferenceHub> vanillaSelection,
            int slots)
        {
            if (slots <= 0)
                return new List<ReferenceHub>();

            reservedHumanPreferenceWinners.Clear();
            RoleSlotForecast forecast = ForecastSlots(eligible.Count);
            ReserveHumanPreferenceWinners(eligible, RolePreferenceCategory.Scientist, forecast.ScientistSlots);
            ReserveHumanPreferenceWinners(eligible, RolePreferenceCategory.ClassD, forecast.ClassDSlots);
            ReserveHumanPreferenceWinners(eligible, RolePreferenceCategory.FacilityGuard, forecast.FacilityGuardSlots);

            List<ReferenceHub> scpVoters = eligible.Where(player => GetCategory(player) == RolePreferenceCategory.Scp).ToList();

            List<ReferenceHub> selected = scpVoters.Count > slots
                ? DrawWeighted(scpVoters, slots)
                : new List<ReferenceHub>(scpVoters);

            FillScpSlots(selected, eligible.Where(hub => GetCategory(hub) == RolePreferenceCategory.None), vanillaSelection, slots);
            FillScpSlots(
                selected,
                eligible.Where(hub => GetCategory(hub) != RolePreferenceCategory.Scp && !reservedHumanPreferenceWinners.Contains(hub)),
                vanillaSelection,
                slots);
            FillScpSlots(selected, eligible.Where(reservedHumanPreferenceWinners.Contains), vanillaSelection, slots);
            reservedHumanPreferenceWinners.ExceptWith(selected);
            return selected.Take(slots).ToList();
        }

        private void ReserveHumanPreferenceWinners(
            List<ReferenceHub> eligible,
            RolePreferenceCategory category,
            int slots)
        {
            if (slots <= 0)
                return;

            List<ReferenceHub> requesters = eligible.Where(player => GetCategory(player) == category).ToList();
            List<ReferenceHub> winners = requesters.Count > slots
                ? DrawWeighted(requesters, slots)
                : requesters;

            foreach (ReferenceHub winner in winners)
                reservedHumanPreferenceWinners.Add(winner);
        }

        private void OnWaitingForPlayers()
        {
            towerSelections.Clear();
            reservedHumanPreferenceWinners.Clear();

            if (tower is null)
                return;

            if (runtimePatchesAvailable)
                tower.StartLobby();
            else
                Log.Error("[Role Preferences] Tower lobby was not started because the runtime role-assignment patches are unavailable.");
        }

        private void OnVerified(VerifiedEventArgs ev)
        {
            Player player = ev.Player;
            Timing.CallDelayed(0.5f, () =>
            {
                if (!isRegistered || player is null || !player.IsConnected)
                    return;

                if (tower is not null && runtimePatchesAvailable && Round.IsLobby)
                    tower.Stage(player);
            });
        }

        private void OnLeft(LeftEventArgs ev)
        {
            if (ev.Player?.ReferenceHub is ReferenceHub hub)
            {
                towerSelections.Remove(hub);
                tower?.Remove(hub);
            }
        }

        private void OnChangingGroup(ChangingGroupEventArgs ev)
        {
            Player player = ev.Player;
            Timing.CallDelayed(0.1f, () =>
            {
                if (isRegistered && player is not null && player.IsConnected)
                {
                    tower?.MarkProbabilityDirty();
                }
            });
        }

        private void OnRestartingRound()
        {
            tower?.StopLobby();
            towerSelections.Clear();
            roleAssignmentInProgress = false;
        }

        internal void SetTowerSelection(Player player, RolePreferenceCategory category)
        {
            ReferenceHub hub = player?.ReferenceHub;
            if (hub is null || category == RolePreferenceCategory.None)
                return;

            towerSelections[hub] = new RolePreferenceSelection
            {
                Category = category,
            };
        }

        internal bool ClearTowerSelection(Player player)
        {
            ReferenceHub hub = player?.ReferenceHub;
            return hub is not null && towerSelections.Remove(hub);
        }

        internal RolePreferenceCategory GetTowerSelection(ReferenceHub hub) => GetCategory(hub);

        internal double GetTowerWeight(ReferenceHub hub) => GetEffectiveWeight(hub);

        internal Dictionary<ReferenceHub, double> CalculateTowerProbabilities(IEnumerable<ReferenceHub> source)
        {
            List<ReferenceHub> players = source
                .Where(hub => hub is not null && Player.Get(hub)?.IsConnected == true)
                .Distinct()
                .ToList();
            Dictionary<ReferenceHub, double> result = players.ToDictionary(player => player, _ => 0d);
            if (players.Count == 0)
                return result;

            RoleSlotForecast slots = ForecastSlots(players.Count);
            CalculateExactCategoryProbability(players, RolePreferenceCategory.Scp, slots.ScpSlots, result);
            CalculateExactCategoryProbability(players, RolePreferenceCategory.Scientist, slots.ScientistSlots, result);
            CalculateExactCategoryProbability(players, RolePreferenceCategory.ClassD, slots.ClassDSlots, result);
            CalculateExactCategoryProbability(players, RolePreferenceCategory.FacilityGuard, slots.FacilityGuardSlots, result);

            return result;
        }

        internal RoleSlotForecast GetTowerSlotForecast(int playerCount) => ForecastSlots(playerCount);

        private void CalculateExactCategoryProbability(
            List<ReferenceHub> players,
            RolePreferenceCategory category,
            int slotCount,
            Dictionary<ReferenceHub, double> result)
        {
            List<ReferenceHub> requesters = players.Where(player => GetCategory(player) == category).ToList();
            if (requesters.Count == 0 || slotCount <= 0)
                return;

            if (requesters.Count <= slotCount)
            {
                foreach (ReferenceHub requester in requesters)
                    result[requester] = 100d;

                return;
            }

            List<ProbabilityWeightGroup> groups = requesters
                .GroupBy(GetEffectiveWeight)
                .OrderBy(group => group.Key)
                .Select(group => new ProbabilityWeightGroup(group.Key, group.ToList()))
                .ToList();

            Dictionary<ProbabilityState, double> states = new Dictionary<ProbabilityState, double>
            {
                [new ProbabilityState(new int[groups.Count])] = 1d,
            };

            for (int draw = 0; draw < slotCount; draw++)
            {
                Dictionary<ProbabilityState, double> next = new Dictionary<ProbabilityState, double>();
                foreach (KeyValuePair<ProbabilityState, double> stateEntry in states)
                {
                    double remainingTotalWeight = 0;
                    for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                    {
                        int remaining = groups[groupIndex].Players.Count - stateEntry.Key.Counts[groupIndex];
                        remainingTotalWeight += remaining * groups[groupIndex].Weight;
                    }

                    if (remainingTotalWeight <= 0)
                        continue;

                    for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                    {
                        ProbabilityWeightGroup group = groups[groupIndex];
                        int remaining = group.Players.Count - stateEntry.Key.Counts[groupIndex];
                        if (remaining <= 0)
                            continue;

                        int[] nextCounts = (int[])stateEntry.Key.Counts.Clone();
                        nextCounts[groupIndex]++;
                        ProbabilityState nextState = new ProbabilityState(nextCounts);
                        double transitionProbability = remaining * group.Weight / remainingTotalWeight;
                        double probability = stateEntry.Value * transitionProbability;
                        if (next.TryGetValue(nextState, out double existing))
                            next[nextState] = existing + probability;
                        else
                            next[nextState] = probability;
                    }
                }

                states = next;
            }

            double[] expectedWinners = new double[groups.Count];
            foreach (KeyValuePair<ProbabilityState, double> stateEntry in states)
            {
                for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                    expectedWinners[groupIndex] += stateEntry.Value * stateEntry.Key.Counts[groupIndex];
            }

            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                ProbabilityWeightGroup group = groups[groupIndex];
                double playerProbability = expectedWinners[groupIndex] * 100d / group.Players.Count;
                foreach (ReferenceHub player in group.Players)
                    result[player] = playerProbability;
            }
        }

        private static RoleSlotForecast ForecastSlots(int playerCount)
        {
            RoleSlotForecast result = new RoleSlotForecast();
            if (playerCount <= 0)
                return result;

            string configuredQueue = GameCore.ConfigFile.ServerConfig.GetString(RoleAssigner.SpawnQueueKey, RoleAssigner.DefaultQueue);
            List<Team> totalQueue = new List<Team>();
            List<Team> humanQueue = new List<Team>();
            foreach (char character in configuredQueue)
            {
                Team team = (Team)(character - '0');
                if (!Enum.IsDefined(typeof(Team), team))
                    continue;

                totalQueue.Add(team);
                if (team != Team.SCPs)
                    humanQueue.Add(team);
            }

            if (totalQueue.Count == 0)
                return result;

            bool allowOverflow = GameCore.ConfigFile.ServerConfig.GetBool(RoleAssigner.AllowScpOverflowSettingKey);
            int maxScps = ScpSpawner.MaxSpawnableScps;
            for (int index = 0; index < playerCount; index++)
            {
                if (totalQueue[index % totalQueue.Count] != Team.SCPs)
                    continue;

                result.ScpSlots++;
                if (result.ScpSlots == maxScps && !allowOverflow)
                    break;
            }

            int humanCount = playerCount - result.ScpSlots;
            for (int index = 0; index < humanCount; index++)
            {
                Team team = humanQueue.Count == 0 ? Team.ClassD : humanQueue[index % humanQueue.Count];
                switch (team)
                {
                    case Team.Scientists:
                        result.ScientistSlots++;
                        break;
                    case Team.FoundationForces:
                        result.FacilityGuardSlots++;
                        break;
                    default:
                        result.ClassDSlots++;
                        break;
                }
            }

            return result;
        }

        private string FormatActivePreferences(IEnumerable<ReferenceHub> players) => string.Join(
            ", ",
            players
                .Where(player => GetCategory(player) != RolePreferenceCategory.None)
                .Select(player => $"{player.authManager.UserId}={GetCategory(player)}"));

        private string FormatHumanAssignments(IEnumerable<HumanAssignment> assignments) => string.Join(
            ", ",
            assignments.Select(assignment => $"{assignment.Player.authManager.UserId}={assignment.Role}"));

        private List<ReferenceHub> BuildVanillaScpSelection(
            List<ReferenceHub> eligible,
            int targetCount,
            ScpTicketsLoader tickets)
        {
            List<ReferenceHub> selected = new List<ReferenceHub>(targetCount);
            if (targetCount <= 0 || eligible.Count == 0)
                return selected;

            int highestTickets = 0;
            List<ReferenceHub> highest = new List<ReferenceHub>();
            foreach (ReferenceHub player in eligible)
            {
                int count = tickets.GetTickets(player, 10);
                if (count < highestTickets)
                    continue;

                if (count > highestTickets)
                {
                    highest.Clear();
                    highestTickets = count;
                }

                highest.Add(player);
            }

            if (highest.Count > 0)
                selected.Add(highest[random.Next(highest.Count)]);

            int slotsLeft = targetCount - selected.Count;
            List<ScpTicketCandidate> candidates = new List<ScpTicketCandidate>();
            foreach (ReferenceHub player in eligible.Where(player => !selected.Contains(player)))
            {
                long weight = 1;
                int playerTickets = tickets.GetTickets(player, 10);
                for (int i = 0; i < slotsLeft; i++)
                    weight = unchecked(weight * playerTickets);

                candidates.Add(new ScpTicketCandidate(player, weight));
            }

            while (selected.Count < targetCount && candidates.Count > 0)
            {
                long totalWeight = 0;
                foreach (ScpTicketCandidate candidate in candidates)
                    totalWeight = unchecked(totalWeight + candidate.Weight);
                int winnerIndex = candidates.Count - 1;
                if (totalWeight <= 0)
                {
                    winnerIndex = random.Next(candidates.Count);
                }
                else
                {
                    double roll = random.NextDouble() * totalWeight;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        roll -= candidates[i].Weight;
                        if (roll <= 0)
                        {
                            winnerIndex = i;
                            break;
                        }
                    }
                }

                selected.Add(candidates[winnerIndex].Player);
                candidates.RemoveAt(winnerIndex);
            }

            return selected;
        }

        private static void UpdateScpTickets(
            IEnumerable<ReferenceHub> eligible,
            IEnumerable<ReferenceHub> selected,
            ScpTicketsLoader tickets)
        {
            foreach (ReferenceHub player in eligible)
            {
                if (!ScpPlayerPicker.IsOptedOutOfScp(player))
                    tickets.ModifyTickets(player, tickets.GetTickets(player, 10) + 2);
            }

            foreach (ReferenceHub player in selected)
                tickets.ModifyTickets(player, 10);
        }

        private List<HumanAssignment> BuildHumanAssignments(List<ReferenceHub> players, List<RoleTypeId> roles)
        {
            List<ReferenceHub> remainingPlayers = new List<ReferenceHub>(players);
            List<RoleTypeId> remainingRoles = new List<RoleTypeId>(roles);
            List<HumanAssignment> assignments = new List<HumanAssignment>(players.Count);

            AssignRequestedHumanRole(RoleTypeId.Scientist, RolePreferenceCategory.Scientist, remainingPlayers, remainingRoles, assignments);
            AssignRequestedHumanRole(RoleTypeId.ClassD, RolePreferenceCategory.ClassD, remainingPlayers, remainingRoles, assignments);
            AssignRequestedHumanRole(RoleTypeId.FacilityGuard, RolePreferenceCategory.FacilityGuard, remainingPlayers, remainingRoles, assignments);

            Shuffle(remainingRoles);
            foreach (RoleTypeId role in remainingRoles)
            {
                ReferenceHub player = ChooseByHumanHistory(remainingPlayers, role);
                if (player is null)
                    break;

                assignments.Add(new HumanAssignment(player, role));
                remainingPlayers.Remove(player);
            }

            reservedHumanPreferenceWinners.Clear();
            return assignments;
        }

        private void AssignRequestedHumanRole(
            RoleTypeId role,
            RolePreferenceCategory category,
            List<ReferenceHub> remainingPlayers,
            List<RoleTypeId> remainingRoles,
            List<HumanAssignment> assignments)
        {
            int slots = remainingRoles.Count(candidate => candidate == role);
            if (slots <= 0)
                return;

            List<ReferenceHub> requesters = remainingPlayers.Where(player => GetCategory(player) == category).ToList();
            List<ReferenceHub> winners = requesters
                .Where(reservedHumanPreferenceWinners.Contains)
                .Take(slots)
                .ToList();

            int slotsLeft = slots - winners.Count;
            if (slotsLeft > 0)
            {
                List<ReferenceHub> unreserved = requesters.Where(player => !winners.Contains(player)).ToList();
                List<ReferenceHub> additional = unreserved.Count > slotsLeft
                    ? DrawWeighted(unreserved, slotsLeft)
                    : unreserved.Take(slotsLeft).ToList();
                winners.AddRange(additional);
            }

            foreach (ReferenceHub winner in winners)
            {
                assignments.Add(new HumanAssignment(winner, role));
                remainingPlayers.Remove(winner);
                remainingRoles.Remove(role);
            }
        }

        private ReferenceHub ChooseByHumanHistory(List<ReferenceHub> players, RoleTypeId role)
        {
            int minimum = int.MaxValue;
            List<ReferenceHub> candidates = new List<ReferenceHub>();
            foreach (ReferenceHub player in players)
            {
                int count = RoleAssignmentAccess.GetHumanRoleCount(player.authManager.UserId, role);
                if (count < minimum)
                {
                    minimum = count;
                    candidates.Clear();
                }

                if (count == minimum)
                    candidates.Add(player);
            }

            return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
        }

        private void FillScpSlots(
            List<ReferenceHub> selected,
            IEnumerable<ReferenceHub> pool,
            List<ReferenceHub> vanillaSelection,
            int targetCount)
        {
            if (selected.Count >= targetCount)
                return;

            List<ReferenceHub> available = pool.Where(hub => !selected.Contains(hub)).Distinct().ToList();
            foreach (ReferenceHub vanillaPlayer in vanillaSelection)
            {
                if (selected.Count >= targetCount)
                    return;

                if (available.Remove(vanillaPlayer))
                    selected.Add(vanillaPlayer);
            }

            Shuffle(available);
            foreach (ReferenceHub player in available)
            {
                if (selected.Count >= targetCount)
                    return;

                selected.Add(player);
            }
        }

        private List<ReferenceHub> DrawWeighted(List<ReferenceHub> candidates, int count)
        {
            return DrawWeighted(candidates, count, random);
        }

        private List<ReferenceHub> DrawWeighted(List<ReferenceHub> candidates, int count, Random randomSource)
        {
            List<ReferenceHub> remaining = new List<ReferenceHub>(candidates);
            List<ReferenceHub> winners = new List<ReferenceHub>(Math.Min(count, remaining.Count));

            while (winners.Count < count && remaining.Count > 0)
            {
                double total = remaining.Sum(GetEffectiveWeight);
                double roll = randomSource.NextDouble() * total;
                ReferenceHub winner = remaining[remaining.Count - 1];

                foreach (ReferenceHub candidate in remaining)
                {
                    roll -= GetEffectiveWeight(candidate);
                    if (roll < 0)
                    {
                        winner = candidate;
                        break;
                    }
                }

                winners.Add(winner);
                remaining.Remove(winner);
            }

            return winners;
        }

        private double GetEffectiveWeight(ReferenceHub hub) => GetEffectiveWeight(Player.Get(hub));

        private double GetEffectiveWeight(Player player)
        {
            if (player is null)
                return GetDefaultWeight();

            return GetConfiguredWeight(player);
        }

        private double GetConfiguredWeight(Player player)
        {
            RolePreferencePriorityTier tier = FindTier(player);
            return tier is null ? GetDefaultWeight() : SanitizeWeight(tier.Weight, GetDefaultWeight());
        }

        private double GetDefaultWeight() => SanitizeWeight(settings.DefaultWeight, 1);

        private RolePreferencePriorityTier FindTier(Player player)
        {
            string group = player?.Group?.GetKey();
            if (string.IsNullOrWhiteSpace(group) || settings.PriorityTiers is null)
                return null;

            RolePreferencePriorityTier best = null;
            double bestWeight = double.MinValue;
            foreach (RolePreferencePriorityTier tier in settings.PriorityTiers)
            {
                if (tier?.Groups is null || !tier.Groups.Any(candidate => string.Equals(candidate?.Trim(), group, StringComparison.OrdinalIgnoreCase)))
                    continue;

                double weight = SanitizeWeight(tier.Weight, GetDefaultWeight());
                if (best is null || weight > bestWeight)
                {
                    best = tier;
                    bestWeight = weight;
                }
            }

            return best;
        }

        private RolePreferenceCategory GetCategory(ReferenceHub hub)
        {
            if (hub is not null && towerSelections.TryGetValue(hub, out RolePreferenceSelection towerSelection))
                return towerSelection.Category;

            return RolePreferenceCategory.None;
        }

        private void ValidateSettings()
        {
            if (settings.DefaultWeight <= 0 || double.IsNaN(settings.DefaultWeight) || double.IsInfinity(settings.DefaultWeight))
                Log.Warn("[Role Preferences] default_weight is invalid and will be treated as 1.0.");

            if (settings.PriorityTiers is null)
                return;

            Dictionary<string, string> seenGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (RolePreferencePriorityTier tier in settings.PriorityTiers.Where(tier => tier is not null))
            {
                if (tier.Weight <= 0 || double.IsNaN(tier.Weight) || double.IsInfinity(tier.Weight))
                    Log.Warn($"[Role Preferences] Tier '{tier.Name}' has an invalid weight and will use default_weight.");

                if (tier.Groups is null)
                    continue;

                foreach (string rawGroup in tier.Groups)
                {
                    string group = rawGroup?.Trim();
                    if (string.IsNullOrWhiteSpace(group))
                        continue;

                    if (seenGroups.TryGetValue(group, out string previousTier))
                        Log.Warn($"[Role Preferences] Group '{group}' appears in tiers '{previousTier}' and '{tier.Name}'. The highest valid weight will be used.");
                    else
                        seenGroups[group] = tier.Name;
                }
            }
        }

        private void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T value = list[i];
                list[i] = list[j];
                list[j] = value;
            }
        }

        private static double SanitizeWeight(double value, double fallback) =>
            value > 0 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : fallback;

        private static bool IsHumanPreference(RolePreferenceCategory category) =>
            category == RolePreferenceCategory.Scientist ||
            category == RolePreferenceCategory.ClassD ||
            category == RolePreferenceCategory.FacilityGuard;

        private static bool CategoryMatchesRole(RolePreferenceCategory category, RoleTypeId role) =>
            (category == RolePreferenceCategory.Scientist && role == RoleTypeId.Scientist) ||
            (category == RolePreferenceCategory.ClassD && role == RoleTypeId.ClassD) ||
            (category == RolePreferenceCategory.FacilityGuard && role == RoleTypeId.FacilityGuard);

        private sealed class ProbabilityWeightGroup
        {
            public ProbabilityWeightGroup(double weight, List<ReferenceHub> players)
            {
                Weight = weight;
                Players = players;
            }

            public double Weight { get; }

            public List<ReferenceHub> Players { get; }
        }

        private sealed class ProbabilityState : IEquatable<ProbabilityState>
        {
            private readonly int hashCode;

            public ProbabilityState(int[] counts)
            {
                Counts = counts;
                unchecked
                {
                    int hash = 17;
                    foreach (int count in counts)
                        hash = (hash * 31) + count;

                    hashCode = hash;
                }
            }

            public int[] Counts { get; }

            public bool Equals(ProbabilityState other)
            {
                if (ReferenceEquals(this, other))
                    return true;
                if (other is null || hashCode != other.hashCode || Counts.Length != other.Counts.Length)
                    return false;

                for (int i = 0; i < Counts.Length; i++)
                {
                    if (Counts[i] != other.Counts[i])
                        return false;
                }

                return true;
            }

            public override bool Equals(object obj) => Equals(obj as ProbabilityState);

            public override int GetHashCode() => hashCode;
        }

        private sealed class HumanAssignment
        {
            public HumanAssignment(ReferenceHub player, RoleTypeId role)
            {
                Player = player;
                Role = role;
            }

            public ReferenceHub Player { get; }

            public RoleTypeId Role { get; }
        }

        private sealed class ScpTicketCandidate
        {
            public ScpTicketCandidate(ReferenceHub player, long weight)
            {
                Player = player;
                Weight = weight;
            }

            public ReferenceHub Player { get; }

            public long Weight { get; }
        }
    }
}
