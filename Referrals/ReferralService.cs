namespace SmokyPluginV2.Referrals
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Exiled.API.Features;
    using Exiled.API.Features.Items;

    using ServerEvents = Exiled.Events.Handlers.Server;

    using SmokyPluginV2.Database;

    internal sealed class ReferralService : IDisposable
    {
        private readonly PostgreSqlService database;
        private readonly HashSet<string> janitorCardUses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object janitorCardLock = new object();
        private ReferralSettings settings;
        private bool registered;

        public ReferralService(PostgreSqlService database, ReferralSettings settings)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            this.settings = settings ?? new ReferralSettings();
        }

        public void ReloadSettings(ReferralSettings reloadedSettings) =>
            settings = reloadedSettings ?? new ReferralSettings();

        public void Register()
        {
            if (registered)
                return;
            ServerEvents.WaitingForPlayers += ResetRoundUses;
            ServerEvents.RestartingRound += ResetRoundUses;
            registered = true;
        }

        public void Dispose()
        {
            if (registered)
            {
                ServerEvents.WaitingForPlayers -= ResetRoundUses;
                ServerEvents.RestartingRound -= ResetRoundUses;
                registered = false;
            }

            lock (janitorCardLock)
                janitorCardUses.Clear();
        }

        public bool TryAccept(
            string invitedPlayerUserId,
            string code,
            long unpersistedPlaytimeSeconds,
            out string response)
        {
            ReferralSettings current = settings ?? new ReferralSettings();
            if (!current.IsEnabled)
            {
                response = "Реферальная программа отключена.";
                return false;
            }

            int entryMinutes = Math.Max(0, current.CodeEntryMaxMinutes);
            bool accepted = database.TryAcceptReferral(
                invitedPlayerUserId,
                code,
                Math.Max(0, unpersistedPlaytimeSeconds),
                entryMinutes * 60L,
                DateTime.UtcNow,
                out response);
            if (accepted)
                Plugin.Instance?.RolePreferences?.NotifyAccessGroupsChanged();
            return accepted;
        }

        public bool TryGetOrCreateStatus(
            ulong discordUserId,
            out ReferralStatus status,
            out string error)
        {
            ReferralSettings current = settings ?? new ReferralSettings();
            if (!current.IsEnabled)
            {
                status = null;
                error = "Реферальная программа отключена.";
                return false;
            }

            return database.TryGetOrCreateReferralStatus(
                discordUserId,
                EarnedPrivilegeGroupName,
                out status,
                out error);
        }

        public void OnPlaytimePersisted(string playerUserId, long addedPlaytimeSeconds)
        {
            ReferralSettings current = settings ?? new ReferralSettings();
            if (!current.IsEnabled || addedPlaytimeSeconds <= 0)
                return;

            long qualificationSeconds = Math.Max(1, current.QualificationMinutes) * 60L;
            if (!database.TryGetReferralQualificationTransition(
                    playerUserId,
                    addedPlaytimeSeconds,
                    qualificationSeconds,
                    Math.Max(1, current.RequiredReferrals),
                    out ReferralQualificationTransition transition,
                    out string error))
            {
                Log.Error($"[Referrals] Qualification check failed for {playerUserId}: {error}");
                return;
            }

            if (transition == null || !transition.InviteeQualified)
                return;

            if (transition.InviteeJustQualified)
                Plugin.Instance?.PlayerAccess?.SynchronizeBySteamId(playerUserId);
            if (transition.RewardThresholdReached &&
                !string.IsNullOrWhiteSpace(transition.InviterPlayerUserId))
            {
                string groupName = EarnedPrivilegeGroupName;
                if (!database.TryGrantPermanentSteamPrivilege(
                        transition.InviterPlayerUserId,
                        groupName,
                        "earned_referrals",
                        out bool inserted,
                        out error))
                {
                    Log.Error(
                        $"[Referrals] Could not grant {groupName} to " +
                        $"{transition.InviterPlayerUserId}: {error}");
                    return;
                }

                if (inserted)
                {
                    Plugin.Instance?.PlayerAccess?.SynchronizeBySteamId(
                        transition.InviterPlayerUserId);
                }
            }
        }

        public bool TryGiveJanitorCard(Player player, out string response)
        {
            if (player == null || !player.IsConnected || player.IsHost || player.IsNPC)
            {
                response = "Не удалось определить ваш игровой аккаунт.";
                return false;
            }

            if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(player, out string steamUserId) != true)
            {
                response = "Для вашего Discord-аккаунта не найдена связка со Steam.";
                return false;
            }

            if (!Round.IsStarted || Round.IsEnded)
            {
                response = "Карту уборщика можно получить только во время активного раунда.";
                return false;
            }

            lock (janitorCardLock)
            {
                if (janitorCardUses.Contains(steamUserId))
                {
                    response = "Вы уже получали карту уборщика в этом раунде.";
                    return false;
                }
            }

            long qualificationSeconds = QualificationMinutes * 60L;
            if (!database.TryIsPendingReferral(
                    steamUserId,
                    qualificationSeconds,
                    out bool isPendingReferral,
                    out string error))
            {
                response = error;
                return false;
            }

            if (!isPendingReferral)
            {
                response = "Карта уборщика доступна после ввода реферального кода — до момента, когда приглашение будет засчитано.";
                return false;
            }

            if (player.Items.Count() >= 8)
            {
                response = "Ваш инвентарь заполнен. Освободите место и повторите команду.";
                return false;
            }

            Item item;
            try
            {
                item = player.AddItem(ItemType.KeycardJanitor);
            }
            catch (Exception exception)
            {
                Log.Error($"[Referrals] Could not give a janitor keycard to {player.UserId}: {exception}");
                response = "Не удалось выдать карту уборщика. Возможно, ваш инвентарь заполнен.";
                return false;
            }

            if (item == null)
            {
                response = "Ваш инвентарь заполнен. Освободите место и повторите команду.";
                return false;
            }

            lock (janitorCardLock)
                janitorCardUses.Add(steamUserId);
            response = "Карта уборщика добавлена в ваш инвентарь.";
            return true;
        }

        public int QualificationMinutes =>
            Math.Max(1, (settings ?? new ReferralSettings()).QualificationMinutes);

        public int RequiredReferrals =>
            Math.Max(1, (settings ?? new ReferralSettings()).RequiredReferrals);

        public int CodeEntryMaxMinutes =>
            Math.Max(0, (settings ?? new ReferralSettings()).CodeEntryMaxMinutes);

        public double PendingReferralWeight
        {
            get
            {
                double value = (settings ?? new ReferralSettings()).PendingReferralWeight;
                return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value)
                    ? value
                    : 0;
            }
        }

        public int InGameMaximumDisplayedParticipants =>
            Math.Max(0, (settings ?? new ReferralSettings()).InGameMaximumDisplayedParticipants);

        public string EarnedPrivilegeGroupName =>
            (Plugin.Instance?.Config?.EarnedPrivileges?.GroupName ?? string.Empty).Trim();

        public double EarnedPrivilegeWeight
        {
            get
            {
                RolePreferenceSettings rolePreferences =
                    Plugin.Instance?.Config?.RolePreferences ?? new RolePreferenceSettings();
                string groupName = EarnedPrivilegeGroupName;
                double fallback = IsValidWeight(rolePreferences.DefaultWeight)
                    ? rolePreferences.DefaultWeight
                    : 1d;
                if (string.IsNullOrWhiteSpace(groupName) || rolePreferences.PriorityTiers == null)
                    return fallback;

                double[] matchingWeights = rolePreferences.PriorityTiers
                    .Where(tier =>
                        tier?.Groups != null &&
                        tier.Groups.Any(group => string.Equals(
                            group?.Trim(),
                            groupName,
                            StringComparison.OrdinalIgnoreCase)) &&
                        IsValidWeight(tier.Weight))
                    .Select(tier => tier.Weight)
                    .ToArray();
                return matchingWeights.Length == 0 ? fallback : matchingWeights.Max();
            }
        }

        public bool TryGetOrCreateStatus(
            string playerUserId,
            out ReferralStatus status,
            out string error)
        {
            ReferralSettings current = settings ?? new ReferralSettings();
            if (!current.IsEnabled)
            {
                status = null;
                error = "Реферальная программа отключена.";
                return false;
            }

            return database.TryGetOrCreateReferralStatus(
                playerUserId,
                EarnedPrivilegeGroupName,
                out status,
                out error);
        }

        private static bool IsValidWeight(double value) =>
            value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);

        private void ResetRoundUses()
        {
            lock (janitorCardLock)
                janitorCardUses.Clear();
        }
    }
}
