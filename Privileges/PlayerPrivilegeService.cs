namespace SmokyPluginV2.Privileges
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using SmokyPluginV2.Database;
    using SmokyPluginV2.Referrals;

    internal sealed class PlayerPrivilegeService
    {
        private readonly PostgreSqlService database;
        private EarnedPrivilegeSettings settings;

        public PlayerPrivilegeService(PostgreSqlService database, EarnedPrivilegeSettings settings)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            this.settings = settings ?? new EarnedPrivilegeSettings();
        }

        public void ReloadSettings(EarnedPrivilegeSettings reloadedSettings)
        {
            settings = reloadedSettings ?? new EarnedPrivilegeSettings();
        }

        public void OnPlaytimePersisted(string playerUserId)
        {
            EarnedPrivilegeSettings current = settings ?? new EarnedPrivilegeSettings();
            if (current.RequiredHours <= 0 ||
                double.IsNaN(current.RequiredHours) ||
                double.IsInfinity(current.RequiredHours) ||
                string.IsNullOrWhiteSpace(current.GroupName))
            {
                return;
            }

            long requiredSeconds = (long)Math.Ceiling(
                Math.Min(current.RequiredHours, long.MaxValue / 3600d) * 3600d);
            if (!database.TryGrantEarnedPlaytimePrivilege(
                    playerUserId,
                    Math.Max(1, requiredSeconds),
                    current.GroupName,
                    out bool inserted,
                    out string error))
            {
                Exiled.API.Features.Log.Error(
                    $"[Privileges] Playtime grant check failed for {playerUserId}: {error}");
                return;
            }

            if (inserted)
                Plugin.Instance?.PlayerAccess?.SynchronizeBySteamId(playerUserId);
        }

        public bool TryResolveBySteamId(
            string playerUserId,
            out PlayerAccessSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!TryResolveIdentityBySteamId(playerUserId, out PlayerAccessIdentity identity, out error))
                return false;

            return TryResolve(identity, out snapshot, out error);
        }

        public bool TryResolveByDiscordId(
            ulong discordUserId,
            out PlayerAccessSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (!TryResolveIdentityByDiscordId(discordUserId, out PlayerAccessIdentity identity, out error))
                return false;

            return TryResolve(identity, out snapshot, out error);
        }

        public bool TryResolveIdentityBySteamId(
            string playerUserId,
            out PlayerAccessIdentity identity,
            out string error)
        {
            identity = null;
            if (!database.TryResolveAccessIdentityBySteamId(
                    playerUserId,
                    out long playerId,
                    out string resolvedPlayerUserId,
                    out ulong discordUserId,
                    out error))
            {
                return false;
            }

            identity = new PlayerAccessIdentity
            {
                PlayerId = playerId,
                PlayerUserId = resolvedPlayerUserId,
                DiscordUserId = discordUserId,
            };
            return true;
        }

        public bool TryResolveIdentityByDiscordId(
            ulong discordUserId,
            out PlayerAccessIdentity identity,
            out string error)
        {
            identity = null;
            if (discordUserId == 0)
            {
                error = "Discord ID не указан.";
                return false;
            }

            if (!database.TryResolveAccessIdentityByDiscordId(
                    discordUserId,
                    out long playerId,
                    out string playerUserId,
                    out error))
            {
                return false;
            }

            identity = new PlayerAccessIdentity
            {
                PlayerId = playerId,
                PlayerUserId = playerUserId,
                DiscordUserId = discordUserId,
            };
            return true;
        }

        public bool TryResolve(
            PlayerAccessIdentity identity,
            out PlayerAccessSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (identity == null)
            {
                error = "Не удалось определить связку аккаунтов.";
                return false;
            }

            EarnedPrivilegeSettings currentSettings = settings ?? new EarnedPrivilegeSettings();
            HashSet<string> steamPrivilegeGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> discordPrivilegeGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> managedDiscordGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<PendingPrivilegeRevocation> pendingRevocations = new List<PendingPrivilegeRevocation>();
            if (!TryResolveSteamPrivileges(
                    identity,
                    currentSettings,
                    steamPrivilegeGroups,
                    managedDiscordGroups,
                    pendingRevocations,
                    out long totalPlaytimeSeconds,
                    out double? temporaryRolePreferenceWeight,
                    out error))
            {
                return false;
            }

            if (!TryResolveDiscordPrivileges(
                    identity,
                    discordPrivilegeGroups,
                    managedDiscordGroups,
                    pendingRevocations,
                    out error))
            {
                return false;
            }

            string[] combinedPrivilegeGroups = steamPrivilegeGroups
                .Union(discordPrivilegeGroups, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            snapshot = new PlayerAccessSnapshot
            {
                PlayerUserId = identity.PlayerUserId,
                DiscordUserId = identity.DiscordUserId,
                SteamPrivilegeGroups = steamPrivilegeGroups.ToArray(),
                DiscordPrivilegeGroups = discordPrivilegeGroups.ToArray(),
                PrivilegeGroups = combinedPrivilegeGroups,
                ManagedDiscordGroups = managedDiscordGroups.ToArray(),
                PendingRevocations = pendingRevocations.ToArray(),
                TotalPlaytimeSeconds = totalPlaytimeSeconds,
                TemporaryRolePreferenceWeight = temporaryRolePreferenceWeight,
            };
            error = null;
            return true;
        }

        public bool TryFinalizeRevocations(
            IReadOnlyCollection<PendingPrivilegeRevocation> revocations,
            out string error)
        {
            if (revocations == null || revocations.Count == 0)
            {
                error = null;
                return true;
            }

            return database.TryFinalizePrivilegeRevocations(
                revocations.Select(revocation => revocation.SourceId),
                out error);
        }

        private bool TryResolveSteamPrivileges(
            PlayerAccessIdentity identity,
            EarnedPrivilegeSettings currentSettings,
            ISet<string> result,
            ISet<string> managedResult,
            ICollection<PendingPrivilegeRevocation> pendingRevocations,
            out long totalPlaytimeSeconds,
            out double? temporaryRolePreferenceWeight,
            out string error)
        {
            totalPlaytimeSeconds = 0;
            temporaryRolePreferenceWeight = null;
            if (identity.PlayerId <= 0)
            {
                error = null;
                return true;
            }

            if (!database.TryGetPrivilegeGrants(
                    "steam",
                    identity.PlayerUserId,
                    out IReadOnlyCollection<string> activeGroups,
                    out IReadOnlyCollection<string> managedGroups,
                    out IReadOnlyCollection<PendingPrivilegeRevocation> storedRevocations,
                    out error))
            {
                return false;
            }

            result.UnionWith(activeGroups);
            managedResult.UnionWith(managedGroups);
            foreach (PendingPrivilegeRevocation revocation in storedRevocations)
                pendingRevocations.Add(revocation);

            ReferralSettings referralSettings = currentSettings.Referrals ?? new ReferralSettings();
            if (referralSettings.IsEnabled)
            {
                long qualificationSeconds = Math.Max(1, referralSettings.QualificationMinutes) * 60L;
                if (!database.TryIsPendingReferral(
                        identity.PlayerUserId,
                        qualificationSeconds,
                        out bool isPendingReferral,
                        out error))
                {
                    return false;
                }

                if (isPendingReferral &&
                    referralSettings.PendingReferralWeight > 0 &&
                    !double.IsNaN(referralSettings.PendingReferralWeight) &&
                    !double.IsInfinity(referralSettings.PendingReferralWeight))
                {
                    temporaryRolePreferenceWeight = referralSettings.PendingReferralWeight;
                }
            }

            error = null;
            return true;
        }

        private bool TryResolveDiscordPrivileges(
            PlayerAccessIdentity identity,
            ISet<string> result,
            ISet<string> managedResult,
            ICollection<PendingPrivilegeRevocation> pendingRevocations,
            out string error)
        {
            if (identity.DiscordUserId == 0)
            {
                error = null;
                return true;
            }

            if (!database.TryGetPrivilegeGrants(
                    "discord",
                    identity.DiscordUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    out IReadOnlyCollection<string> activeGroups,
                    out IReadOnlyCollection<string> managedGroups,
                    out IReadOnlyCollection<PendingPrivilegeRevocation> storedRevocations,
                    out error))
            {
                return false;
            }

            result.UnionWith(activeGroups);
            managedResult.UnionWith(managedGroups);
            foreach (PendingPrivilegeRevocation revocation in storedRevocations)
                pendingRevocations.Add(revocation);

            error = null;
            return true;
        }
    }
}
