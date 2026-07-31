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
            string groupName = (currentSettings.GroupName ?? string.Empty).Trim();
            if (groupName.Length > 64)
            {
                error = "Название группы привилегии не может быть длиннее 64 символов.";
                return false;
            }

            HashSet<string> steamPrivilegeGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> discordPrivilegeGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> managedDiscordGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<PendingPrivilegeRevocation> pendingRevocations = new List<PendingPrivilegeRevocation>();
            if (!TryResolveSteamPrivileges(
                    identity,
                    currentSettings,
                    groupName,
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

            // Each persistent privilege source finalizes its own records here. No
            // donation source exists yet, so a non-empty collection is rejected
            // instead of silently losing a future PostgreSQL state transition.
            error = "Для источника отзыва привилегии не настроена фиксация в PostgreSQL.";
            return false;
        }

        private bool TryResolveSteamPrivileges(
            PlayerAccessIdentity identity,
            EarnedPrivilegeSettings currentSettings,
            string groupName,
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

            if (!database.TryGetTotalPlaytimeSeconds(identity.PlayerId, out totalPlaytimeSeconds, out error))
                return false;

            long requiredSeconds = currentSettings.RequiredHours > 0
                ? (long)Math.Ceiling(Math.Min(currentSettings.RequiredHours, long.MaxValue / 3600d) * 3600d)
                : long.MaxValue;
            bool earnedByPlaytime = totalPlaytimeSeconds >= requiredSeconds;
            bool earnedByReferrals = false;
            ReferralSettings referralSettings = currentSettings.Referrals ?? new ReferralSettings();
            if (referralSettings.IsEnabled)
            {
                long qualificationSeconds = Math.Max(1, referralSettings.QualificationMinutes) * 60L;
                if (!database.TryGetReferralAccessState(
                        identity.PlayerId,
                        qualificationSeconds,
                        out ReferralAccessState referralState,
                        out error))
                {
                    return false;
                }

                earnedByReferrals = referralState.QualifiedReferralCount >=
                    Math.Max(1, referralSettings.RequiredReferrals);
                if (referralState.IsPendingInvitee &&
                    referralSettings.PendingReferralWeight > 0 &&
                    !double.IsNaN(referralSettings.PendingReferralWeight) &&
                    !double.IsInfinity(referralSettings.PendingReferralWeight))
                {
                    temporaryRolePreferenceWeight = referralSettings.PendingReferralWeight;
                }
            }

            if (!string.IsNullOrWhiteSpace(groupName) &&
                (earnedByPlaytime || earnedByReferrals))
            {
                result.Add(groupName);
            }

            // Steam-bound donations will also contribute active groups, managed
            // groups and source-id revocations here when that source is added.

            error = null;
            return true;
        }

        private static bool TryResolveDiscordPrivileges(
            PlayerAccessIdentity identity,
            ISet<string> result,
            ISet<string> managedResult,
            ICollection<PendingPrivilegeRevocation> pendingRevocations,
            out string error)
        {
            // Active Discord-bound donations will be added to result. Expired,
            // non-revoked donations will add their group to managedResult and a
            // source-id entry to pendingRevocations when that source is implemented.
            error = null;
            return true;
        }
    }
}
