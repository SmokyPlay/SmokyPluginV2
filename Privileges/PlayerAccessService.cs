namespace SmokyPluginV2.Privileges
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    using Exiled.API.Extensions;
    using Exiled.API.Features;
    using Exiled.Events.EventArgs.Player;

    using SmokyPluginV2.Database;
    using SmokyPluginV2.Discord;

    using PlayerEvents = Exiled.Events.Handlers.Player;

    internal sealed class PlayerAccessService : IDisposable
    {
        private readonly PlayerPrivilegeService privileges;
        private readonly DiscordRoleSynchronizationService discordRoles;
        private readonly ConcurrentDictionary<string, string> synchronizedGroups =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> synchronizationVersions =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, object> unlinkTokens =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<ulong, object> unlinkDiscordUsers =
            new ConcurrentDictionary<ulong, object>();
        private readonly ConcurrentDictionary<string, string[]> resolvedPrivilegeGroups =
            new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string[]> resolvedDiscordGroups =
            new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, double> temporaryRolePreferenceWeights =
            new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private DiscordSettings discordSettings;
        private bool disposed;

        public PlayerAccessService(
            PlayerPrivilegeService privileges,
            DiscordRoleSynchronizationService discordRoles,
            DiscordSettings discordSettings)
        {
            this.privileges = privileges ?? throw new ArgumentNullException(nameof(privileges));
            this.discordRoles = discordRoles;
            this.discordSettings = discordSettings ?? new DiscordSettings();
        }

        public void Register()
        {
            PlayerEvents.Verified += OnVerified;
            PlayerEvents.Left += OnLeft;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            PlayerEvents.Verified -= OnVerified;
            PlayerEvents.Left -= OnLeft;
            foreach (Player player in Player.List.ToList())
                RemoveSynchronizedGroup(player);
            synchronizedGroups.Clear();
            synchronizationVersions.Clear();
            unlinkTokens.Clear();
            unlinkDiscordUsers.Clear();
            resolvedPrivilegeGroups.Clear();
            resolvedDiscordGroups.Clear();
            temporaryRolePreferenceWeights.Clear();
        }

        public void ReloadSettings(EarnedPrivilegeSettings reloadedEarnedSettings, DiscordSettings reloadedDiscordSettings)
        {
            privileges.ReloadSettings(reloadedEarnedSettings);
            discordSettings = reloadedDiscordSettings ?? new DiscordSettings();
            RefreshOnlinePlayers();
        }

        public void Synchronize(Player player, bool notifyPlayer = false)
        {
            if (disposed || !IsRealPlayer(player))
                return;

            SynchronizeBySteamId(player.UserId, notifyPlayer);
        }

        public void SynchronizeBySteamId(string playerUserId, bool notifyPlayer = false)
        {
            if (disposed || string.IsNullOrWhiteSpace(playerUserId) ||
                !PostgreSqlService.IsSteamUserId(playerUserId))
            {
                return;
            }

            int version = synchronizationVersions.AddOrUpdate(playerUserId, 1, (_, current) => unchecked(current + 1));
            unlinkTokens.TryGetValue(playerUserId, out object pendingUnlinkToken);
            DiscordSettings currentDiscordSettings = discordSettings ?? new DiscordSettings();

            Task.Run(async () =>
            {
                AccessResolutionResult resolution = await ResolveBySteamIdAsync(
                    playerUserId,
                    currentDiscordSettings).ConfigureAwait(false);
                if (!resolution.IsSuccess)
                {
                    NotifyFailure(playerUserId, version, notifyPlayer, resolution.Error);
                    return;
                }

                await SynchronizeResolvedSnapshotAsync(
                    playerUserId,
                    resolution.Snapshot,
                    resolution.DiscordMember,
                    currentDiscordSettings,
                    version,
                    pendingUnlinkToken,
                    notifyPlayer,
                    applyGameAccess: true,
                    shouldContinue: () => IsCurrent(playerUserId, version)).ConfigureAwait(false);
            });
        }

        public void SynchronizeByDiscordId(ulong discordUserId, bool notifyPlayer = false)
        {
            if (disposed || discordUserId == 0)
                return;

            DiscordSettings currentDiscordSettings = discordSettings ?? new DiscordSettings();
            Task.Run(async () =>
            {
                AccessResolutionResult resolution = await ResolveByDiscordIdAsync(
                    discordUserId,
                    null).ConfigureAwait(false);
                if (!resolution.IsSuccess)
                {
                    Log.Error(
                        $"[PlayerAccess] Synchronization by Discord ID {discordUserId} failed: " +
                        $"{resolution.Error ?? "unknown database error"}");
                    return;
                }

                PlayerAccessSnapshot snapshot = resolution.Snapshot;
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.PlayerUserId))
                    return;

                string playerUserId = snapshot.PlayerUserId;
                int version = synchronizationVersions.AddOrUpdate(
                    playerUserId,
                    1,
                    (_, current) => unchecked(current + 1));
                unlinkTokens.TryGetValue(playerUserId, out object pendingUnlinkToken);
                await SynchronizeResolvedSnapshotAsync(
                    playerUserId,
                    snapshot,
                    resolution.DiscordMember,
                    currentDiscordSettings,
                    version,
                    pendingUnlinkToken,
                    notifyPlayer,
                    applyGameAccess: true,
                    shouldContinue: () => IsCurrent(playerUserId, version)).ConfigureAwait(false);
            });
        }

        public Task<DiscordRoleSynchronizationResult> SynchronizeDiscordRolesByDiscordIdAsync(
            ulong discordUserId,
            DiscordGuildMemberResult knownMember = null)
        {
            if (disposed || discordUserId == 0)
            {
                return Task.FromResult(new DiscordRoleSynchronizationResult
                {
                    IsCanceled = true,
                    Error = "Discord role synchronization is unavailable.",
                });
            }

            if (unlinkDiscordUsers.ContainsKey(discordUserId))
            {
                return Task.FromResult(new DiscordRoleSynchronizationResult
                {
                    IsCanceled = true,
                    Error = "Discord account unlink cleanup is in progress.",
                });
            }

            DiscordSettings currentDiscordSettings = discordSettings ?? new DiscordSettings();
            return Task.Run(async () =>
            {
                AccessResolutionResult resolution = await ResolveByDiscordIdAsync(
                    discordUserId,
                    knownMember).ConfigureAwait(false);
                if (!resolution.IsSuccess)
                {
                    Log.Error(
                        $"[PlayerAccess] Discord-only synchronization for {discordUserId} failed: " +
                        $"{resolution.Error ?? "unknown database error"}");
                    return new DiscordRoleSynchronizationResult
                    {
                        Error = resolution.Error ?? "Database privilege resolution failed.",
                    };
                }

                PlayerAccessSnapshot snapshot = resolution.Snapshot;
                if (disposed || snapshot == null)
                {
                    return new DiscordRoleSynchronizationResult
                    {
                        IsCanceled = disposed,
                        Error = snapshot == null ? "Database privilege resolution returned no snapshot." : null,
                    };
                }

                DiscordRoleSynchronizationResult result = await SynchronizeResolvedSnapshotAsync(
                    snapshot.PlayerUserId,
                    snapshot,
                    resolution.DiscordMember,
                    currentDiscordSettings,
                    0,
                    null,
                    notifyPlayer: false,
                    applyGameAccess: false,
                    shouldContinue: () => !disposed && !unlinkDiscordUsers.ContainsKey(discordUserId)).ConfigureAwait(false);

                if (result == null || !result.IsSuccess)
                {
                    Log.Error(
                        $"[PlayerAccess] Discord-only role synchronization for {discordUserId} failed: " +
                        $"{result?.Error ?? "unknown Discord error"}");
                }

                return result ?? new DiscordRoleSynchronizationResult
                {
                    Error = "Discord role synchronization returned no result.",
                };
            });
        }

        public void SynchronizeAfterUnlink(
            string playerUserId,
            ulong previousDiscordUserId,
            bool notifyPlayer = false)
        {
            if (disposed ||
                string.IsNullOrWhiteSpace(playerUserId) ||
                previousDiscordUserId == 0)
            {
                return;
            }

            int version = synchronizationVersions.AddOrUpdate(
                playerUserId,
                1,
                (_, current) => unchecked(current + 1));
            object unlinkToken = new object();
            unlinkTokens[playerUserId] = unlinkToken;
            unlinkDiscordUsers[previousDiscordUserId] = unlinkToken;
            DiscordSettings currentDiscordSettings = discordSettings ?? new DiscordSettings();

            Task.Run(async () =>
            {
                try
                {
                    Task<AccessResolutionResult> steamResolutionTask =
                        ResolveBySteamIdAsync(playerUserId, currentDiscordSettings);
                    Task<AccessResolutionResult> discordResolutionTask = ResolveIdentityAsync(
                        new PlayerAccessIdentity
                        {
                            DiscordUserId = previousDiscordUserId,
                        },
                        true,
                        null);

                    AccessResolutionResult steamResolution =
                        await steamResolutionTask.ConfigureAwait(false);
                    if (!steamResolution.IsSuccess)
                    {
                        Log.Error(
                            $"[PlayerAccess] Steam privilege resolution after unlink failed for " +
                            $"{playerUserId}: {steamResolution.Error ?? "unknown database error"}");
                    }
                    else if (IsCurrent(playerUserId, version))
                    {
                        ApplyResolvedAccess(
                            playerUserId,
                            steamResolution.Snapshot,
                            null,
                            currentDiscordSettings,
                            version,
                            notifyPlayer);
                    }

                    AccessResolutionResult discordResolution =
                        await discordResolutionTask.ConfigureAwait(false);
                    if (!discordResolution.IsSuccess)
                    {
                        Log.Error(
                            $"[PlayerAccess] Discord privilege resolution after unlink failed for " +
                            $"{previousDiscordUserId}: {discordResolution.Error ?? "unknown database error"}");
                    }

                    PlayerAccessSnapshot discordSnapshot = discordResolution.Snapshot;
                    if (discordResolution.IsSuccess &&
                        discordSnapshot != null &&
                        IsUnlinkCurrent(playerUserId, unlinkToken) &&
                        discordRoles != null &&
                        currentDiscordSettings.AccountLinking?.IsEnabled == true)
                    {
                        DiscordRoleSynchronizationResult unlinkDiscordResult =
                            discordRoles.Synchronize(
                                previousDiscordUserId,
                                discordResolution.DiscordMember,
                                discordSnapshot.DiscordPrivilegeGroups,
                                steamResolution.IsSuccess && steamResolution.Snapshot != null
                                    ? steamResolution.Snapshot.SteamPrivilegeGroups
                                        .Union(
                                            steamResolution.Snapshot.ManagedDiscordGroups ?? Array.Empty<string>(),
                                            StringComparer.OrdinalIgnoreCase)
                                        .Union(
                                            discordSnapshot.ManagedDiscordGroups ?? Array.Empty<string>(),
                                            StringComparer.OrdinalIgnoreCase)
                                        .ToArray()
                                    : discordSnapshot.ManagedDiscordGroups,
                                GetValidMappings(currentDiscordSettings),
                                false,
                                currentDiscordSettings.AccountLinking?.LinkedDiscordRoleId ?? 0,
                                () => IsUnlinkCurrent(playerUserId, unlinkToken));
                        await FinalizeReconciliationAsync(
                            CombineRevocations(
                                steamResolution.Snapshot,
                                discordSnapshot),
                            unlinkDiscordResult,
                            currentDiscordSettings).ConfigureAwait(false);
                    }
                }
                finally
                {
                    RemoveUnlinkToken(playerUserId, unlinkToken);
                    RemoveUnlinkDiscordUser(previousDiscordUserId, unlinkToken);
                }
            });
        }

        public void ApplyResolvedAccess(
            string playerUserId,
            PlayerAccessSnapshot snapshot,
            DiscordRoleSynchronizationResult discordResult,
            bool notifyPlayer = false)
        {
            if (disposed || string.IsNullOrWhiteSpace(playerUserId) || snapshot == null)
                return;
            if (discordResult?.IsCanceled == true)
                return;

            int version = synchronizationVersions.AddOrUpdate(
                playerUserId,
                1,
                (_, current) => unchecked(current + 1));
            ApplyResolvedAccess(
                playerUserId,
                snapshot,
                discordResult,
                discordSettings ?? new DiscordSettings(),
                version,
                notifyPlayer);
        }

        public void RefreshOnlinePlayers(bool remoteAdminReloaded = false)
        {
            if (remoteAdminReloaded)
                synchronizedGroups.Clear();

            foreach (Player player in Player.List.ToList())
            {
                if (IsRealPlayer(player))
                    Synchronize(player);
            }
        }

        internal IReadOnlyCollection<string> GetResolvedGroups(string playerUserId)
        {
            if (string.IsNullOrWhiteSpace(playerUserId))
                return Array.Empty<string>();

            HashSet<string> groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resolvedPrivilegeGroups.TryGetValue(playerUserId, out string[] privilegeGroups))
                groups.UnionWith(privilegeGroups ?? Array.Empty<string>());
            if (resolvedDiscordGroups.TryGetValue(playerUserId, out string[] discordGroups))
                groups.UnionWith(discordGroups ?? Array.Empty<string>());
            return groups.ToArray();
        }

        internal double? GetTemporaryRolePreferenceWeight(string playerUserId)
        {
            if (string.IsNullOrWhiteSpace(playerUserId) ||
                !temporaryRolePreferenceWeights.TryGetValue(playerUserId, out double weight))
            {
                return null;
            }

            return weight;
        }

        public void RemoveSynchronizedGroup(Player player)
        {
            string playerUserId = player?.UserId;
            if (string.IsNullOrWhiteSpace(playerUserId) ||
                !synchronizedGroups.TryRemove(playerUserId, out string assignedGroup))
            {
                return;
            }

            string currentGroup = player.Group?.GetKey();
            if (!string.Equals(currentGroup, assignedGroup, StringComparison.OrdinalIgnoreCase))
                return;

            player.Group = null;
        }

        public void Forget(Player player)
        {
            string playerUserId = player?.UserId;
            if (string.IsNullOrWhiteSpace(playerUserId))
                return;

            synchronizedGroups.TryRemove(playerUserId, out _);
            synchronizationVersions.TryRemove(playerUserId, out _);
            resolvedPrivilegeGroups.TryRemove(playerUserId, out _);
            resolvedDiscordGroups.TryRemove(playerUserId, out _);
            temporaryRolePreferenceWeights.TryRemove(playerUserId, out _);
        }

        private void OnVerified(VerifiedEventArgs ev) => Synchronize(ev.Player);

        private void OnLeft(LeftEventArgs ev) => Forget(ev.Player);

        private async Task<AccessResolutionResult> ResolveBySteamIdAsync(
            string playerUserId,
            DiscordSettings currentDiscordSettings)
        {
            if (!privileges.TryResolveIdentityBySteamId(
                    playerUserId,
                    out PlayerAccessIdentity identity,
                    out string identityError))
            {
                return AccessResolutionResult.Failed(identityError);
            }

            return await ResolveIdentityAsync(
                identity,
                currentDiscordSettings?.AccountLinking?.IsEnabled == true,
                null).ConfigureAwait(false);
        }

        private async Task<AccessResolutionResult> ResolveByDiscordIdAsync(
            ulong discordUserId,
            DiscordGuildMemberResult knownMember)
        {
            if (!privileges.TryResolveIdentityByDiscordId(
                    discordUserId,
                    out PlayerAccessIdentity identity,
                    out string identityError))
            {
                return AccessResolutionResult.Failed(identityError);
            }

            return await ResolveIdentityAsync(
                identity,
                true,
                knownMember).ConfigureAwait(false);
        }

        private async Task<AccessResolutionResult> ResolveIdentityAsync(
            PlayerAccessIdentity identity,
            bool lookupDiscordMember,
            DiscordGuildMemberResult knownMember)
        {
            Task<PrivilegeResolutionResult> privilegeTask = Task.Run(() =>
            {
                bool success = privileges.TryResolve(
                    identity,
                    out PlayerAccessSnapshot snapshot,
                    out string error);
                return new PrivilegeResolutionResult
                {
                    IsSuccess = success,
                    Snapshot = snapshot,
                    Error = error,
                };
            });

            Task<DiscordGuildMemberResult> memberTask = null;
            if (identity?.DiscordUserId != 0 &&
                discordRoles != null &&
                lookupDiscordMember)
            {
                memberTask = knownMember != null
                    ? Task.FromResult(knownMember)
                    : discordRoles.GetGuildMemberAsync(identity.DiscordUserId);
            }

            PrivilegeResolutionResult privilegeResolution =
                await privilegeTask.ConfigureAwait(false);
            DiscordGuildMemberResult member = memberTask != null
                ? await memberTask.ConfigureAwait(false)
                : null;
            return new AccessResolutionResult
            {
                IsSuccess = privilegeResolution.IsSuccess,
                Snapshot = privilegeResolution.Snapshot,
                DiscordMember = member,
                Error = privilegeResolution.Error,
            };
        }

        private async Task FinalizeReconciliationAsync(
            PlayerAccessSnapshot snapshot,
            DiscordRoleSynchronizationResult synchronization,
            DiscordSettings currentDiscordSettings)
        {
            if (synchronization?.ReconciliationTask == null)
                return;

            try
            {
                DiscordRoleReconciliationReport report =
                    await synchronization.ReconciliationTask.ConfigureAwait(false);
                if (report == null ||
                    report.IsCanceled ||
                    snapshot?.PendingRevocations == null ||
                    snapshot.PendingRevocations.Count == 0)
                {
                    return;
                }

                List<DiscordRoleGroupMapping> mappings = GetValidMappings(currentDiscordSettings);
                List<PendingPrivilegeRevocation> settled = snapshot.PendingRevocations
                    .Where(revocation =>
                    {
                        DiscordRoleGroupMapping mapping = mappings.FirstOrDefault(candidate =>
                            string.Equals(
                                candidate.RemoteAdminGroup?.Trim(),
                                revocation?.GroupName?.Trim(),
                                StringComparison.OrdinalIgnoreCase));
                        return mapping != null && report.IsRemovalSettled(mapping.DiscordRoleId);
                    })
                    .ToList();
                if (settled.Count == 0)
                    return;

                if (!privileges.TryFinalizeRevocations(settled, out string error))
                {
                    Log.Error(
                        $"[PlayerAccess] Could not finalize {settled.Count} settled privilege " +
                        $"revocation(s) in PostgreSQL: {error ?? "unknown database error"}");
                }
            }
            catch (Exception exception)
            {
                Log.Error($"[PlayerAccess] Privilege revocation finalization failed: {exception}");
            }
        }

        private static PlayerAccessSnapshot CombineRevocations(
            params PlayerAccessSnapshot[] snapshots) =>
            new PlayerAccessSnapshot
            {
                PendingRevocations = (snapshots ?? Array.Empty<PlayerAccessSnapshot>())
                    .Where(snapshot => snapshot?.PendingRevocations != null)
                    .SelectMany(snapshot => snapshot.PendingRevocations)
                    .ToArray(),
            };

        private async Task<DiscordRoleSynchronizationResult> SynchronizeResolvedSnapshotAsync(
            string playerUserId,
            PlayerAccessSnapshot snapshot,
            DiscordGuildMemberResult discordMember,
            DiscordSettings currentDiscordSettings,
            int version,
            object pendingUnlinkToken,
            bool notifyPlayer,
            bool applyGameAccess,
            Func<bool> shouldContinue)
        {
            if (snapshot == null)
            {
                return new DiscordRoleSynchronizationResult
                {
                    Error = "Privilege resolution returned no snapshot.",
                };
            }

            List<DiscordRoleGroupMapping> mappings = GetValidMappings(currentDiscordSettings);
            bool accountLinkingEnabled =
                currentDiscordSettings.AccountLinking?.IsEnabled == true;
            bool shouldSynchronizeDiscord = snapshot.DiscordUserId != 0 &&
                (!applyGameAccess || accountLinkingEnabled);
            if (shouldSynchronizeDiscord && pendingUnlinkToken != null)
                RemoveUnlinkToken(playerUserId, pendingUnlinkToken);

            DiscordRoleSynchronizationResult discordResult = null;
            if (shouldSynchronizeDiscord && discordRoles != null)
            {
                discordResult = discordRoles.Synchronize(
                    snapshot.DiscordUserId,
                    discordMember,
                    snapshot.PrivilegeGroups,
                    snapshot.ManagedDiscordGroups,
                    mappings,
                    accountLinkingEnabled && !string.IsNullOrWhiteSpace(snapshot.PlayerUserId),
                    accountLinkingEnabled
                        ? currentDiscordSettings.AccountLinking?.LinkedDiscordRoleId ?? 0
                        : 0,
                    shouldContinue);
            }
            else if (shouldSynchronizeDiscord && !applyGameAccess)
            {
                return new DiscordRoleSynchronizationResult
                {
                    Error = "Discord role service is unavailable.",
                };
            }

            if (discordResult?.IsCanceled == true || shouldContinue?.Invoke() == false)
            {
                return discordResult ?? new DiscordRoleSynchronizationResult
                {
                    IsCanceled = true,
                };
            }

            if (applyGameAccess)
            {
                ApplyResolvedAccess(
                    playerUserId,
                    snapshot,
                    discordResult,
                    currentDiscordSettings,
                    version,
                    notifyPlayer);
            }

            await FinalizeReconciliationAsync(
                snapshot,
                discordResult,
                currentDiscordSettings).ConfigureAwait(false);
            return discordResult ?? new DiscordRoleSynchronizationResult
            {
                IsSuccess = !shouldSynchronizeDiscord,
            };
        }

        private void ApplyResolvedAccess(
            string playerUserId,
            PlayerAccessSnapshot snapshot,
            DiscordRoleSynchronizationResult discordResult,
            DiscordSettings resolvedDiscordSettings,
            int version,
            bool notifyPlayer)
        {
            MainThreadDispatcher.Dispatch(
                () => ApplyResolvedAccessOnMainThread(
                    playerUserId,
                    snapshot,
                    discordResult,
                    resolvedDiscordSettings,
                    version,
                    notifyPlayer),
                MainThreadDispatcher.DispatchTime.FixedUpdate);
        }

        private void ApplyResolvedAccessOnMainThread(
            string playerUserId,
            PlayerAccessSnapshot snapshot,
            DiscordRoleSynchronizationResult discordResult,
            DiscordSettings resolvedDiscordSettings,
            int version,
            bool notifyPlayer)
        {
            if (!IsCurrent(playerUserId, version))
                return;

            Player player = Player.Get(playerUserId);
            if (!IsRealPlayer(player))
                return;

            DiscordSettings settings = resolvedDiscordSettings ?? new DiscordSettings();
            List<DiscordRoleGroupMapping> mappings = GetValidMappings(settings);
            HashSet<string> privilegeGroups = new HashSet<string>(
                snapshot.PrivilegeGroups ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            bool preserveNative = settings.AccountLinking?.PreserveNativeGroup != false;
            bool hasNativeGroup = preserveNative &&
                Server.PermissionsHandler?.Members.ContainsKey(playerUserId) == true;
            bool hasDiscordLink = snapshot.DiscordUserId != 0 &&
                settings.AccountLinking?.IsEnabled == true;
            bool discordLookupFailed = hasDiscordLink &&
                (discordResult == null || !discordResult.IsSuccess);
            IEnumerable<ulong> effectiveDiscordRoleIds =
                discordResult?.IsSuccess == true && discordResult.IsGuildMember
                    ? discordResult.EffectiveRoleIds
                    : Array.Empty<ulong>();
            UpdateResolvedGroups(
                playerUserId,
                privilegeGroups,
                hasDiscordLink,
                discordLookupFailed,
                effectiveDiscordRoleIds,
                mappings);
            if (snapshot.TemporaryRolePreferenceWeight.HasValue)
            {
                temporaryRolePreferenceWeights[playerUserId] =
                    snapshot.TemporaryRolePreferenceWeight.Value;
            }
            else
            {
                temporaryRolePreferenceWeights.TryRemove(playerUserId, out _);
            }
            DiscordRoleGroupMapping selected = null;
            if (!hasNativeGroup && !discordLookupFailed)
                selected = SelectHighestMapping(effectiveDiscordRoleIds, privilegeGroups, mappings);

            if (discordResult?.IsSuccess == true && !discordResult.IsGuildMember)
                Log.Warn($"[PlayerAccess] Discord user {snapshot.DiscordUserId} linked to {playerUserId} is not a guild member.");

            ApplyGameGroup(
                playerUserId,
                version,
                hasNativeGroup,
                discordLookupFailed,
                selected,
                notifyPlayer);
        }

        private void ApplyGameGroup(
            string playerUserId,
            int version,
            bool hasNativeGroup,
            bool discordLookupFailed,
            DiscordRoleGroupMapping selected,
            bool notifyPlayer)
        {
            if (!IsCurrent(playerUserId, version))
                return;

            Player player = Player.Get(playerUserId);
            if (!IsRealPlayer(player))
                return;

            if (hasNativeGroup)
            {
                synchronizedGroups.TryRemove(playerUserId, out _);
                if (notifyPlayer)
                    player.SendConsoleMessage("Нативная группа Remote Admin сохранена. Заработанные Discord-роли синхронизируются.", "green");
                return;
            }

            if (discordLookupFailed)
            {
                Log.Error($"[PlayerAccess] Discord role lookup failed for {playerUserId}; the current game group was preserved.");
                if (notifyPlayer)
                    player.SendConsoleMessage("Не удалось получить роли Discord. Текущая игровая группа не изменена.", "red");
                return;
            }

            if (selected == null)
            {
                synchronizedGroups.TryRemove(playerUserId, out _);
                if (player.Group != null)
                {
                    player.Group = null;
                    Log.Info($"[PlayerAccess] Cleared non-native RA group for {playerUserId}; no database or Discord privilege applies.");
                }

                if (notifyPlayer)
                    player.SendConsoleMessage("Подходящие привилегии в PostgreSQL или Discord не найдены.", "yellow");
                return;
            }

            string groupName = selected.RemoteAdminGroup.Trim();
            if (Server.PermissionsHandler == null ||
                !Server.PermissionsHandler.Groups.TryGetValue(groupName, out UserGroup group))
            {
                Log.Error($"[PlayerAccess] Remote Admin group '{groupName}' is not defined.");
                if (notifyPlayer)
                    player.SendConsoleMessage($"Группа Remote Admin '{groupName}' не настроена на сервере.", "red");
                return;
            }

            synchronizedGroups[playerUserId] = groupName;
            string currentGroupName = player.Group?.GetKey();
            if (!string.Equals(currentGroupName, groupName, StringComparison.OrdinalIgnoreCase))
            {
                player.Group = group;
                Log.Info($"[PlayerAccess] Assigned temporary RA group '{groupName}' to {playerUserId}.");
            }

            if (notifyPlayer)
                player.SendConsoleMessage($"Привилегии синхронизированы. Игровая группа: {groupName}.", "green");
        }

        private static DiscordRoleGroupMapping SelectHighestMapping(
            IEnumerable<ulong> discordRoleIds,
            HashSet<string> privilegeGroups,
            IEnumerable<DiscordRoleGroupMapping> mappings)
        {
            HashSet<ulong> roles = new HashSet<ulong>(discordRoleIds ?? Array.Empty<ulong>());
            return mappings.FirstOrDefault(mapping =>
                roles.Contains(mapping.DiscordRoleId) ||
                privilegeGroups.Contains(mapping.RemoteAdminGroup.Trim()));
        }

        private void NotifyFailure(string playerUserId, int version, bool notifyPlayer, string error)
        {
            Log.Error($"[PlayerAccess] Synchronization failed for {playerUserId}: {error ?? "unknown database error"}");
            if (!notifyPlayer)
                return;

            MainThreadDispatcher.Dispatch(
                () =>
                {
                    if (!IsCurrent(playerUserId, version))
                        return;
                    Player.Get(playerUserId)?.SendConsoleMessage(
                        "Не удалось синхронизировать привилегии из PostgreSQL. Повторите команду позже.",
                        "red");
                },
                MainThreadDispatcher.DispatchTime.FixedUpdate);
        }

        private static List<DiscordRoleGroupMapping> GetValidMappings(DiscordSettings settings) =>
            ((settings ?? new DiscordSettings()).RoleGroups ?? new List<DiscordRoleGroupMapping>())
                .Where(mapping =>
                    mapping != null &&
                    mapping.DiscordRoleId != 0 &&
                    !string.IsNullOrWhiteSpace(mapping.RemoteAdminGroup))
                .ToList();

        private void UpdateResolvedGroups(
            string playerUserId,
            IEnumerable<string> privilegeGroups,
            bool hasDiscordLink,
            bool discordLookupFailed,
            IEnumerable<ulong> effectiveDiscordRoleIds,
            IEnumerable<DiscordRoleGroupMapping> mappings)
        {
            resolvedPrivilegeGroups[playerUserId] = (privilegeGroups ?? Array.Empty<string>())
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!hasDiscordLink)
            {
                resolvedDiscordGroups.TryRemove(playerUserId, out _);
            }
            else if (!discordLookupFailed)
            {
                HashSet<ulong> roleIds = new HashSet<ulong>(
                    effectiveDiscordRoleIds ?? Array.Empty<ulong>());
                resolvedDiscordGroups[playerUserId] = (mappings ??
                        Array.Empty<DiscordRoleGroupMapping>())
                    .Where(mapping =>
                        mapping != null &&
                        mapping.DiscordRoleId != 0 &&
                        !string.IsNullOrWhiteSpace(mapping.RemoteAdminGroup) &&
                        roleIds.Contains(mapping.DiscordRoleId))
                    .Select(mapping => mapping.RemoteAdminGroup.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            Plugin.Instance?.RolePreferences?.NotifyAccessGroupsChanged();
        }

        private bool IsCurrent(string playerUserId, int version) =>
            !disposed &&
            synchronizationVersions.TryGetValue(playerUserId, out int current) &&
            current == version;

        private bool IsUnlinkCurrent(string playerUserId, object token) =>
            !disposed &&
            token != null &&
            unlinkTokens.TryGetValue(playerUserId, out object current) &&
            ReferenceEquals(current, token);

        private void RemoveUnlinkToken(string playerUserId, object token)
        {
            if (string.IsNullOrWhiteSpace(playerUserId) || token == null)
                return;

            ((ICollection<KeyValuePair<string, object>>)unlinkTokens).Remove(
                new KeyValuePair<string, object>(playerUserId, token));
        }

        private void RemoveUnlinkDiscordUser(ulong discordUserId, object token)
        {
            if (discordUserId == 0 || token == null)
                return;

            ((ICollection<KeyValuePair<ulong, object>>)unlinkDiscordUsers).Remove(
                new KeyValuePair<ulong, object>(discordUserId, token));
        }

        private static bool IsRealPlayer(Player player) =>
            player != null &&
            player.IsConnected &&
            !player.IsHost &&
            !player.IsNPC &&
            PostgreSqlService.IsSteamUserId(player.UserId);

        private sealed class PrivilegeResolutionResult
        {
            public bool IsSuccess { get; set; }

            public PlayerAccessSnapshot Snapshot { get; set; }

            public string Error { get; set; }
        }

        private sealed class AccessResolutionResult
        {
            public bool IsSuccess { get; set; }

            public PlayerAccessSnapshot Snapshot { get; set; }

            public DiscordGuildMemberResult DiscordMember { get; set; }

            public string Error { get; set; }

            public static AccessResolutionResult Failed(string error) =>
                new AccessResolutionResult { Error = error };
        }
    }
}
