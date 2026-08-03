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
            string targetUserId = ev.Target?.UserId;
            if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(ev.Target, out string targetSteamUserId) == true)
                targetUserId = targetSteamUserId;
            pendingKicks[Key(ev.Target)] = new PendingKick(ev.Target, targetUserId, SenderText(ev.CommandSender, ev.Player), moderatorUserId, ev.Reason, automaticAfk);
        }

        private void OnKicked(KickedEventArgs ev)
        {
            if (IsBanDisconnectReason(ev.Reason)) { pendingKicks.TryRemove(Key(ev.Player), out _); return; }
            if (pendingKicks.TryRemove(Key(ev.Player), out PendingKick pending))
            {
                long? punishmentId = null;
                if (!pending.IsAutomaticAfk)
                    punishmentId = Store(pending.TargetUserId, pending.Target?.Nickname, pending.ModeratorUserId, PunishmentType.Kick, pending.Reason, null);
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
            if (ev.Details == null) return;
            string issuerText = ev.Player != null && !ev.Player.IsHost ? DiscordLogService.PlayerText(ev.Player) :
                string.IsNullOrWhiteSpace(ev.Details.Issuer) ? "**Dedicated Server / неизвестно**" : DiscordLogService.Escape(ev.Details.Issuer);
            string reason = string.IsNullOrWhiteSpace(ev.Details.Reason) ? "не указана" : ev.Details.Reason;
            DateTime? expires = ev.Details.Expires > DateTime.UtcNow.Ticks ? new DateTime(ev.Details.Expires, DateTimeKind.Utc) : (DateTime?)null;
            string duration = expires.HasValue ? FormatDuration((long)Math.Ceiling((expires.Value - DateTime.UtcNow).TotalSeconds)) : "бессрочно";
            if (ev.Type == BanHandler.BanType.IP)
            {
                string playerText = ev.Target != null
                    ? DiscordLogService.PlayerText(ev.Target)
                    : "**Неизвестный игрок**";
                DiscordLogService.Current?.LogModeration("IP-бан игрока",
                    $"**Игрок:** {playerText}\n" +
                    $"**IP-адрес:** `{DiscordLogService.Escape(ev.Details.Id)}`\n" +
                    $"**Модератор:** {issuerText}\n" +
                    $"**Срок:** {duration}\n**Причина:** {DiscordLogService.Escape(reason)}");
                return;
            }

            string moderatorId = SenderId(RemoteAdminCommandLoggingPatch.CurrentSender, ev.Player);
            string targetUserId = ev.Target?.UserId ?? ev.Details.Id;
            string targetNickname = ev.Target?.Nickname;
            string displayUserId = ev.Details.Id;
            if (ev.Target == null && TryResolvePunishmentSteamUserId(targetUserId, out string offlineSteamUserId))
            {
                targetUserId = offlineSteamUserId;
                displayUserId = offlineSteamUserId;
                targetNickname = GetLastKnownNickname(offlineSteamUserId);
            }

            long? punishmentId = Store(targetUserId, ev.Target != null ? targetNickname : null, moderatorId, PunishmentType.Ban, reason, expires);
            if (ev.Target != null) Log("Бан игрока", ev.Target, issuerText, duration, reason, punishmentId);
            else DiscordLogService.Current?.LogModeration("Бан игрока",
                $"**Игрок:** {OfflinePlayerText(targetNickname, displayUserId)}\n" +
                $"**Модератор:** {issuerText}{PunishmentIdLine(punishmentId)}\n" +
                $"**Срок:** {duration}\n**Причина:** {DiscordLogService.Escape(reason)}");
        }

        private void OnUnbanned(UnbannedEventArgs ev)
        {
            string targetLine;
            if (ev.BanType == BanHandler.BanType.IP)
            {
                targetLine = $"**IP-адрес:** `{DiscordLogService.Escape(ev.TargetId)}`";
            }
            else if (TryResolvePunishmentSteamUserId(ev.TargetId, out string steamUserId))
            {
                string nickname = GetLastKnownNickname(steamUserId);
                targetLine = $"**Игрок:** {OfflinePlayerText(nickname, steamUserId)}";
                if (punishments != null)
                {
                    if (!punishments.TryDeleteActiveBans(steamUserId, DateTime.UtcNow, out int deleted, out string error))
                        Exiled.API.Features.Log.Error("[Moderation] Failed to update history after unban: " + error);
                    else if (deleted > 0)
                        Exiled.API.Features.Log.Info($"[Moderation] Removed {deleted} active ban record(s) for {steamUserId}.");
                }
            }
            else
            {
                targetLine = $"**Игрок:** {OfflinePlayerText(null, ev.TargetId)}";
            }

            DiscordLogService.Current?.LogModeration("Игрок разбанен",
                $"{targetLine}\n**Тип блокировки:** `{ev.BanType}`\n**Модератор:** {SenderText(RemoteAdminCommandLoggingPatch.CurrentSender, null)}", false);
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
            if (punishments == null || !TryResolvePunishmentSteamUserId(userId, out string steamUserId))
            {
                return null;
            }

            PunishmentRecord record = new PunishmentRecord
            {
                PlayerUserId = steamUserId, PlayerNickname = nickname ?? string.Empty,
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

        private static bool TryResolvePunishmentSteamUserId(string userId, out string steamUserId)
        {
            if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(userId, out steamUserId) == true)
                return true;

            if (!PostgreSqlService.TryParseDiscordUserId(userId, out ulong discordUserId) ||
                Plugin.Instance?.Database == null)
            {
                steamUserId = null;
                return false;
            }

            bool resolved = Plugin.Instance.Database.TryGetPlayerUserId(
                discordUserId,
                out steamUserId,
                out _);
            return resolved && PostgreSqlService.IsSteamUserId(steamUserId);
        }

        private static void Log(string action, Exiled.API.Features.Player target, string moderator, string duration, string reason, long? punishmentId = null, bool punishment = true) =>
            DiscordLogService.Current?.LogModeration(action,
                $"**Игрок:** {DiscordLogService.PlayerText(target)}\n**Модератор:** {moderator}{PunishmentIdLine(punishmentId)}\n" +
                $"**Срок:** {DiscordLogService.Escape(duration)}\n**Причина:** {DiscordLogService.Escape(reason)}", punishment);

        private static string PunishmentIdLine(long? punishmentId) =>
            punishmentId.HasValue ? $"\n**ID наказания:** `#{punishmentId.Value}`" : string.Empty;

        private string GetLastKnownNickname(string steamUserId)
        {
            if (punishments == null)
                return null;

            if (!punishments.TryGetHistory(steamUserId, out PunishmentHistory history, out string error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Exiled.API.Features.Log.Error("[Moderation] Failed to resolve the last known nickname: " + error);
                return null;
            }

            return history.PlayerExists && !string.IsNullOrWhiteSpace(history.PlayerNickname)
                ? history.PlayerNickname
                : null;
        }

        private static string OfflinePlayerText(string nickname, string userId) =>
            $"**{DiscordLogService.Escape(string.IsNullOrWhiteSpace(nickname) ? "Неизвестный игрок" : nickname)}** " +
            $"(`{DiscordLogService.Escape(userId)}`)";

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
            public PendingKick(Exiled.API.Features.Player target, string targetUserId, string moderator, string moderatorUserId, string reason, bool isAutomaticAfk)
            { Target = target; TargetUserId = targetUserId; Moderator = moderator; ModeratorUserId = moderatorUserId; Reason = reason; IsAutomaticAfk = isAutomaticAfk; }
            public Exiled.API.Features.Player Target { get; }
            public string TargetUserId { get; }
            public string Moderator { get; }
            public string ModeratorUserId { get; }
            public string Reason { get; }
            public bool IsAutomaticAfk { get; }
        }
    }
}
