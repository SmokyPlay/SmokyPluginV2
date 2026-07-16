namespace SmokyPluginV2.Handlers
{
    using System;
    using System.Collections.Concurrent;

    using CommandSystem;
    using Exiled.Events.EventArgs.Player;
    using Exiled.Events.EventArgs.Server;

    using SmokyPluginV2.Discord;
    using SmokyPluginV2.Patches;

    using PlayerEvents = Exiled.Events.Handlers.Player;
    using ServerEvents = Exiled.Events.Handlers.Server;

    internal sealed class DiscordModerationHandler
    {
        private readonly ConcurrentDictionary<string, PendingAction> pendingKicks = new ConcurrentDictionary<string, PendingAction>();

        public void Register()
        {
            PlayerEvents.Kicking += OnKicking;
            PlayerEvents.Kicked += OnKicked;
            PlayerEvents.Banned += OnBanned;
            PlayerEvents.IssuingMute += OnIssuingMute;
            PlayerEvents.RevokingMute += OnRevokingMute;
            ServerEvents.Unbanned += OnUnbanned;
        }

        public void Unregister()
        {
            PlayerEvents.Kicking -= OnKicking;
            PlayerEvents.Kicked -= OnKicked;
            PlayerEvents.Banned -= OnBanned;
            PlayerEvents.IssuingMute -= OnIssuingMute;
            PlayerEvents.RevokingMute -= OnRevokingMute;
            ServerEvents.Unbanned -= OnUnbanned;
        }

        private void OnKicking(KickingEventArgs ev)
        {
            if (!ev.IsAllowed || IsBanDisconnectReason(ev.Reason))
                return;

            pendingKicks[TargetKey(ev.Target)] = new PendingAction(ev.Target, SenderText(ev.CommandSender, ev.Player), "не применяется", ev.Reason);
        }

        private void OnKicked(KickedEventArgs ev)
        {
            if (IsBanDisconnectReason(ev.Reason))
            {
                pendingKicks.TryRemove(TargetKey(ev.Player), out _);
                return;
            }

            if (pendingKicks.TryRemove(TargetKey(ev.Player), out PendingAction action))
                Log("Кик игрока", action.Target, action.Moderator, action.Duration, action.Reason);
            else
                Log("Кик игрока", ev.Player, "**Dedicated Server / неизвестно**", "не применяется", ev.Reason);
        }

        private void OnBanned(BannedEventArgs ev)
        {
            // An online ban is persisted twice by the game: once by IP and once by account ID.
            // Only the account record is useful for a moderation log.
            if (ev.Type == BanHandler.BanType.IP || (ev.Details?.Id?.Contains("@") == false))
                return;

            string moderator = ev.Player != null && !ev.Player.IsHost
                ? DiscordLogService.PlayerText(ev.Player)
                : string.IsNullOrWhiteSpace(ev.Details?.Issuer)
                    ? "**Dedicated Server / неизвестно**"
                    : DiscordLogService.Escape(ev.Details.Issuer);
            string reason = ev.Details?.Reason ?? "не указана";
            string duration = ev.Details == null
                ? "неизвестно"
                : FormatDuration(Math.Max(0, (long)Math.Ceiling((new DateTime(ev.Details.Expires).ToLocalTime() - DateTime.Now).TotalSeconds)));

            if (ev.Target != null)
            {
                Log("Бан игрока", ev.Target, moderator, duration, reason);
                return;
            }

            DiscordLogService.Current?.LogModeration(
                "Бан игрока",
                $"**Игрок:** **{DiscordLogService.Escape(ev.Details?.OriginalName)}** (`{DiscordLogService.Escape(ev.Details?.Id)}`)\n**Модератор:** {moderator}\n**Срок:** {DiscordLogService.Escape(duration)}\n**Причина:** {DiscordLogService.Escape(reason)}");
        }

        private void OnIssuingMute(IssuingMuteEventArgs ev)
        {
            if (!ev.IsAllowed)
                return;

            string type = ev.IsIntercom ? "Мут интеркома" : "Голосовой мут";
            Log(type, ev.Player, SenderText(RemoteAdminCommandLoggingPatch.CurrentSender, null), "до ручного снятия", "Причина не передаётся игровым событием mute");
        }

        private void OnRevokingMute(RevokingMuteEventArgs ev)
        {
            if (!ev.IsAllowed)
                return;

            string type = ev.IsIntercom ? "Снят мут интеркома" : "Снят голосовой мут";
            Log(type, ev.Player, SenderText(RemoteAdminCommandLoggingPatch.CurrentSender, null), "—", "—", false);
        }

        private void OnUnbanned(UnbannedEventArgs ev)
        {
            string moderator = SenderText(RemoteAdminCommandLoggingPatch.CurrentSender, null);
            DiscordLogService.Current?.LogModeration(
                "Игрок разбанен",
                $"**Идентификатор:** `{DiscordLogService.Escape(ev.TargetId)}`\n**Тип блокировки:** `{ev.BanType}`\n**Модератор:** {moderator}",
                false);
        }

        private static void Log(string action, Exiled.API.Features.Player target, string moderator, string duration, string reason, bool punishment = true)
        {
            DiscordLogService.Current?.LogModeration(
                action,
                $"**Игрок:** {DiscordLogService.PlayerText(target)}\n**Модератор:** {moderator}\n**Срок:** {DiscordLogService.Escape(duration)}\n**Причина:** {DiscordLogService.Escape(reason)}",
                punishment);
        }

        private static string SenderText(ICommandSender sender, Exiled.API.Features.Player fallback)
        {
            if (sender is CommandSender commandSender)
                return $"**{DiscordLogService.Escape(commandSender.Nickname)}** (`{DiscordLogService.Escape(commandSender.SenderId)}`)";

            return fallback != null ? DiscordLogService.PlayerText(fallback) : "**Dedicated Server / неизвестно**";
        }

        private static string FormatDuration(long seconds)
        {
            if (seconds <= 0)
                return "бессрочно";

            TimeSpan duration = TimeSpan.FromSeconds(seconds);
            if (duration.TotalDays >= 1)
                return $"{duration.TotalDays:0.##} дн.";
            if (duration.TotalHours >= 1)
                return $"{duration.TotalHours:0.##} ч.";
            if (duration.TotalMinutes >= 1)
                return $"{duration.TotalMinutes:0.##} мин.";

            return $"{duration.TotalSeconds:0} сек.";
        }

        private static string TargetKey(Exiled.API.Features.Player player) => player?.UserId ?? player?.Id.ToString() ?? Guid.NewGuid().ToString("N");

        private static bool IsBanDisconnectReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            return reason.IndexOf("you have been banned", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("вы были забанены", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("вы были заблокированы", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class PendingAction
        {
            public PendingAction(Exiled.API.Features.Player target, string moderator, string duration, string reason)
            {
                Target = target;
                Moderator = moderator;
                Duration = duration;
                Reason = reason;
            }

            public Exiled.API.Features.Player Target { get; }

            public string Moderator { get; }

            public string Duration { get; }

            public string Reason { get; }
        }
    }
}
