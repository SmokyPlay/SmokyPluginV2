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

    using SmokyPluginV2.Discord;
    using SmokyPluginV2.Warnings;

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class WarnCommand : ICommand
    {
        public string Command => "warn";

        public string[] Aliases => Array.Empty<string>();

        public string Description => "Выдаёт игроку постоянное предупреждение: warn <игрок> <причина>.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(WarningPermissions.Manage))
            {
                response = $"Недостаточно прав. Требуется: {WarningPermissions.Manage}";
                return false;
            }

            WarningService service = Plugin.Instance?.WarningService;
            WarningSettings settings = Plugin.Instance?.Config?.Warnings;
            if (service is null || settings?.IsEnabled != true)
            {
                response = "Система предупреждений отключена.";
                return false;
            }

            if (arguments.Count < 2)
            {
                response = "Использование: warn <игрок> <причина>";
                return false;
            }

            Player target = Player.Get(WarningCommandHelpers.NormalizePlayerSelector(ArgumentAt(arguments, 0)));
            if (target is null || !target.IsConnected || target.IsHost)
            {
                response = "Игрок не найден. Предупреждение можно выдать только игроку, который сейчас находится на сервере.";
                return false;
            }

            if (!WarningCommandHelpers.TryGetModerator(sender, out WarningModerator moderator, out response))
                return false;

            int requiredKickPower = WarningCommandHelpers.GetRequiredKickPower(target);
            if (!moderator.IsServer && moderator.KickPower < requiredKickPower)
            {
                response = $"Недостаточно прав для выдачи предупреждения игроку {target.Nickname}.";
                return false;
            }

            string reason = JoinArguments(arguments, 1).Trim();
            int maxReasonLength = Math.Max(1, settings.MaxReasonLength);
            if (reason.Length > maxReasonLength)
            {
                response = $"Причина слишком длинная. Максимум: {maxReasonLength} символов.";
                return false;
            }

            WarningRecord warning = new WarningRecord
            {
                PlayerUserId = target.UserId,
                PlayerNickname = target.Nickname,
                ModeratorUserId = moderator.UserId,
                ModeratorNickname = moderator.Nickname,
                IssuedAtUtc = DateTime.UtcNow,
                Reason = reason,
            };

            if (!service.TryAdd(warning, out string saveError))
            {
                response = saveError;
                return false;
            }

            if (settings.NotifyPlayer)
            {
                string message = (settings.NotificationMessage ?? string.Empty)
                    .Replace("{id}", warning.Id.ToString())
                    .Replace("{reason}", warning.Reason)
                    .Replace("{moderator}", warning.ModeratorNickname);
                target.Broadcast(settings.NotificationDuration, message, shouldClearPrevious: true);
            }

            WarningCommandHelpers.LogIssued(warning);
            response = $"Игроку {target.Nickname} ({target.UserId}) выдано предупреждение #{warning.Id}.";
            return true;
        }

        private static string ArgumentAt(ArraySegment<string> arguments, int index) => arguments.Array[arguments.Offset + index];

        private static string JoinArguments(ArraySegment<string> arguments, int startIndex) =>
            string.Join(" ", arguments.Skip(startIndex));
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class WarningsCommand : ICommand
    {
        public string Command => "warnings";

        public string[] Aliases => new[] { "warns" };

        public string Description => "Показывает предупреждения: warnings <игрок|UserId>.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(WarningPermissions.Manage))
            {
                response = $"Недостаточно прав. Требуется: {WarningPermissions.Manage}";
                return false;
            }

            if (!WarningCommandHelpers.TryGetService(arguments, out WarningService service, out string userId, out string displayName, out response))
                return false;

            if (!service.TryGetForPlayer(userId, out IReadOnlyList<WarningRecord> warnings, out response))
                return false;

            response = WarningCommandHelpers.FormatWarnings(displayName, userId, warnings);
            return true;
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class UnwarnCommand : ICommand
    {
        public string Command => "delwarn";

        public string[] Aliases => new[] { "unwarn", "rmwarn" };

        public string Description => "Удаляет предупреждение: delwarn <ID>.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(WarningPermissions.Manage))
            {
                response = $"Недостаточно прав. Требуется: {WarningPermissions.Manage}";
                return false;
            }

            WarningService service = Plugin.Instance?.WarningService;
            WarningSettings settings = Plugin.Instance?.Config?.Warnings;
            if (service is null || settings?.IsEnabled != true)
            {
                response = "Система предупреждений отключена.";
                return false;
            }

            if (arguments.Count != 1 || !long.TryParse(arguments.Array[arguments.Offset], out long warningId) || warningId <= 0)
            {
                response = "Использование: delwarn <ID>";
                return false;
            }

            if (!service.TryGet(warningId, out WarningRecord existing, out response))
                return false;

            if (existing is null)
            {
                response = $"Предупреждение #{warningId} не найдено.";
                return false;
            }

            if (!WarningCommandHelpers.TryGetModerator(sender, out WarningModerator moderator, out response))
                return false;

            Player onlineTarget = Player.Get(existing.PlayerUserId);
            int requiredKickPower = onlineTarget != null && onlineTarget.IsConnected
                ? WarningCommandHelpers.GetRequiredKickPower(onlineTarget)
                : WarningCommandHelpers.GetRequiredKickPower(existing.PlayerUserId);

            if (!moderator.IsServer && moderator.KickPower < requiredKickPower)
            {
                response = "Недостаточно прав для удаления этого предупреждения.";
                return false;
            }

            if (!service.TryDelete(warningId, out WarningRecord deleted, out string error))
            {
                response = error;
                return false;
            }

            WarningCommandHelpers.LogDeleted(deleted, moderator);
            response = $"Предупреждение #{warningId} игрока {deleted.PlayerNickname} удалено.";
            return true;
        }
    }

    internal static class WarningCommandHelpers
    {
        public static bool TryGetModerator(ICommandSender sender, out WarningModerator moderator, out string error)
        {
            CommandSender commandSender = sender as CommandSender;
            bool isServer = commandSender is ServerConsoleSender || commandSender == Server.Host.Sender;

            if (isServer)
            {
                moderator = new WarningModerator
                {
                    UserId = "server",
                    Nickname = "Dedicated Server",
                    KickPower = byte.MaxValue,
                    IsServer = true,
                };
                error = null;
                return true;
            }

            if (commandSender is null || string.IsNullOrWhiteSpace(commandSender.SenderId))
            {
                moderator = null;
                error = "Не удалось определить отправителя команды.";
                return false;
            }

            moderator = new WarningModerator
            {
                UserId = commandSender.SenderId,
                Nickname = commandSender.Nickname,
                KickPower = commandSender.KickPower,
                IsServer = false,
            };
            error = null;
            return true;
        }

        public static int GetRequiredKickPower(Player target)
        {
            int requiredKickPower = target.Group?.RequiredKickPower ?? 0;
            return Math.Max(target.KickPower, requiredKickPower);
        }

        public static int GetRequiredKickPower(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId) || Server.PermissionsHandler is null)
                return 0;

            if (!Server.PermissionsHandler.Members.TryGetValue(userId, out string groupName) ||
                !Server.PermissionsHandler.Groups.TryGetValue(groupName, out UserGroup group))
                return 0;

            return Math.Max(group.KickPower, group.RequiredKickPower);
        }

        public static bool TryGetService(ArraySegment<string> arguments, out WarningService service, out string userId, out string displayName, out string error)
        {
            service = Plugin.Instance?.WarningService;
            userId = null;
            displayName = null;

            if (service is null || Plugin.Instance?.Config?.Warnings?.IsEnabled != true)
            {
                error = "Система предупреждений отключена.";
                return false;
            }

            if (arguments.Count < 1)
            {
                error = "Укажите игрока или его UserId.";
                return false;
            }

            string query = NormalizePlayerSelector(arguments.Array[arguments.Offset]);
            Player player = Player.Get(query);
            if (player != null && player.IsConnected && !player.IsHost)
            {
                userId = player.UserId;
                displayName = player.Nickname;
                error = null;
                return true;
            }

            userId = NormalizeUserId(query);
            displayName = "офлайн-игрок";
            error = null;
            return true;
        }

        public static string FormatWarnings(string displayName, string userId, IReadOnlyList<WarningRecord> warnings)
        {
            if (warnings.Count == 0)
                return $"У игрока {displayName} ({userId}) нет предупреждений.";

            const int limit = 15;
            StringBuilder response = new StringBuilder();
            response.AppendLine($"Предупреждения {displayName} ({userId}):");

            foreach (WarningRecord warning in warnings.Take(limit))
                response.AppendLine($"#{warning.Id} {warning.IssuedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} — {warning.Reason} — {warning.ModeratorNickname} ({warning.ModeratorUserId})");

            if (warnings.Count > limit)
                response.AppendLine($"Показано {limit} из {warnings.Count} записей.");

            return response.ToString().TrimEnd();
        }

        public static void LogIssued(WarningRecord warning)
        {
            QueueModerationLog(
                "Предупреждение игроку",
                $"**Игрок:** **{DiscordLogService.Escape(warning.PlayerNickname)}** (`{DiscordLogService.Escape(warning.PlayerUserId)}`)\n" +
                $"**Модератор:** **{DiscordLogService.Escape(warning.ModeratorNickname)}** (`{DiscordLogService.Escape(warning.ModeratorUserId)}`)\n" +
                $"**Warning ID:** `#{warning.Id}`\n" +
                $"**Причина:** {DiscordLogService.Escape(warning.Reason)}");
        }

        public static void LogDeleted(WarningRecord warning, WarningModerator moderator)
        {
            QueueModerationLog(
                "Предупреждение удалено",
                $"**Игрок:** **{DiscordLogService.Escape(warning.PlayerNickname)}** (`{DiscordLogService.Escape(warning.PlayerUserId)}`)\n" +
                $"**Модератор:** **{DiscordLogService.Escape(moderator.Nickname)}** (`{DiscordLogService.Escape(moderator.UserId)}`)\n" +
                $"**Warning ID:** `#{warning.Id}`\n" +
                $"**Причина предупреждения:** {DiscordLogService.Escape(warning.Reason)}",
                false);
        }

        private static void QueueModerationLog(string title, string description, bool isPunishment = true)
        {
            DiscordLogService logs = Plugin.Instance?.DiscordLogs ?? DiscordLogService.Current;
            if (logs is null)
            {
                Log.Warn($"[Warnings] Discord moderation log '{title}' was not queued because the Discord bot is not running.");
                return;
            }

            if (!logs.LogModeration(title, description, isPunishment))
                Log.Warn($"[Warnings] Discord moderation log '{title}' was not queued because discord.moderation_channel_id is 0.");
        }

        private static string NormalizeUserId(string userId)
        {
            string normalized = (userId ?? string.Empty).Trim();
            if (normalized.IndexOf('@') < 0 && normalized.All(char.IsDigit) && normalized.Length >= 16)
                normalized += "@steam";

            return normalized;
        }

        public static string NormalizePlayerSelector(string selector) =>
            (selector ?? string.Empty).Trim().Trim('.');
    }

    internal static class WarningPermissions
    {
        public const string Manage = "smokyplugin.warnings";
    }
}
