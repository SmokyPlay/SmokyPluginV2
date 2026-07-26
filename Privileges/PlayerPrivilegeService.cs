namespace SmokyPluginV2.Privileges
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using SmokyPluginV2.Database;
    using SmokyPluginV2.Referrals;

    internal sealed class PlayerPrivilegeService
    {
        private readonly MariaDbService database;
        private EarnedPrivilegeSettings settings;

        public PlayerPrivilegeService(MariaDbService database, EarnedPrivilegeSettings settings)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            this.settings = settings ?? new EarnedPrivilegeSettings();
        }

        public void ReloadSettings(EarnedPrivilegeSettings reloadedSettings)
        {
            settings = reloadedSettings ?? new EarnedPrivilegeSettings();
        }

        public bool TryResolveBySteamId(string playerUserId, out PlayerAccessSnapshot snapshot, out string error)
        {
            snapshot = null;
            if (!database.TryResolveAccessIdentityBySteamId(
                    playerUserId,
                    out long playerId,
                    out string resolvedPlayerUserId,
                    out ulong discordUserId,
                    out error))
            {
                return false;
            }

            return TryResolveSources(
                new ResolvedIdentity
                {
                    PlayerId = playerId,
                    PlayerUserId = resolvedPlayerUserId,
                    DiscordUserId = discordUserId,
                },
                out snapshot,
                out error);
        }

        public bool TryResolveByDiscordId(ulong discordUserId, out PlayerAccessSnapshot snapshot, out string error)
        {
            snapshot = null;
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

            return TryResolveSources(
                new ResolvedIdentity
                {
                    PlayerId = playerId,
                    PlayerUserId = playerUserId,
                    DiscordUserId = discordUserId,
                },
                out snapshot,
                out error);
        }

        private bool TryResolveSources(
            ResolvedIdentity identity,
            out PlayerAccessSnapshot snapshot,
            out string error)
        {
            snapshot = null;
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
            if (!TryResolveSteamPrivileges(
                    identity,
                    currentSettings,
                    groupName,
                    steamPrivilegeGroups,
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
                    out error))
                return false;

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
                TotalPlaytimeSeconds = totalPlaytimeSeconds,
                TemporaryRolePreferenceWeight = temporaryRolePreferenceWeight,
            };
            error = null;
            return true;
        }

        private bool TryResolveSteamPrivileges(
            ResolvedIdentity identity,
            EarnedPrivilegeSettings currentSettings,
            string groupName,
            ISet<string> result,
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
                long qualificationSeconds =
                    Math.Max(1, referralSettings.QualificationMinutes) * 60L;
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

            error = null;
            return true;
        }

        private static bool TryResolveDiscordPrivileges(
            ResolvedIdentity identity,
            ISet<string> result,
            ISet<string> managedResult,
            out string error)
        {
            // Discord-bound donations will be resolved here when that source is implemented.
            error = null;
            return true;
        }

        private sealed class ResolvedIdentity
        {
            public long PlayerId { get; set; }

            public string PlayerUserId { get; set; }

            public ulong DiscordUserId { get; set; }
        }
    }
}
