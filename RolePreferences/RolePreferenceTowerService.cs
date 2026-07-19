namespace SmokyPluginV2.RolePreferences
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using Exiled.API.Extensions;
    using Exiled.API.Features;

    using GameCore;

    using MEC;

    using PlayerRoles;

    using UnityEngine;

    using ExiledLight = Exiled.API.Features.Toys.Light;
    using ExiledPrimitive = Exiled.API.Features.Toys.Primitive;
    using ExiledText = Exiled.API.Features.Toys.Text;
    using ExiledToy = Exiled.API.Features.Toys.AdminToy;

    internal sealed class RolePreferenceTowerService
    {
        private const float LoopInterval = 0.1f;
        private const float HintInterval = 0.25f;
        private const float HintDuration = 2f;
        private const float NativeSpawnNeutralRadiusSquared = 0.0625f;

        private readonly RolePreferenceService owner;
        private readonly RolePreferenceTowerSettings settings;
        private readonly Dictionary<ReferenceHub, ParticipantState> participants = new Dictionary<ReferenceHub, ParticipantState>(ReferenceHubReferenceComparer.Instance);
        private readonly Dictionary<RolePreferenceCategory, Vector3> zonePositions = new Dictionary<RolePreferenceCategory, Vector3>();
        private readonly HashSet<ReferenceHub> eventBriefingMutedPlayers = new HashSet<ReferenceHub>(ReferenceHubReferenceComparer.Instance);
        private readonly List<ExiledToy> toys = new List<ExiledToy>();
        private Dictionary<ReferenceHub, double> probabilities = new Dictionary<ReferenceHub, double>(ReferenceHubReferenceComparer.Instance);
        private CoroutineHandle loop;
        private bool loopRunning;
        private bool lobbyActive;
        private bool probabilityDirty = true;
        private Transform startRoundTransform;
        private Vector3 startRoundOriginalScale;
        private bool startRoundScreenHidden;
        private bool markersCreated;
        private float runtimeZoneHalfSize;
        private Vector3 randomSelectionCenter;
        private bool randomSelectionCenterSet;
        private float nextHintAt;
        private float nextLoopErrorLogAt;
        private bool eventBriefingActive;

        public RolePreferenceTowerService(RolePreferenceService owner, RolePreferenceTowerSettings settings)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public bool Contains(ReferenceHub hub) => hub is not null && participants.ContainsKey(hub);

        public void StartLobby()
        {
            StopLobby();
            lobbyActive = true;
            probabilityDirty = true;
            nextHintAt = 0;
            HideStartRoundScreen();

            foreach (Player player in Player.List)
                Stage(player);

            loop = Timing.RunCoroutine(LobbyLoop());
            loopRunning = true;
            Log.Info("[Role Preferences] Tower lobby started. Players will be assigned directly from Tutorial by the atomic role allocator.");
        }

        public void StopLobby()
        {
            DisableEventBriefing();
            lobbyActive = false;
            StopLoop();
            ClearParticipantHints();
            DestroyMarkers();
            RestoreStartRoundScreen();
            foreach (ReferenceHub hub in participants.Keys.ToList())
            {
                Player player = Player.Get(hub);
                if (player is not null && player.IsConnected)
                    player.IsGodModeEnabled = false;
            }

            participants.Clear();
            probabilities.Clear();
            probabilityDirty = true;
        }

        public void BeginRoleAssignment()
        {
            DisableEventBriefing();
            lobbyActive = false;
            StopLoop();
            ClearParticipantHints();
            DestroyMarkers();
            // Keep StartRound hidden here. The game removes its lobby screen as the
            // round transitions; restoring it in this small window causes a visible flash.
        }

        public void EndRoleAssignment()
        {
            foreach (ReferenceHub hub in participants.Keys.ToList())
            {
                Player player = Player.Get(hub);
                if (player is not null && player.IsConnected)
                    player.IsGodModeEnabled = false;
            }

            Log.Info($"[Role Preferences] Tower lobby handed {participants.Count} Tutorial participant(s) to the atomic role allocator.");
            participants.Clear();
            probabilities.Clear();
        }

        private void ClearParticipantHints()
        {
            foreach (ReferenceHub hub in participants.Keys.ToList())
            {
                Player player = Player.Get(hub);
                if (player is not null && player.IsConnected)
                    player.ShowHint(string.Empty, 0.1f);
            }
        }

        public void Stage(Player player)
        {
            if (!lobbyActive || player is null)
                return;

            ReferenceHub hub = player.ReferenceHub;
            if (!IsLiveHub(hub) || !player.IsConnected)
                return;

            if (!participants.TryGetValue(hub, out ParticipantState state))
            {
                state = new ParticipantState { NativeSpawnReadyAt = Time.realtimeSinceStartup + 0.25f };
                participants.Add(hub, state);
                probabilityDirty = true;
            }

            float now = Time.realtimeSinceStartup;
            if (player.Role.Type != RoleTypeId.Tutorial && now - state.LastRoleSetAt >= 0.75f)
            {
                state.LastRoleSetAt = now;
                state.NativeSpawnReadyAt = now + 0.5f;
                player.Role.Set(RoleTypeId.Tutorial);
                player.ClearInventory();
                player.IsGodModeEnabled = true;
            }
            else if (player.Role.Type == RoleTypeId.Tutorial)
            {
                player.IsGodModeEnabled = true;
            }

            SyncEventBriefingMute(player);
        }

        public void Remove(ReferenceHub hub)
        {
            if (hub is null)
                return;

            if (participants.Remove(hub))
                probabilityDirty = true;

            probabilities.Remove(hub);
            eventBriefingMutedPlayers.Remove(hub);
            owner.ForgetTowerSelection(hub);
        }

        public void MarkProbabilityDirty() => probabilityDirty = true;

        public bool TryToggleEventBriefing(out bool enabled, out string error)
        {
            enabled = eventBriefingActive;
            EventBriefingSettings briefing = settings.EventBriefing;
            if (briefing?.IsEnabled != true)
            {
                error = "Система блокировки лобби для ивента отключена в конфиге.";
                return false;
            }

            if (!lobbyActive || !Round.IsLobby || RoundStart.singleton is null)
            {
                error = "Команда доступна только во время лобби в башне.";
                return false;
            }

            if (eventBriefingActive)
                DisableEventBriefing();
            else
                EnableEventBriefing();

            enabled = eventBriefingActive;
            error = null;
            return true;
        }

        private IEnumerator<float> LobbyLoop()
        {
            while (lobbyActive)
            {
                try
                {
                    RunLobbyTick();
                }
                catch (Exception exception)
                {
                    probabilityDirty = true;
                    nextHintAt = 0;

                    float now = Time.realtimeSinceStartup;
                    if (now >= nextLoopErrorLogAt)
                    {
                        nextLoopErrorLogAt = now + 5f;
                        Log.Error($"[Role Preferences] Tower lobby tick failed but the update loop was kept alive:\n{exception}");
                    }
                }

                yield return Timing.WaitForSeconds(LoopInterval);
            }
        }

        private void RunLobbyTick()
        {
            PruneInvalidParticipants();

            foreach (Player player in Player.List.ToList())
                Stage(player);

            float now = Time.realtimeSinceStartup;
            TryCreateMarkers(now);
            foreach (KeyValuePair<ReferenceHub, ParticipantState> pair in participants.ToList())
            {
                if (!IsLiveHub(pair.Key))
                {
                    Remove(pair.Key);
                    continue;
                }

                Player player = Player.Get(pair.Key);
                if (player is null || !player.IsConnected)
                {
                    Remove(pair.Key);
                    continue;
                }

                UpdateSelection(player);
            }

            if (now < nextHintAt)
                return;

            RefreshProbabilities();
            foreach (ReferenceHub hub in participants.Keys.ToList())
            {
                if (!IsLiveHub(hub))
                {
                    Remove(hub);
                    continue;
                }

                Player player = Player.Get(hub);
                if (player is not null && player.IsConnected)
                    ShowHint(player);
            }

            nextHintAt = now + HintInterval;
        }

        private void PruneInvalidParticipants()
        {
            foreach (ReferenceHub hub in participants.Keys.ToList())
            {
                if (!IsLiveHub(hub))
                    Remove(hub);
            }
        }

        private static bool IsLiveHub(ReferenceHub hub) =>
            hub != null && ReferenceHub.AllHubs.Contains(hub);

        private void UpdateSelection(Player player)
        {
            RolePreferenceCategory nearby = FindZone(player.Position);
            if (nearby == RolePreferenceCategory.None)
            {
                if (owner.ClearTowerSelection(player))
                {
                    probabilityDirty = true;
                    nextHintAt = 0;
                }

                return;
            }

            if (owner.GetTowerSelection(player.ReferenceHub) == nearby)
                return;

            owner.SetTowerSelection(player, nearby);
            probabilityDirty = true;
            nextHintAt = 0;
        }

        private RolePreferenceCategory FindZone(Vector3 position)
        {
            if (!markersCreated)
                return RolePreferenceCategory.None;

            if (randomSelectionCenterSet && HorizontalDistanceSquared(position, randomSelectionCenter) <= NativeSpawnNeutralRadiusSquared)
                return RolePreferenceCategory.None;

            float radius = Math.Max(0.5f, runtimeZoneHalfSize > 0 ? runtimeZoneHalfSize : settings.ZoneRadius);
            if (IsInsideSquare(position, zonePositions[RolePreferenceCategory.Scp], radius))
                return RolePreferenceCategory.Scp;
            if (IsInsideSquare(position, zonePositions[RolePreferenceCategory.Scientist], radius))
                return RolePreferenceCategory.Scientist;
            if (IsInsideSquare(position, zonePositions[RolePreferenceCategory.ClassD], radius))
                return RolePreferenceCategory.ClassD;
            if (IsInsideSquare(position, zonePositions[RolePreferenceCategory.FacilityGuard], radius))
                return RolePreferenceCategory.FacilityGuard;
            return RolePreferenceCategory.None;
        }

        private void RefreshProbabilities()
        {
            if (!probabilityDirty)
                return;

            probabilities = owner.CalculateTowerProbabilities(participants.Keys);
            probabilityDirty = false;
        }

        private void ShowHint(Player player)
        {
            RolePreferenceCategory category = owner.GetTowerSelection(player.ReferenceHub);
            string timer = GetTimerText();
            string role = GetDisplayName(category);
            string color = category == RolePreferenceCategory.None ? "#D0D0D0" : GetHexColor(category);
            string probability = category == RolePreferenceCategory.None
                ? "—"
                : probabilities.TryGetValue(player.ReferenceHub, out double chance) ? $"{chance:0.0}%" : "расчёт...";
            RoleSlotForecast forecast = owner.GetTowerSlotForecast(participants.Count);
            int requested = participants.Keys.Count(hub => owner.GetTowerSelection(hub) == category);
            string competition = category == RolePreferenceCategory.None
                ? settings.RandomInstructionText ?? string.Empty
                : (settings.CompetitionText ?? string.Empty)
                    .Replace("{requested}", requested.ToString())
                    .Replace("{slots}", forecast.GetSlots(category).ToString())
                    .Replace("{weight}", owner.GetTowerWeight(player.ReferenceHub).ToString("0.##"));
            string playersConnected = (settings.LobbyTimerPlayersConnected ?? string.Empty)
                .Replace("{players}", participants.Count.ToString());
            string selectedClass = (settings.SelectedClassText ?? string.Empty)
                .Replace("{role}", role)
                .Replace("{color}", color);
            string probabilityLine = (settings.ProbabilityText ?? string.Empty)
                .Replace("{probability}", probability);
            int competitionSize = category == RolePreferenceCategory.None ? 20 : 17;

            if (eventBriefingActive)
            {
                string announcement = settings.EventBriefing?.AnnouncementText ?? string.Empty;
                player.ShowHint(
                    $"<size=28><b>{timer}</b></size>\n" +
                    $"<size=28><b>{playersConnected}</b></size>\n" +
                    announcement,
                    HintDuration);
                return;
            }

            player.ShowHint(
                $"<size=28><b>{timer}</b></size>\n" +
                $"<size=28><b>{playersConnected}</b></size>\n" +
                $"{selectedClass}\n" +
                $"{probabilityLine}\n" +
                $"<size={competitionSize}>{competition}</size>",
                HintDuration);
        }

        private void EnableEventBriefing()
        {
            if (eventBriefingActive)
                return;

            RoundStart.LobbyLock = true;
            eventBriefingActive = true;
            nextHintAt = 0;
            foreach (ReferenceHub hub in participants.Keys.ToList())
                SyncEventBriefingMute(Player.Get(hub));

            Log.Info("[Role Preferences] Event briefing enabled: lobby locked and participants temporarily muted.");
        }

        private void DisableEventBriefing()
        {
            if (!eventBriefingActive && eventBriefingMutedPlayers.Count == 0)
                return;

            eventBriefingActive = false;
            RoundStart.LobbyLock = false;
            foreach (ReferenceHub hub in eventBriefingMutedPlayers.ToList())
                RemoveEventBriefingMute(Player.Get(hub));

            eventBriefingMutedPlayers.Clear();
            nextHintAt = 0;
            Log.Info("[Role Preferences] Event briefing disabled: lobby lock and plugin-owned temporary mutes removed.");
        }

        private void SyncEventBriefingMute(Player player)
        {
            if (!eventBriefingActive || player is null || !player.IsConnected || !Contains(player.ReferenceHub))
                return;

            if (IsInConfiguredGroup(player, settings.EventBriefing?.MuteExemptGroups))
            {
                RemoveEventBriefingMute(player);
                return;
            }

            ReferenceHub hub = player.ReferenceHub;
            if (eventBriefingMutedPlayers.Contains(hub))
            {
                if (!player.IsMuted)
                    player.IsMuted = true;
                return;
            }

            // A mute that existed before the briefing belongs to the game or an
            // administrator. Do not claim ownership of it and never clear it.
            if (player.IsMuted)
                return;

            player.IsMuted = true;
            eventBriefingMutedPlayers.Add(hub);
        }

        private void RemoveEventBriefingMute(Player player)
        {
            ReferenceHub hub = player?.ReferenceHub;
            if (hub is null || !eventBriefingMutedPlayers.Remove(hub) || !player.IsConnected)
                return;

            if (!IsInPersistentMuteFile(player.UserId))
                player.IsMuted = false;
        }

        private static bool IsInPersistentMuteFile(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return true;

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCP Secret Laboratory",
                "config",
                Server.Port.ToString(),
                "mutes.txt");

            if (!File.Exists(path))
                return false;

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        if (string.Equals(line.Trim(), userId, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                return false;
            }
            catch (Exception exception)
            {
                Log.Error($"[Role Preferences] Could not verify persistent mute file '{path}'. The temporary mute will be kept to avoid revoking an administrative mute:\n{exception}");
                return true;
            }
        }

        private string GetTimerText()
        {
            short timer = RoundStart.singleton is null ? (short)-2 : RoundStart.singleton.NetworkTimer;
            if (timer > 0)
                return (settings.LobbyTimerCountdown ?? string.Empty).Replace("{time}", timer.ToString());
            if (timer == -2)
                return settings.LobbyTimerRoundPaused ?? string.Empty;
            return settings.LobbyTimerRoundStarting ?? string.Empty;
        }

        private void TryCreateMarkers(float now)
        {
            if (markersCreated)
                return;

            foreach (KeyValuePair<ReferenceHub, ParticipantState> pair in participants)
            {
                Player player = Player.Get(pair.Key);
                if (player is null || !player.IsConnected || player.Role.Type != RoleTypeId.Tutorial || now < pair.Value.NativeSpawnReadyAt)
                    continue;

                CreateMarkers(player.Position);
                return;
            }
        }

        private void CreateMarkers(Vector3 nativeTutorialSpawn)
        {
            try
            {
                randomSelectionCenter = nativeTutorialSpawn;
                randomSelectionCenterSet = true;
                Vector3 center = settings.UseDynamicZonePositions
                    ? CalculateDynamicZonePositions(nativeTutorialSpawn)
                    : LoadConfiguredZonePositions();

                CreateZone(RolePreferenceCategory.Scp, zonePositions[RolePreferenceCategory.Scp], new Color(0.92f, 0.12f, 0.12f));
                CreateZone(RolePreferenceCategory.Scientist, zonePositions[RolePreferenceCategory.Scientist], new Color(1f, 0.82f, 0.08f));
                CreateZone(RolePreferenceCategory.ClassD, zonePositions[RolePreferenceCategory.ClassD], new Color(1f, 0.38f, 0.04f));
                CreateZone(RolePreferenceCategory.FacilityGuard, zonePositions[RolePreferenceCategory.FacilityGuard], new Color(0.52f, 0.55f, 0.6f));

                ExiledLight light = ExiledLight.Create(center + new Vector3(0f, 3f, 0f), null, Vector3.one, true, Color.white);
                light.Intensity = 1.2f;
                light.Range = 16f;
                toys.Add(light);
                markersCreated = true;
                Log.Info($"[Role Preferences] Tower zones created around native Tutorial spawn {FormatVector(nativeTutorialSpawn)}: " +
                    $"SCP {FormatVector(zonePositions[RolePreferenceCategory.Scp])}, " +
                    $"Scientist {FormatVector(zonePositions[RolePreferenceCategory.Scientist])}, " +
                    $"ClassD {FormatVector(zonePositions[RolePreferenceCategory.ClassD])}, " +
                    $"Guard {FormatVector(zonePositions[RolePreferenceCategory.FacilityGuard])}.");
            }
            catch (Exception exception)
            {
                Log.Error($"[Role Preferences] Failed to create one or more tower markers:\n{exception}");
                DestroyMarkers();
            }
        }

        private Vector3 CalculateDynamicZonePositions(Vector3 spawn)
        {
            float floorY = spawn.y;
            Vector3 floorRayOrigin = spawn + Vector3.right + (Vector3.up * 2f);
            if (Physics.Raycast(floorRayOrigin, Vector3.down, out RaycastHit floorHit, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                floorY = floorHit.point.y + 0.035f;

            Vector3 rayOrigin = new Vector3(spawn.x, floorY + 0.6f, spawn.z);
            float right = 0;
            float left = 0;
            float forward = 0;
            float back = 0;
            bool bounded = TryGetWallDistance(rayOrigin, Vector3.right, out right);
            bounded &= TryGetWallDistance(rayOrigin, Vector3.left, out left);
            bounded &= TryGetWallDistance(rayOrigin, Vector3.forward, out forward);
            bounded &= TryGetWallDistance(rayOrigin, Vector3.back, out back);

            float minX;
            float maxX;
            float minZ;
            float maxZ;
            if (bounded && right + left >= 7f && forward + back >= 7f)
            {
                minX = spawn.x - left;
                maxX = spawn.x + right;
                minZ = spawn.z - back;
                maxZ = spawn.z + forward;
            }
            else
            {
                minX = spawn.x - 5f;
                maxX = spawn.x + 5f;
                minZ = spawn.z - 4f;
                maxZ = spawn.z + 4f;
                Log.Warn("[Role Preferences] Could not measure every tower wall; zones will use offsets around the native Tutorial spawn.");
            }

            float roomWidth = maxX - minX;
            float roomDepth = maxZ - minZ;
            float wallGap = Math.Max(0.05f, Math.Min(settings.DynamicZoneWallGap, 0.75f));
            float centerGap = Math.Max(0.2f, Math.Min(settings.DynamicZoneCenterGap, 2f));
            float availableWidth = Math.Max(2f, (roomWidth - (wallGap * 2f) - centerGap) / 2f);
            float availableDepth = Math.Max(2f, (roomDepth - (wallGap * 2f) - centerGap) / 2f);
            float sizeScale = Math.Max(0.5f, Math.Min(settings.DynamicZoneSizeScale, 1f));
            float zoneSize = Math.Min(availableWidth, availableDepth) * sizeScale;
            runtimeZoneHalfSize = zoneSize / 2f;

            float lowX = minX + wallGap + runtimeZoneHalfSize;
            float highX = maxX - wallGap - runtimeZoneHalfSize;
            float lowZ = minZ + wallGap + runtimeZoneHalfSize;
            float highZ = maxZ - wallGap - runtimeZoneHalfSize;

            zonePositions[RolePreferenceCategory.Scp] = new Vector3(lowX, floorY, highZ);
            zonePositions[RolePreferenceCategory.Scientist] = new Vector3(highX, floorY, highZ);
            zonePositions[RolePreferenceCategory.ClassD] = new Vector3(lowX, floorY, lowZ);
            zonePositions[RolePreferenceCategory.FacilityGuard] = new Vector3(highX, floorY, lowZ);
            return new Vector3((minX + maxX) / 2f, floorY, (minZ + maxZ) / 2f);
        }

        private Vector3 LoadConfiguredZonePositions()
        {
            runtimeZoneHalfSize = Math.Max(0.5f, settings.ZoneRadius);
            zonePositions[RolePreferenceCategory.Scp] = ToVector(settings.ScpZone);
            zonePositions[RolePreferenceCategory.Scientist] = ToVector(settings.ScientistZone);
            zonePositions[RolePreferenceCategory.ClassD] = ToVector(settings.ClassDZone);
            zonePositions[RolePreferenceCategory.FacilityGuard] = ToVector(settings.FacilityGuardZone);
            return ToVector(settings.Center);
        }

        private static bool TryGetWallDistance(Vector3 origin, Vector3 direction, out float distance)
        {
            Vector3 perpendicular = Math.Abs(direction.x) > 0.5f ? Vector3.forward : Vector3.right;
            float[] offsets = { -1f, -0.5f, 0f, 0.5f, 1f };
            List<float> samples = new List<float>(offsets.Length);
            foreach (float offset in offsets)
            {
                Vector3 sampleOrigin = origin + (perpendicular * offset);
                if (Physics.Raycast(sampleOrigin, direction, out RaycastHit hit, 25f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && hit.distance >= 1f)
                    samples.Add(hit.distance);
            }

            if (samples.Count == 0)
            {
                distance = 0;
                return false;
            }

            samples.Sort();
            distance = samples[samples.Count / 2];
            return true;
        }

        private void CreateZone(RolePreferenceCategory category, Vector3 position, Color color)
        {
            float halfSize = Math.Max(0.75f, runtimeZoneHalfSize > 0 ? runtimeZoneHalfSize : settings.ZoneRadius);
            float size = halfSize * 2f;
            const float borderWidth = 0.1f;
            Color mutedFill = Color.Lerp(color, Color.black, 0.48f);
            mutedFill.a = 0.42f;

            float borderY = position.y + 0.018f;
            float outerSize = size + (borderWidth * 2f);
            float borderOffset = halfSize + (borderWidth / 2f);
            CreateBorderBar(new Vector3(position.x, borderY, position.z + borderOffset), new Vector3(outerSize, 0.04f, borderWidth), color);
            CreateBorderBar(new Vector3(position.x, borderY, position.z - borderOffset), new Vector3(outerSize, 0.04f, borderWidth), color);
            CreateBorderBar(new Vector3(position.x + borderOffset, borderY, position.z), new Vector3(borderWidth, 0.04f, size), color);
            CreateBorderBar(new Vector3(position.x - borderOffset, borderY, position.z), new Vector3(borderWidth, 0.04f, size), color);

            ExiledPrimitive pad = ExiledPrimitive.Create(
                PrimitiveType.Cube,
                position + new Vector3(0f, 0.025f, 0f),
                Vector3.zero,
                new Vector3(size, 0.025f, size),
                true,
                mutedFill);
            pad.Collidable = false;
            pad.Visible = true;
            toys.Add(pad);

            ExiledText label = ExiledText.Create(
                position + new Vector3(0f, 0.065f, 0f),
                Quaternion.Euler(90f, 0f, 0f),
                new Vector3(0.18f, 0.18f, 0.18f),
                $"<color={GetHexColor(category)}><b>{GetDisplayName(category).ToUpperInvariant()}</b></color>",
                new Vector2(160f, 38f),
                null,
                true);
            toys.Add(label);
        }

        private void CreateBorderBar(Vector3 position, Vector3 scale, Color color)
        {
            ExiledPrimitive bar = ExiledPrimitive.Create(PrimitiveType.Cube, position, Vector3.zero, scale, true, color);
            bar.Collidable = false;
            bar.Visible = true;
            toys.Add(bar);
        }

        private void DestroyMarkers()
        {
            foreach (ExiledToy toy in toys.ToList())
            {
                try
                {
                    toy?.Destroy();
                }
                catch (Exception exception)
                {
                    Log.Debug($"[Role Preferences] Could not destroy a tower marker: {exception.Message}");
                }
            }

            toys.Clear();
            zonePositions.Clear();
            markersCreated = false;
            runtimeZoneHalfSize = 0;
            randomSelectionCenterSet = false;
        }

        private void HideStartRoundScreen()
        {
            GameObject startRound = GameObject.Find("StartRound");
            if (startRound is null && RoundStart.singleton is not null)
                startRound = RoundStart.singleton.gameObject;

            if (startRound is null)
            {
                Log.Warn("[Role Preferences] Could not find the StartRound screen; the native lobby overlay may remain visible over the tower.");
                return;
            }

            startRoundTransform = startRound.transform;
            startRoundOriginalScale = startRoundTransform.localScale;
            startRoundTransform.localScale = Vector3.zero;
            startRoundScreenHidden = true;
            Log.Debug("[Role Preferences] Native StartRound lobby screen has been hidden for the tower lobby.");
        }

        private void RestoreStartRoundScreen()
        {
            if (!startRoundScreenHidden)
                return;

            if (startRoundTransform is not null)
                startRoundTransform.localScale = startRoundOriginalScale;

            startRoundTransform = null;
            startRoundScreenHidden = false;
        }

        private void StopLoop()
        {
            if (!loopRunning)
                return;

            Timing.KillCoroutines(loop);
            loopRunning = false;
        }

        private static bool IsInsideSquare(Vector3 position, Vector3 center, float halfSize) =>
            Math.Abs(position.x - center.x) <= halfSize && Math.Abs(position.z - center.z) <= halfSize;

        private static float HorizontalDistanceSquared(Vector3 first, Vector3 second)
        {
            float dx = first.x - second.x;
            float dz = first.z - second.z;
            return (dx * dx) + (dz * dz);
        }

        private static bool IsInConfiguredGroup(Player player, IEnumerable<string> groups)
        {
            string group = player?.Group?.GetKey();
            return !string.IsNullOrWhiteSpace(group) &&
                   groups?.Any(candidate => string.Equals(candidate?.Trim(), group, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static Vector3 ToVector(RolePreferencePoint point) =>
            point is null ? Vector3.zero : new Vector3(point.X, point.Y, point.Z);

        private static string FormatVector(Vector3 value) => $"({value.x:0.00}, {value.y:0.00}, {value.z:0.00})";

        private string GetDisplayName(RolePreferenceCategory category)
        {
            switch (category)
            {
                case RolePreferenceCategory.Scp: return settings.ScpRoleName ?? string.Empty;
                case RolePreferenceCategory.Scientist: return settings.ScientistRoleName ?? string.Empty;
                case RolePreferenceCategory.ClassD: return settings.ClassDRoleName ?? string.Empty;
                case RolePreferenceCategory.FacilityGuard: return settings.FacilityGuardRoleName ?? string.Empty;
                default: return settings.RandomRoleName ?? string.Empty;
            }
        }

        private static string GetHexColor(RolePreferenceCategory category)
        {
            switch (category)
            {
                case RolePreferenceCategory.Scp: return "#FF3B3B";
                case RolePreferenceCategory.Scientist: return "#FFE531";
                case RolePreferenceCategory.ClassD: return "#FF7A18";
                case RolePreferenceCategory.FacilityGuard: return "#8A9099";
                default: return "#B8B8B8";
            }
        }

        private sealed class ParticipantState
        {
            public float LastRoleSetAt { get; set; } = -10f;

            public float NativeSpawnReadyAt { get; set; }
        }
    }
}
