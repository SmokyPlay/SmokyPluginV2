namespace SmokyPluginV2.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    using CommandSystem;

    using Exiled.API.Features;

    using SmokyPluginV2.Warnings;

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class MyWarningsCommand : ICommand
    {
        public string Command => "warns";

        public string[] Aliases => new[] { "mywarns" };

        public string Description => "Показывает ваши предупреждения.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            WarningService service = Plugin.Instance?.WarningService;
            if (service is null || Plugin.Instance?.Config?.Warnings?.IsEnabled != true)
            {
                response = "Система предупреждений отключена.";
                return false;
            }

            Player player = Player.Get(sender);
            if (player is null || !player.IsConnected || player.IsHost)
            {
                response = "Не удалось определить ваш игровой аккаунт. Попробуйте ещё раз после полной авторизации на сервере.";
                return false;
            }

            if (!service.TryGetForPlayer(player.UserId, out IReadOnlyList<WarningRecord> warnings, out response))
                return false;

            if (warnings.Count == 0)
            {
                response = "У вас нет предупреждений.";
                return true;
            }

            StringBuilder text = new StringBuilder();
            text.AppendLine($"Ваши предупреждения: {warnings.Count}");
            foreach (WarningRecord warning in warnings)
            {
                text.AppendLine($"#{warning.Id} | {warning.IssuedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
                text.AppendLine($"Причина: {warning.Reason}");
            }

            response = text.ToString().TrimEnd();
            return true;
        }
    }
}
