namespace SmokyPluginV2.Commands
{
    using System;
    using System.Linq;
    using System.Text;
    using CommandSystem;
    using Exiled.API.Features;
    using SmokyPluginV2.Database;
    using SmokyPluginV2.Moderation;

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class MyWarningsCommand : ICommand
    {
        public string Command => "warns";
        public string[] Aliases => new[] { "mywarns" };
        public string Description => "Показывает ваши предупреждения.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            PunishmentService service = Plugin.Instance?.Punishments;
            if (service == null) { response = "Система наказаний отключена."; return false; }
            Player player = Player.Get(sender);
            if (player == null || !player.IsConnected || player.IsHost)
            { response = "Не удалось определить ваш игровой аккаунт."; return false; }
            if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(player, out string steamUserId) != true)
            { response = "Для вашего Discord-аккаунта не найдена связка со Steam."; return false; }
            if (!service.TryGetHistory(steamUserId, out PunishmentHistory history, out response)) return false;
            var warnings = history.Records.Where(item => item.Type == PunishmentType.Warning).ToList();
            if (warnings.Count == 0) { response = "У вас нет предупреждений."; return true; }
            StringBuilder text = new StringBuilder($"Ваши предупреждения: {warnings.Count}\n");
            foreach (PunishmentRecord warning in warnings)
                text.AppendLine($"#{warning.Id} | {warning.IssuedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}\nПричина: {warning.Reason}");
            response = text.ToString().TrimEnd();
            return true;
        }
    }
}
