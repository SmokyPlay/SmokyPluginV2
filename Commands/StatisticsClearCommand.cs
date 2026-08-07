namespace SmokyPluginV2.Commands
{
    using System;
    using System.Linq;

    using CommandSystem;

    using Exiled.API.Features;
    using Exiled.Permissions.Extensions;

    using SmokyPluginV2.Database;
    using SmokyPluginV2.Statistics;

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class StatisticsClearCommand : ICommand
    {
        private const string Permission = "smokyplugin.statistics.clear";

        public string Command => "clearstats";

        public string[] Aliases => new[] { "resetstats", "cs" };

        public string Description => "Очищает статистику игрока на текущем сервере: clearstats [игровой ID или SteamID64].";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            StatisticsSettings settings = Plugin.Instance?.Config?.Statistics;
            if (!sender.CheckPermission(Permission))
            {
                response = $"Недостаточно прав. Требуется: {Permission}";
                return false;
            }

            StatisticsService statistics = Plugin.Instance?.Statistics;
            if (statistics is null || settings?.IsEnabled != true || Plugin.Instance?.Database is null)
            {
                response = "Система статистики отключена или PostgreSQL недоступна.";
                return false;
            }

            if (arguments.Count != 1)
            {
                response = "Использование: clearstats [игровой ID или SteamID64]";
                return false;
            }

            string selector = (arguments.Array[arguments.Offset] ?? string.Empty).Trim().Trim('.');
            if (!TryResolvePlayer(selector, out string userId, out Player onlinePlayer, out response))
                return false;
            if (onlinePlayer != null &&
                Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(onlinePlayer, out userId) != true)
            {
                response = "Для Discord-аккаунта игрока не найдена связка со Steam.";
                return false;
            }
            if (!PostgreSqlService.IsSteamUserId(userId))
            {
                response = "Статистику пока можно очищать только для Steam-профиля.";
                return false;
            }

            if (!statistics.TryClearPlayerStatistics(userId, onlinePlayer, out bool existed, out string error))
            {
                response = error;
                return false;
            }

            if (!existed)
            {
                response = $"Для игрока `{PostgreSqlService.NormalizeSteamId(userId)}` на этом сервере сохранённая статистика не найдена.";
                return false;
            }

            string playerText = onlinePlayer != null
                ? $"{onlinePlayer.Nickname} ({PostgreSqlService.NormalizeSteamId(userId)})"
                : PostgreSqlService.NormalizeSteamId(userId);
            response = $"Статистика игрока {playerText} на этом сервере полностью очищена. Новые события будут записываться с этого момента.";
            return true;
        }

        private static bool TryResolvePlayer(string selector, out string userId, out Player onlinePlayer, out string error)
        {
            userId = null;
            onlinePlayer = null;
            error = null;

            if (SteamIdCommandParser.TryParse(selector, out userId))
            {
                onlinePlayer = Player.Get(userId);
                return true;
            }

            if (!int.TryParse(selector, out int playerId) || playerId <= 0)
            {
                error = "Укажите игровой ID игрока, который сейчас находится на сервере, или корректный 17-значный SteamID64.";
                return false;
            }

            onlinePlayer = Player.List.FirstOrDefault(player => player != null && player.IsConnected && !player.IsHost && player.Id == playerId);
            if (onlinePlayer is null)
            {
                error = $"Игрок с игровым ID {playerId} сейчас не находится на сервере. Для офлайн-игрока укажите SteamID64.";
                return false;
            }

            userId = onlinePlayer.UserId;
            return true;
        }
    }
}
