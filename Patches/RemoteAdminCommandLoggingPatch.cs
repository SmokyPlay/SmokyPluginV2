namespace SmokyPluginV2.Patches
{
    using System;
    using System.Linq;

    using Exiled.API.Features;

    using HarmonyLib;

    using RemoteAdmin;

    using SmokyPluginV2.Discord;

    [HarmonyPatch(typeof(CommandProcessor), nameof(CommandProcessor.ProcessQuery), new[] { typeof(string), typeof(CommandSender) })]
    internal static class RemoteAdminCommandLoggingPatch
    {
        [ThreadStatic]
        private static CommandSender currentSender;

        public static CommandSender CurrentSender => currentSender;

        [HarmonyPrefix]
        private static void Prefix(string __0, CommandSender __1)
        {
            string query = __0;
            CommandSender sender = __1;
            currentSender = sender;

            DiscordLogService logs = DiscordLogService.Current;
            if (logs is null || string.IsNullOrWhiteSpace(query) || query.StartsWith("$", StringComparison.Ordinal))
                return;

            string[] parts = query.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string command = parts.Length > 0 ? parts[0] : "unknown";
            string arguments = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "—";

            if (IsSensitive(command))
                arguments = "[аргументы скрыты]";

            Player player = Player.Get(sender);
            string role = player is null ? "сервер/внешний отправитель" : player.Role.Type.ToString();
            string executor = $"**{DiscordLogService.Escape(sender?.Nickname ?? "Dedicated Server")}** (`{DiscordLogService.Escape(sender?.SenderId ?? "server")}`)";

            logs.LogRemoteAdmin(
                $"Remote Admin: {DiscordLogService.Escape(command)}",
                $"**Исполнитель:** {executor}\n**Игровая роль:** `{role}`\n**Команда:** `{DiscordLogService.Escape(command)}`\n**Аргументы:** {DiscordLogService.Escape(arguments)}");
        }

        [HarmonyFinalizer]
        private static void Finalizer() => currentSender = null;

        private static bool IsSensitive(string command) =>
            command.Equals("auth", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("authenticate", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("password", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("token", StringComparison.OrdinalIgnoreCase);
    }
}
