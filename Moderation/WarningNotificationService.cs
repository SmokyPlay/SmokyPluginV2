namespace SmokyPluginV2.Moderation
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using Exiled.API.Features;
    using Exiled.Events.EventArgs.Player;

    using SmokyPluginV2.Database;
    using SmokyPluginV2.Discord;

    using PlayerEvents = Exiled.Events.Handlers.Player;

    internal sealed class WarningNotificationService
    {
        private readonly PunishmentService punishments;
        private bool isRegistered;

        public WarningNotificationService(PunishmentService punishments)
        {
            this.punishments = punishments ?? throw new ArgumentNullException(nameof(punishments));
        }

        public void Register()
        {
            if (isRegistered)
                return;

            isRegistered = true;
            PlayerEvents.Verified += OnVerified;
        }

        public void Unregister()
        {
            if (!isRegistered)
                return;

            isRegistered = false;
            PlayerEvents.Verified -= OnVerified;
        }

        public void NotifyIssuedWarning(PunishmentRecord warning, Player onlinePlayer, string moderatorNickname)
        {
            if (warning == null || warning.Type != PunishmentType.Warning || !NotificationsEnabled())
                return;

            if (onlinePlayer != null && onlinePlayer.IsConnected)
            {
                if (TryQueueBroadcast(onlinePlayer, warning, moderatorNickname, true))
                    MarkNotified(warning.Id);
                return;
            }

            if (!punishments.TryGetDiscordUserId(warning.PlayerUserId, out ulong discordUserId, out string error))
            {
                Log.Error($"[Moderation] Could not resolve Discord link for warning #{warning.Id}: {error}");
                return;
            }

            DiscordLogService discord = Plugin.Instance?.DiscordLogs;
            if (discordUserId == 0 || discord == null)
                return;

            string serverName = Plugin.Instance?.Database?.ServerName ?? $"Server {Server.Port}";
            Task.Run(async () =>
            {
                try
                {
                    bool delivered = await discord.SendWarningDirectMessageAsync(
                        discordUserId,
                        serverName,
                        warning.Reason,
                        warning.IssuedAtUtc).ConfigureAwait(false);
                    if (delivered)
                        MarkNotified(warning.Id);
                    else
                        Log.Info($"[Moderation] Discord DM for warning #{warning.Id} was not delivered; in-game notification remains pending.");
                }
                catch (Exception exception)
                {
                    Log.Error($"[Moderation] Discord DM delivery failed for warning #{warning.Id}: {exception}");
                }
            });
        }

        private void OnVerified(VerifiedEventArgs ev)
        {
            Player player = ev.Player;
            if (!isRegistered || player == null || !player.IsConnected || !NotificationsEnabled())
                return;

            if (!TryResolveSteamUserId(player, out string steamUserId))
                return;

            NotifyPending(player, steamUserId);
        }

        internal void OnIdentityResolved(Player player, string steamUserId)
        {
            if (!isRegistered || player == null || !player.IsConnected || !NotificationsEnabled() ||
                !PostgreSqlService.IsSteamUserId(steamUserId))
            {
                return;
            }

            NotifyPending(player, steamUserId);
        }

        private void NotifyPending(Player player, string steamUserId)
        {
            if (!punishments.TryGetPendingWarningNotifications(steamUserId, out IReadOnlyList<PunishmentRecord> pending, out string error))
            {
                Log.Error($"[Moderation] Could not load pending warnings for {steamUserId}: {error}");
                return;
            }

            foreach (PunishmentRecord warning in pending)
            {
                if (player == null || !player.IsConnected)
                    break;

                string moderator = string.IsNullOrWhiteSpace(warning.ModeratorNickname)
                    ? warning.ModeratorUserId
                    : warning.ModeratorNickname;
                if (TryQueueBroadcast(player, warning, moderator, false))
                    MarkNotified(warning.Id);
            }
        }

        private static bool TryResolveSteamUserId(Player player, out string steamUserId)
        {
            steamUserId = null;
            if (PostgreSqlService.IsSteamUserId(player?.UserId))
            {
                steamUserId = PostgreSqlService.ToExiledUserId(
                    PostgreSqlService.NormalizeSteamId(player.UserId));
                return true;
            }

            return Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(player, out steamUserId) == true;
        }

        private static bool TryQueueBroadcast(
            Player player,
            PunishmentRecord warning,
            string moderatorNickname,
            bool clearPrevious)
        {
            WarningSettings settings = Plugin.Instance?.Config?.Warnings;
            if (settings == null || player == null || !player.IsConnected)
                return false;

            try
            {
                string message = (settings.NotificationMessage ?? string.Empty)
                    .Replace("{id}", warning.Id.ToString())
                    .Replace("{reason}", warning.Reason ?? string.Empty)
                    .Replace("{moderator}", moderatorNickname ?? string.Empty);
                player.Broadcast(settings.NotificationDuration, message, shouldClearPrevious: clearPrevious);
                return true;
            }
            catch (Exception exception)
            {
                Log.Error($"[Moderation] Could not queue warning #{warning.Id} broadcast for {player.UserId}: {exception}");
                return false;
            }
        }

        private void MarkNotified(long punishmentId)
        {
            if (!punishments.TryMarkNotified(punishmentId, DateTime.UtcNow, out string error))
                Log.Error($"[Moderation] Could not mark warning #{punishmentId} as notified: {error}");
        }

        private static bool NotificationsEnabled() =>
            Plugin.Instance?.Config?.Warnings?.NotifyPlayer == true;
    }
}
