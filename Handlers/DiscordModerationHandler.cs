namespace SmokyPluginV2.Handlers
{
    using System;
    using System.Collections.Concurrent;
    using CommandSystem;
    using Exiled.Events.EventArgs.Player;
    using Exiled.Events.EventArgs.Server;
    using SmokyPluginV2.Database;
    using SmokyPluginV2.Discord;
    using SmokyPluginV2.Moderation;
    using SmokyPluginV2.Patches;
    using PlayerEvents = Exiled.Events.Handlers.Player;
    using ServerEvents = Exiled.Events.Handlers.Server;

    internal sealed class DiscordModerationHandler
    {
        private readonly ConcurrentDictionary<string, PendingKick> pendingKicks = new ConcurrentDictionary<string, PendingKick>();
        private readonly PunishmentService punishments;

        public DiscordModerationHandler(PunishmentService punishments) { this.punishments = punishments; }

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
            if (!ev.IsAllowed || IsBanDisconnectReason(ev.Reason)) return;
            string moderatorUserId = SenderId(ev.CommandSender, ev.Player);
            bool automaticAfk = IsServerSender(ev.CommandSender) && IsAfkReason(ev.Reason);
            pendingKicks[Key(ev.Target)] = new PendingKick(ev.Target, SenderText(ev.CommandSender, ev.Player), moderatorUserId, ev.Reason, automaticAfk);
        }

        private void OnKicked(KickedEventArgs ev)
        {
            if (IsBanDisconnectReason(ev.Reason)) { pendingKicks.TryRemove(Key(ev.Player), out _); return; }
            if (pendingKicks.TryRemove(Key(ev.Player), out PendingKick pending))
            {
                long? punishmentId = null;
                if (!pending.IsAutomaticAfk)
                    punishmentId = Store(pending.Target?.UserId, pending.Target?.Nickname, pending.ModeratorUserId, PunishmentType.Kick, pending.Reason, null);
                Log("Кик игрока", pending.Target, pending.Moderator, "не применяется", pending.Reason, punishmentId);
            }
            else
            {
                long? punishmentId = null;
                if (!IsAfkReason(ev.Reason))
                    punishmentId = Store(ev.Player?.UserId, ev.Player?.Nickname, "server", PunishmentType.Kick, ev.Reason, null);
                Log("Кик игрока", ev.Player, "**Dedicated Server / неизвестно**", "не применяется", ev.Reason, punishmentId);
            }
        }

        private void OnBanned(BannedEventArgs ev)
        {
            if (ev.Type == BanHandler.BanType.IP || ev.Details == null || !PostgreSqlService.IsSteamUserId(ev.Details.Id)) return;
            string issuerText = ev.Player != null && !ev.Player.IsHost ? DiscordLogService.PlayerText(ev.Player) :
                string.IsNullOrWhiteSpace(ev.Details.Issuer) ? "**Dedicated Server / неизвестно**" : DiscordLogService.Escape(ev.Details.Issuer);
            string reason = string.IsNullOrWhiteSpace(ev.Details.Reason) ? "не указана" : ev.Details.Reason;
            DateTime? expires = ev.Details.Expires > DateTime.UtcNow.Ticks ? new DateTime(ev.Details.Expires, DateTimeKind.Utc) : (DateTime?)null;
            string moderatorId = SenderId(RemoteAdminCommandLoggingPatch.CurrentSender, ev.Player);
            long? punishmentId = Store(ev.Details.Id, ev.Target?.Nickname ?? ev.Details.OriginalName, moderatorId, PunishmentType.Ban, reason, expires);
            string duration = expires.HasValue ? FormatDuration((long)Math.Ceiling((expires.Value - DateTime.UtcNow).TotalSeconds)) : "бессрочно";
            if (ev.Target != null) Log("Бан игрока", ev.Target, issuerText, duration, reason, punishmentId);
            else DiscordLogService.Current?.LogModeration("Бан игрока",
                $"**Игрок:** **{DiscordLogService.Escape(ev.Details.OriginalName)}** (`{DiscordLogService.Escape(ev.Details.Id)}`)\n" +
                $"**Модератор:** {issuerText}{PunishmentIdLine(punishmentId)}\n" +
                $"**Срок:** {duration}\n**Причина:** {DiscordLogService.Escape(reason)}");
        }

        private void OnUnbanned(UnbannedEventArgs ev)
        {
            if (ev.BanType != BanHandler.BanType.IP && punishments != null && PostgreSqlService.IsSteamUserId(ev.TargetId))
            {
                if (!punishments.TryDeleteActiveBans(ev.TargetId, DateTime.UtcNow, out int deleted, out string error))
                    Exiled.API.Features.Log.Error("[Moderation] Failed to update history after unban: " + error);
                else if (deleted > 0)
                    Exiled.API.Features.Log.Info($"[Moderation] Removed {deleted} active ban record(s) for {ev.TargetId}.");
            }
            DiscordLogService.Current?.LogModeration("Игрок разбанен",
                $"**Идентификатор:** `{DiscordLogService.Escape(ev.TargetId)}`\n**Тип блокировки:** `{ev.BanType}`\n**Модератор:** {SenderText(RemoteAdminCommandLoggingPatch.CurrentSender, null)}", false);
        }

        private void OnIssuingMute(IssuingMuteEventArgs ev)
        {
            if (!ev.IsAllowed) return;
            Log(ev.IsIntercom ? "Мут интеркома" : "Голосовой мут", ev.Player, SenderText(RemoteAdminCommandLoggingPatch.CurrentSender, null), "до ручного снятия", "Причина не передаётся игровым событием mute");
        }

        private void OnRevokingMute(RevokingMuteEventArgs ev)
        {
            if (!ev.IsAllowed) return;
            Log(ev.IsIntercom ? "Снят мут интеркома" : "Снят голосовой мут", ev.Player, SenderText(RemoteAdminCommandLoggingPatch.CurrentSender, null), "—", "—", null, false);
        }

        private long? Store(string userId, string nickname, string moderatorId, PunishmentType type, string reason, DateTime? expires)
        {
            if (punishments == null || !PostgreSqlService.IsSteamUserId(userId)) return null;
            PunishmentRecord record = new PunishmentRecord
            {
                PlayerUserId = userId, PlayerNickname = nickname ?? string.Empty,
                ModeratorUserId = string.IsNullOrWhiteSpace(moderatorId) ? "server" : moderatorId,
                Type = type, Reason = string.IsNullOrWhiteSpace(reason) ? "не указана" : reason,
                IssuedAtUtc = DateTime.UtcNow, ExpiresAtUtc = expires,
            };
            if (!punishments.TryAdd(record, out string error))
            {
                Exiled.API.Features.Log.Error($"[Moderation] Failed to store {type}: {error}");
                return null;
            }

            return record.Id;
        }

        private static void Log(string action, Exiled.API.Features.Player target, string moderator, string duration, string reason, long? punishmentId = null, bool punishment = true) =>
            DiscordLogService.Current?.LogModeration(action,
                $"**Игрок:** {DiscordLogService.PlayerText(target)}\n**Модератор:** {moderator}{PunishmentIdLine(punishmentId)}\n" +
                $"**Срок:** {DiscordLogService.Escape(duration)}\n**Причина:** {DiscordLogService.Escape(reason)}", punishment);

        private static string PunishmentIdLine(long? punishmentId) =>
            punishmentId.HasValue ? $"\n**ID наказания:** `#{punishmentId.Value}`" : string.Empty;

        private static string SenderText(ICommandSender sender, Exiled.API.Features.Player fallback) => sender is CommandSender commandSender
            ? $"**{DiscordLogService.Escape(commandSender.Nickname)}** (`{DiscordLogService.Escape(commandSender.SenderId)}`)"
            : fallback != null ? DiscordLogService.PlayerText(fallback) : "**Dedicated Server / неизвестно**";

        private static string SenderId(ICommandSender sender, Exiled.API.Features.Player fallback) =>
            IsServerSender(sender) ? "server" :
            sender is CommandSender commandSender && !string.IsNullOrWhiteSpace(commandSender.SenderId) ? commandSender.SenderId :
            fallback != null && !fallback.IsHost ? fallback.UserId : "server";

        private static bool IsServerSender(ICommandSender sender) =>
            sender is ServerConsoleSender || sender == Exiled.API.Features.Server.Host?.Sender;

        private static string FormatDuration(long seconds)
        {
            if (seconds <= 0) return "бессрочно";
            TimeSpan value = TimeSpan.FromSeconds(seconds);
            if (value.TotalDays >= 1) return $"{value.TotalDays:0.##} дн.";
            if (value.TotalHours >= 1) return $"{value.TotalHours:0.##} ч.";
            if (value.TotalMinutes >= 1) return $"{value.TotalMinutes:0.##} мин.";
            return $"{value.TotalSeconds:0} сек.";
        }

        private static string Key(Exiled.API.Features.Player player) => player?.UserId ?? player?.Id.ToString() ?? Guid.NewGuid().ToString("N");
        private static bool IsBanDisconnectReason(string reason) => !string.IsNullOrWhiteSpace(reason) &&
            (reason.IndexOf("you have been banned", StringComparison.OrdinalIgnoreCase) >= 0 || reason.IndexOf("забан", StringComparison.OrdinalIgnoreCase) >= 0 || reason.IndexOf("заблокирован", StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool IsAfkReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            return reason.IndexOf("afk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("away from keyboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("неактив", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("бездейств", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("отсутстви", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class PendingKick
        {
            public PendingKick(Exiled.API.Features.Player target, string moderator, string moderatorUserId, string reason, bool isAutomaticAfk)
            { Target = target; Moderator = moderator; ModeratorUserId = moderatorUserId; Reason = reason; IsAutomaticAfk = isAutomaticAfk; }
            public Exiled.API.Features.Player Target { get; }
            public string Moderator { get; }
            public string ModeratorUserId { get; }
            public string Reason { get; }
            public bool IsAutomaticAfk { get; }
        }
    }
}
