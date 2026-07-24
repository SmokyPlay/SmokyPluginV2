namespace SmokyPluginV2.Commands
{
    using System;
    using System.Globalization;
    using System.Linq;

    using CommandSystem;

    using Exiled.API.Features;
    using Exiled.Permissions.Extensions;

    using SmokyPluginV2.RolePreferences;

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class RolePriorityCommand : ICommand
    {
        private const string Permission = "smokyplugin.roleweight";

        public string Command => "roleweight";

        public string[] Aliases => new[] { "setweight", "rw" };

        public string Description => "Временно задаёт вес игрока при распределении ролей в текущем лобби: roleweight [ID] [вес].";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(Permission))
            {
                response = $"Недостаточно прав. Требуется: {Permission}";
                return false;
            }

            if (arguments.Count != 2)
            {
                response = "Использование: roleweight [внутренний ID игрока] [вес]";
                return false;
            }

            string idText = (arguments.Array[arguments.Offset] ?? string.Empty).Trim().Trim('.');
            if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out int playerId) || playerId <= 0)
            {
                response = "Внутренний ID игрока должен быть положительным целым числом.";
                return false;
            }

            string weightText = (arguments.Array[arguments.Offset + 1] ?? string.Empty).Trim();
            if (!TryParseWeight(weightText, out double weight) || weight <= 0 || double.IsNaN(weight) || double.IsInfinity(weight))
            {
                response = "Вес должен быть конечным числом больше нуля. Пример: roleweight 12 2.5";
                return false;
            }

            Player player = Player.List.FirstOrDefault(candidate =>
                candidate != null &&
                candidate.IsConnected &&
                !candidate.IsHost &&
                candidate.Id == playerId);
            if (player is null)
            {
                response = $"Игрок с внутренним ID {playerId} сейчас не находится на сервере.";
                return false;
            }

            RolePreferenceService service = Plugin.Instance?.RolePreferences;
            if (service is null)
            {
                response = "Система распределения ролей отключена.";
                return false;
            }

            if (!service.TrySetLobbyWeight(player, weight, out double previousWeight, out response))
                return false;

            Log.Info($"[Role Preferences] {sender.LogName} set lobby-only weight {weight:0.##} for {player.Nickname} ({player.UserId}, ID {player.Id}); previous effective weight: {previousWeight:0.##}.");
            response = $"Для игрока {player.Nickname} (ID {player.Id}) установлен временный вес {weight:0.##} вместо {previousWeight:0.##}. Он действует только в текущем лобби.";
            return true;
        }

        private static bool TryParseWeight(string value, out double weight)
        {
            const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
            return double.TryParse(value, styles, CultureInfo.InvariantCulture, out weight) ||
                double.TryParse(value, styles, CultureInfo.CurrentCulture, out weight);
        }
    }
}
