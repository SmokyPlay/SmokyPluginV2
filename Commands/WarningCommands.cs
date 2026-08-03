namespace SmokyPluginV2.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;

    using CommandSystem;
    using Exiled.API.Features;
    using Exiled.Permissions.Extensions;
    using RemoteAdmin;

    using SmokyPluginV2.Database;
    using SmokyPluginV2.Discord;
    using SmokyPluginV2.Moderation;

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class WarnCommand : ICommand
    {
        public string Command => "warn";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Выдаёт игроку предупреждение: warn [игрок или SteamID64] [причина].";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(ModerationPermissions.IssueWarning))
            {
                response = $"Недостаточно прав. Требуется: {ModerationPermissions.IssueWarning}";
                return false;
            }
            PunishmentService service = Plugin.Instance?.Punishments;
            WarningSettings settings = Plugin.Instance?.Config?.Warnings;
            if (service == null || settings?.IsEnabled != true) { response = "Система наказаний отключена."; return false; }
            if (arguments.Count < 2) { response = "Использование: warn [игрок или SteamID64] [причина]"; return false; }
            if (!ModerationCommandHelpers.TryGetModerator(sender, out PunishmentModerator moderator, out response)) return false;

            string selector = ModerationCommandHelpers.NormalizePlayerSelector(ArgumentAt(arguments, 0));
            Player target = Player.Get(selector);
            string userId;
            string nickname;
            bool online = target != null && target.IsConnected && !target.IsHost;
            if (online)
            {
                if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(target, out userId) != true)
                { response = "Для Discord-аккаунта игрока не найдена связка со Steam."; return false; }
                nickname = target.Nickname;
                if (!moderator.IsServer && moderator.KickPower < ModerationCommandHelpers.GetRequiredKickPower(target))
                { response = $"Недостаточно прав для выдачи предупреждения игроку {target.Nickname}."; return false; }
            }
            else
            {
                userId = ModerationCommandHelpers.NormalizeUserId(selector);
                if (!PostgreSqlService.IsSteamUserId(userId))
                { response = "Игрок не найден. Для оффлайн-игрока укажите точный SteamID64."; return false; }
                nickname = string.Empty;
                if (!moderator.IsServer && moderator.KickPower < ModerationCommandHelpers.GetRequiredKickPower(userId))
                { response = "Недостаточно прав для выдачи предупреждения этому игроку."; return false; }
            }

            string reason = string.Join(" ", arguments.Skip(1)).Trim();
            if (reason.Length == 0) { response = "Укажите причину предупреждения."; return false; }
            int maxLength = Math.Max(1, settings.MaxReasonLength);
            if (reason.Length > maxLength) { response = $"Причина слишком длинная. Максимум: {maxLength} символов."; return false; }

            PunishmentRecord record = new PunishmentRecord
            {
                PlayerUserId = userId,
                PlayerNickname = nickname,
                ModeratorUserId = moderator.UserId,
                Type = PunishmentType.Warning,
                IssuedAtUtc = DateTime.UtcNow,
                Reason = reason,
            };
            if (!service.TryAdd(record, out response)) return false;
            Plugin.Instance?.WarningNotifications?.NotifyIssuedWarning(record, online ? target : null, moderator.Nickname);
            Plugin.Instance?.DiscordLogs?.LogModeration("Предупреждение игроку",
                $"**Игрок:** **{DiscordLogService.Escape(string.IsNullOrWhiteSpace(nickname) ? "оффлайн-игрок" : nickname)}** (`{DiscordLogService.Escape(userId)}`)\n" +
                $"**Модератор:** **{DiscordLogService.Escape(moderator.Nickname)}** (`{DiscordLogService.Escape(moderator.UserId)}`)\n" +
                $"**ID наказания:** `#{record.Id}`\n**Причина:** {DiscordLogService.Escape(record.Reason)}");
            response = online
                ? $"Игроку {nickname} ({userId}) выдано предупреждение #{record.Id}."
                : $"Оффлайн-игроку {userId} выдано предупреждение #{record.Id}.";
            return true;
        }

        private static string ArgumentAt(ArraySegment<string> arguments, int index) => arguments.Array[arguments.Offset + index];
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class PunishmentsCommand : ICommand
    {
        public string Command => "punishments";
        public string[] Aliases => new[] { "punishmenthistory", "ph" };
        public string Description => "Показывает историю наказаний: punishments [игровой ID или SteamID64].";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(ModerationPermissions.ViewHistory))
            { response = $"Недостаточно прав. Требуется: {ModerationPermissions.ViewHistory}"; return false; }
            if (!ModerationCommandHelpers.TryResolveTarget(arguments, out string userId, out string displayName, out response)) return false;
            PunishmentService service = Plugin.Instance?.Punishments;
            if (service == null || !service.TryGetHistory(userId, out PunishmentHistory history, out response)) return false;
            if (!history.PlayerExists) { response = "Игрок с указанным SteamID64 не найден."; return false; }
            response = ModerationCommandHelpers.FormatHistory(displayName, history);
            return true;
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class DeletePunishmentCommand : ICommand
    {
        public string Command => "delpunishment";
        public string[] Aliases => new[] { "delpunish", "dp" };
        public string Description => "Удаляет запись наказания из истории: delpunishment [ID].";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(ModerationPermissions.DeleteHistory))
            { response = $"Недостаточно прав. Требуется: {ModerationPermissions.DeleteHistory}"; return false; }
            PunishmentService service = Plugin.Instance?.Punishments;
            if (service == null) { response = "Система наказаний отключена."; return false; }
            if (arguments.Count != 1 || !long.TryParse(arguments.Array[arguments.Offset], out long id) || id <= 0)
            { response = "Использование: delpunishment [ID]"; return false; }
            if (!service.TryDelete(id, out PunishmentRecord deleted, out response)) return false;
            Plugin.Instance?.DiscordLogs?.LogModeration("Запись наказания удалена",
                $"**Игрок:** `{DiscordLogService.Escape(deleted.PlayerUserId)}`\n**ID наказания:** `#{deleted.Id}`\n**Тип:** {ModerationCommandHelpers.TypeName(deleted.Type)}", false);
            response = $"Запись наказания #{deleted.Id} игрока {deleted.PlayerUserId} удалена.";
            return true;
        }
    }

    internal static class ModerationCommandHelpers
    {
        public static bool TryGetModerator(ICommandSender sender, out PunishmentModerator moderator, out string error)
        {
            CommandSender commandSender = sender as CommandSender;
            bool server = commandSender is ServerConsoleSender || commandSender == Server.Host.Sender;
            if (server)
            {
                moderator = new PunishmentModerator { UserId = "server", Nickname = "Dedicated Server", KickPower = byte.MaxValue, IsServer = true };
                error = null; return true;
            }
            if (commandSender == null || string.IsNullOrWhiteSpace(commandSender.SenderId))
            { moderator = null; error = "Не удалось определить отправителя команды."; return false; }
            moderator = new PunishmentModerator { UserId = commandSender.SenderId, Nickname = commandSender.Nickname, KickPower = commandSender.KickPower };
            error = null; return true;
        }

        public static int GetRequiredKickPower(Player target) => Math.Max(target.KickPower, target.Group?.RequiredKickPower ?? 0);
        public static int GetRequiredKickPower(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId) || Server.PermissionsHandler == null) return 0;
            if (!Server.PermissionsHandler.Members.TryGetValue(userId, out string groupName) ||
                !Server.PermissionsHandler.Groups.TryGetValue(groupName, out UserGroup group)) return 0;
            return Math.Max(group.KickPower, group.RequiredKickPower);
        }

        public static bool TryResolveTarget(ArraySegment<string> arguments, out string userId, out string displayName, out string error)
        {
            userId = null; displayName = null;
            if (Plugin.Instance?.Punishments == null) { error = "Система наказаний отключена."; return false; }
            if (arguments.Count < 1) { error = "Укажите игрока или его SteamID64."; return false; }
            string query = NormalizePlayerSelector(arguments.Array[arguments.Offset]);
            Player player = Player.Get(query);
            if (player != null && player.IsConnected && !player.IsHost)
            {
                if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(player, out userId) != true)
                { error = "Для Discord-аккаунта игрока не найдена связка со Steam."; return false; }
                displayName = player.Nickname; error = null; return true;
            }
            userId = NormalizeUserId(query);
            if (!PostgreSqlService.IsSteamUserId(userId)) { error = "Игрок не найден. Для оффлайн-игрока укажите точный SteamID64."; return false; }
            displayName = "оффлайн-игрок"; error = null; return true;
        }

        public static string FormatHistory(string fallbackName, PunishmentHistory history)
        {
            string name = string.IsNullOrWhiteSpace(history.PlayerNickname) ? fallbackName : history.PlayerNickname;
            StringBuilder text = new StringBuilder();
            text.AppendLine($"История наказаний — {name}");
            text.AppendLine($"SteamID: {PostgreSqlService.NormalizeSteamId(history.PlayerUserId)}");
            text.AppendLine(history.DiscordUserId == 0 ? "Discord: не привязан" : $"Discord ID: {history.DiscordUserId}");
            text.AppendLine($"Всего записей: {history.Records.Count}");
            text.AppendLine();
            text.AppendLine(FormatCleanPeriod(history.Records));
            foreach (PunishmentRecord item in history.Records.Take(5))
            {
                text.AppendLine();
                string icon = item.Type == PunishmentType.Warning ? "⚠" : item.Type == PunishmentType.Kick ? "🚪" : "🔨";
                text.AppendLine($"{icon} #{item.Id} • {TypeName(item.Type)}");
                text.AppendLine($"Причина: {item.Reason}");
                text.AppendLine($"Выдано: {item.IssuedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
                text.AppendLine($"Модератор: {FormatModerator(item)}");
                if (item.Type == PunishmentType.Ban)
                    text.AppendLine(item.ExpiresAtUtc.HasValue
                        ? $"Длительность: {FormatDuration(item.ExpiresAtUtc.Value - item.IssuedAtUtc)} | Истекает: {item.ExpiresAtUtc.Value.ToLocalTime():dd.MM.yyyy HH:mm:ss} | Статус: {(item.ExpiresAtUtc.Value > DateTime.UtcNow ? "активен" : "истёк")}"
                        : "Срок: бессрочно | Статус: активен");
            }
            if (history.Records.Count > 5)
                text.AppendLine($"\nПоказано 5 из {history.Records.Count} записей.");
            return text.ToString().TrimEnd();
        }

        private static string FormatDiscordHistory(string fallbackName, PunishmentHistory history)
        {
            string name = string.IsNullOrWhiteSpace(history.PlayerNickname) ? fallbackName : history.PlayerNickname;
            StringBuilder text = new StringBuilder();
            text.AppendLine($"**История наказаний — {DiscordLogService.Escape(name)}**");
            text.AppendLine($"**SteamID:** `{DiscordLogService.Escape(PostgreSqlService.NormalizeSteamId(history.PlayerUserId))}`");
            text.AppendLine(history.DiscordUserId == 0 ? "**Discord:** не привязан" : $"**Discord:** <@{history.DiscordUserId}>");
            text.AppendLine($"**Всего записей:** {history.Records.Count}");
            text.AppendLine();
            text.AppendLine(FormatDiscordStatus(history.Records));

            foreach (PunishmentRecord item in history.Records.Take(5))
            {
                long issued = new DateTimeOffset(item.IssuedAtUtc).ToUnixTimeSeconds();
                string icon = item.Type == PunishmentType.Warning ? "⚠️" : item.Type == PunishmentType.Kick ? "🚪" : "🔨";
                text.AppendLine();
                text.AppendLine($"{icon} **#{item.Id} • {TypeName(item.Type)}**");
                text.AppendLine($"**Причина:** {DiscordLogService.Escape(item.Reason)}");
                text.AppendLine($"**Выдано:** <t:{issued}:F> • <t:{issued}:R>");
                text.AppendLine($"**Модератор:** {FormatDiscordModerator(item)}");
                if (item.Type == PunishmentType.Ban)
                {
                    if (item.ExpiresAtUtc.HasValue)
                    {
                        long expires = new DateTimeOffset(item.ExpiresAtUtc.Value).ToUnixTimeSeconds();
                        text.AppendLine($"**Длительность:** {FormatDuration(item.ExpiresAtUtc.Value - item.IssuedAtUtc)}");
                        text.AppendLine($"**Истекает:** <t:{expires}:F> • <t:{expires}:R>");
                        text.AppendLine($"**Статус:** {(item.ExpiresAtUtc.Value > DateTime.UtcNow ? "активен" : "истёк")}");
                    }
                    else
                    {
                        text.AppendLine("**Срок:** бессрочно");
                        text.AppendLine("**Статус:** активен");
                    }
                }
            }
            if (history.Records.Count > 5)
                text.AppendLine($"\nПоказано 5 из {history.Records.Count} записей. Полная история доступна через `/punishments`.");
            return text.ToString().TrimEnd();
        }

        private static string FormatDiscordStatus(IReadOnlyList<PunishmentRecord> records)
        {
            if (records.Count == 0) return "**Без наказаний:** нарушений не зафиксировано";
            PunishmentRecord permanent = records.FirstOrDefault(item => item.Type == PunishmentType.Ban && !item.ExpiresAtUtc.HasValue);
            if (permanent != null) return $"**Активный бан:** #{permanent.Id}\n**Срок:** бессрочно";
            PunishmentRecord latest = records.OrderByDescending(item => item.EffectiveEndUtc.Value).First();
            DateTime end = latest.EffectiveEndUtc.Value;
            long timestamp = new DateTimeOffset(end).ToUnixTimeSeconds();
            if (end > DateTime.UtcNow) return $"**Активный бан:** #{latest.Id}\n**Истекает:** <t:{timestamp}:F> • <t:{timestamp}:R>";
            return $"**Без наказаний:** {FormatElapsed(DateTime.UtcNow - end)}\n**Отсчёт от:** {TypeName(latest.Type).ToLowerInvariant()} #{latest.Id} • <t:{timestamp}:R>";
        }

        private static string FormatDiscordModerator(PunishmentRecord record)
        {
            if (string.Equals(record.ModeratorUserId, "server", StringComparison.OrdinalIgnoreCase)) return "Dedicated Server";
            if (!string.IsNullOrWhiteSpace(record.ModeratorUserId) && record.ModeratorUserId.EndsWith("@discord", StringComparison.OrdinalIgnoreCase))
            {
                string discordId = record.ModeratorUserId.Substring(0, record.ModeratorUserId.Length - 8);
                return $"<@{discordId}> (`{DiscordLogService.Escape(discordId)}`)";
            }
            if (!string.IsNullOrWhiteSpace(record.ModeratorSteamId))
            {
                string nickname = string.IsNullOrWhiteSpace(record.ModeratorNickname) ? "Неизвестный модератор" : DiscordLogService.Escape(record.ModeratorNickname);
                return $"**{nickname}** (`{DiscordLogService.Escape(record.ModeratorSteamId)}`)";
            }
            return $"`{DiscordLogService.Escape(record.ModeratorUserId)}`";
        }

        private static string FormatElapsed(TimeSpan value)
        {
            if (value.TotalDays >= 1) return FormatRussianCount((int)value.TotalDays, "день", "дня", "дней");
            if (value.TotalHours >= 1) return FormatRussianCount((int)value.TotalHours, "час", "часа", "часов");
            if (value.TotalMinutes >= 1) return FormatRussianCount((int)value.TotalMinutes, "минута", "минуты", "минут");
            return "меньше минуты";
        }

        private static string FormatRussianCount(int value, string one, string few, string many)
        {
            int lastTwo = value % 100;
            int last = value % 10;
            string word = lastTwo >= 11 && lastTwo <= 14 ? many : last == 1 ? one : last >= 2 && last <= 4 ? few : many;
            return $"{value} {word}";
        }

        public static string FormatCleanPeriod(IReadOnlyList<PunishmentRecord> records)
        {
            if (records.Count == 0) return "Без наказаний: нарушений не зафиксировано";
            PunishmentRecord permanent = records.FirstOrDefault(item => item.Type == PunishmentType.Ban && !item.ExpiresAtUtc.HasValue);
            if (permanent != null) return $"Активный бан: #{permanent.Id}\nСрок: бессрочно";
            PunishmentRecord latest = records.OrderByDescending(item => item.EffectiveEndUtc.Value).First();
            DateTime effectiveEnd = latest.EffectiveEndUtc.Value;
            if (effectiveEnd > DateTime.UtcNow)
                return $"Активный бан: #{latest.Id}\nИстекает: {effectiveEnd.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
            return $"Без наказаний: {FormatElapsed(DateTime.UtcNow - effectiveEnd)}\nОтсчёт от: {TypeName(latest.Type).ToLowerInvariant()} #{latest.Id} • {effectiveEnd.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
        }

        public static string TypeName(PunishmentType type) => type == PunishmentType.Warning ? "Предупреждение" : type == PunishmentType.Kick ? "Кик" : "Бан";
        private static string FormatModerator(PunishmentRecord record)
        {
            if (string.Equals(record.ModeratorUserId, "server", StringComparison.OrdinalIgnoreCase)) return "Dedicated Server";
            if (!string.IsNullOrWhiteSpace(record.ModeratorSteamId))
                return $"{(string.IsNullOrWhiteSpace(record.ModeratorNickname) ? "Неизвестный модератор" : record.ModeratorNickname)} ({record.ModeratorSteamId})";
            return record.ModeratorUserId;
        }
        public static string NormalizeUserId(string id)
        {
            string normalized = (id ?? string.Empty).Trim();
            if (normalized.EndsWith("@steam", StringComparison.OrdinalIgnoreCase)) return normalized;
            return normalized.All(char.IsDigit) && normalized.Length == 17 ? normalized + "@steam" : normalized;
        }
        public static string NormalizePlayerSelector(string selector) => (selector ?? string.Empty).Trim().Trim('.');
        private static string FormatAgo(TimeSpan value) => value.TotalDays >= 1 ? $"{value.TotalDays:0.#} дн. назад" : value.TotalHours >= 1 ? $"{value.TotalHours:0.#} ч. назад" : $"{Math.Max(0, value.TotalMinutes):0} мин. назад";
        private static string FormatDuration(TimeSpan value) => value.TotalDays >= 1 ? $"{value.TotalDays:0.##} дн." : value.TotalHours >= 1 ? $"{value.TotalHours:0.##} ч." : value.TotalMinutes >= 1 ? $"{value.TotalMinutes:0.##} мин." : $"{Math.Max(0, value.TotalSeconds):0} сек.";
    }
}
