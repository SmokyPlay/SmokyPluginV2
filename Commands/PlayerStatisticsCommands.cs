namespace SmokyPluginV2.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    using CommandSystem;

    using Exiled.API.Features;

    using SmokyPluginV2.Database;
    using SmokyPluginV2.Statistics;

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class PlayerStatisticsCommand : ICommand
    {
        public string Command => "stats";

        public string[] Aliases => Array.Empty<string>();

        public string Description => "Показывает статистику: .stats [никнейм, часть никнейма или SteamID64].";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!TryGetServices(out StatisticsService statistics, out PostgreSqlService database, out response))
                return false;
            if (!TryGetRequester(sender, out Player requester, out string requesterSteamId, out response))
                return false;

            Player target = requester;
            string targetSteamId = requesterSteamId;
            if (arguments.Count > 0)
            {
                string selector = string.Join(" ", arguments.Array, arguments.Offset, arguments.Count).Trim();
                if (SteamIdCommandParser.TryParse(selector, out string parsedSteamUserId))
                {
                    targetSteamId = PostgreSqlService.NormalizeSteamId(parsedSteamUserId);
                    target = FindOnlinePlayerBySteamId(targetSteamId);
                }
                else
                {
                    if (!TryFindOnlinePlayer(selector, out target, out response))
                        return false;
                    if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(target, out targetSteamId) != true)
                    {
                        response = "Для Discord-аккаунта выбранного игрока не найдена связка со Steam.";
                        return false;
                    }
                }
            }

            if (!statistics.TryFlushPendingWrites(out response))
                return false;
            if (!database.TryGetPlayerStatistics(targetSteamId, out PlayerStatisticsRecord record, out response))
                return false;
            if (record == null)
            {
                string missingTarget = target != null
                    ? DisplayNickname(target)
                    : PostgreSqlService.NormalizeSteamId(targetSteamId);
                response = $"Для игрока {missingTarget} статистика ещё не записана.";
                return false;
            }

            bool isOwner = string.Equals(
                PostgreSqlService.NormalizeSteamId(requesterSteamId),
                PostgreSqlService.NormalizeSteamId(targetSteamId),
                StringComparison.Ordinal);
            if (record.StatisticsPrivate && !isOwner)
            {
                response = "Этот игрок закрыл доступ к своей статистике.";
                return false;
            }

            statistics.ApplyLivePlayerStatistics(targetSteamId, record);
            long livePlaytimeSeconds = target != null
                ? statistics.GetUnpersistedPlaytimeSeconds(target.UserId)
                : 0;
            string displayName = target != null
                ? DisplayNickname(target)
                : Sanitize(string.IsNullOrWhiteSpace(record.Nickname)
                    ? PostgreSqlService.NormalizeSteamId(targetSteamId)
                    : record.Nickname.Trim());
            response = CompactStatisticsFormatter.Build(record, displayName, livePlaytimeSeconds);
            return true;
        }

        internal static bool TryGetServices(
            out StatisticsService statistics,
            out PostgreSqlService database,
            out string error)
        {
            statistics = Plugin.Instance?.Statistics;
            database = Plugin.Instance?.Database;
            if (statistics == null || database == null || Plugin.Instance?.Config?.Statistics?.IsEnabled != true)
            {
                error = "Система статистики отключена или PostgreSQL недоступна.";
                return false;
            }

            error = null;
            return true;
        }

        internal static bool TryGetRequester(
            ICommandSender sender,
            out Player player,
            out string steamUserId,
            out string error)
        {
            player = Player.Get(sender);
            steamUserId = null;
            if (player == null || !player.IsConnected || player.IsHost || player.IsNPC)
            {
                error = "Не удалось определить ваш игровой аккаунт.";
                return false;
            }

            if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(player, out steamUserId) != true)
            {
                error = "Для вашего Discord-аккаунта не найдена связка со Steam.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryFindOnlinePlayer(string selector, out Player player, out string error)
        {
            player = null;
            if (string.IsNullOrWhiteSpace(selector))
            {
                error = "Укажите никнейм или его часть после .stats.";
                return false;
            }

            List<Player> players = Player.List
                .Where(candidate =>
                    candidate != null &&
                    candidate.IsConnected &&
                    !candidate.IsHost &&
                    !candidate.IsNPC &&
                    !string.IsNullOrWhiteSpace(candidate.Nickname))
                .ToList();
            List<Player> exact = players
                .Where(candidate => string.Equals(candidate.Nickname.Trim(), selector, StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<Player> matches = exact.Count > 0
                ? exact
                : players
                    .Where(candidate => candidate.Nickname.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            if (matches.Count == 0)
            {
                error = $"На сервере не найден игрок, в никнейме которого есть «{Sanitize(selector)}».";
                return false;
            }

            if (matches.Count > 1)
            {
                string names = string.Join(", ", matches
                    .Take(8)
                    .Select(DisplayNickname));
                if (matches.Count > 8)
                    names += $" и ещё {matches.Count - 8}";
                error = "Найдено несколько игроков: " + names + ". Уточните никнейм.";
                return false;
            }

            player = matches[0];
            error = null;
            return true;
        }

        private static Player FindOnlinePlayerBySteamId(string steamUserId)
        {
            string normalizedSteamId = PostgreSqlService.NormalizeSteamId(steamUserId);
            foreach (Player candidate in Player.List)
            {
                if (candidate == null || !candidate.IsConnected || candidate.IsHost || candidate.IsNPC)
                    continue;
                if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(candidate, out string candidateSteamId) == true &&
                    string.Equals(
                        PostgreSqlService.NormalizeSteamId(candidateSteamId),
                        normalizedSteamId,
                        StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string DisplayNickname(Player player) =>
            Sanitize(string.IsNullOrWhiteSpace(player?.Nickname) ? "игрок" : player.Nickname.Trim());

        private static string Sanitize(string value) =>
            (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
    }

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class StatisticsPrivacyCommand : ICommand
    {
        public string Command => "statsprivacy";

        public string[] Aliases => new[] { "sp" };

        public string Description => "Переключает доступ других пользователей к вашей статистике: .statsprivacy / .sp.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (arguments.Count != 0)
            {
                response = "Использование: .statsprivacy или .sp";
                return false;
            }

            if (!PlayerStatisticsCommand.TryGetServices(out StatisticsService statistics, out PostgreSqlService database, out response))
                return false;
            if (!PlayerStatisticsCommand.TryGetRequester(sender, out Player _, out string steamUserId, out response))
                return false;
            if (!statistics.TryFlushPendingWrites(out response))
                return false;
            if (!database.TryToggleStatisticsPrivacy(steamUserId, out bool isPrivate, out bool playerExists, out response))
                return false;
            if (!playerExists)
            {
                response = "Ваш профиль статистики ещё не создан. Сыграйте на сервере хотя бы несколько секунд и повторите команду.";
                return false;
            }

            response = isPrivate
                ? "Ваша статистика теперь закрыта. Другие игроки не смогут посмотреть её через .stats, но вы по-прежнему сможете просматривать её самостоятельно. Чтобы снова открыть статистику, повторно введите .statsprivacy или .sp."
                : "Ваша статистика теперь открыта и доступна другим игрокам через .stats.";
            return true;
        }
    }

    internal static class CompactStatisticsFormatter
    {
        public static string Build(PlayerStatisticsRecord stats, string nickname, long livePlaytimeSeconds)
        {
            long totalKills = stats.HumanKillsAsHuman + stats.HumanKillsAsScp + stats.ScpsDestroyed;
            long totalDeaths = stats.HumanDeaths + stats.ScpDeaths;
            long totalPlaytime = stats.HumanSeconds + stats.ScpSeconds + stats.SpectatorSeconds + Math.Max(0, livePlaytimeSeconds);
            string ratio = totalDeaths > 0
                ? ((double)totalKills / totalDeaths).ToString("0.00", CultureInfo.InvariantCulture)
                : totalKills > 0 ? "∞" : "—";

            StringBuilder result = new StringBuilder();
            result.Append("=== СТАТИСТИКА — ").Append(nickname).AppendLine(" ===");
            result.AppendLine();
            result.AppendLine("ОБЩЕЕ");
            Append(result, "Игровое время", Duration(totalPlaytime));
            Append(result, "Сыграно раундов", stats.RoundsCompleted);
            result.AppendLine();
            result.AppendLine("БОЙ");
            Append(result, "Убийства", totalKills);
            result.Append("За человека: ").Append(stats.HumanKillsAsHuman)
                .Append(" · За SCP: ").Append(stats.HumanKillsAsScp)
                .Append(" · Уничтожено SCP: ").AppendLine(stats.ScpsDestroyed.ToString(CultureInfo.InvariantCulture));
            Append(result, "Смерти", totalDeaths);
            Append(result, "Соотношение убийств/смертей", ratio);
            result.AppendLine();
            result.AppendLine("ЛИЧНЫЕ РЕКОРДЫ");
            result.Append("Убийств за раунд: за человека — ").Append(stats.BestHumanKillsRound)
                .Append(" · за SCP — ").AppendLine(stats.BestScpKillsRound.ToString(CultureInfo.InvariantCulture));
            result.Append("Самая долгая жизнь: за человека — ").Append(Duration(stats.LongestHumanLifeSeconds))
                .Append(" · за SCP — ").AppendLine(Duration(stats.LongestScpLifeSeconds));
            result.Append("Быстрейший побег: за класс D — ").Append(Duration(stats.FastestClassDEscapeUncuffedSeconds))
                .Append(" · за учёного — ").AppendLine(Duration(stats.FastestScientistEscapeUncuffedSeconds));
            Append(result, "Лучший результат в змейке", stats.BestSnakeScore);
            result.AppendLine();
            result.AppendLine("ОСОБЫЕ ДОСТИЖЕНИЯ");
            Append(result, "Побегов из измерения", stats.PocketEscapes);
            Append(result, "Эвакуировано связанных", stats.ClassDEscorted + stats.ScientistEscorted);
            Append(result, "Возрождено игроков за SCP-049", stats.ZombiesCreated);
            Append(result, "Взорвано боеголовок", stats.WarheadDetonations);
            Append(result, "Активировано генераторов", stats.GeneratorsActivated);
            Append(result, "Съедено розовых конфет", stats.PinkCandiesEaten);
            result.AppendLine();
            result.AppendLine("Статистику другого игрока можно посмотреть командой .stats НИК или .stats STEAMID64.");
            result.AppendLine("Закрыть или открыть свою статистику можно командой .statsprivacy или .sp.");
            result.AppendLine("Полная статистика доступна на нашем Discord-сервере.");
            result.Append("Ссылка — в информации о сервере.");
            return result.ToString();
        }

        private static void Append(StringBuilder builder, string label, object value) =>
            builder.Append(label)
                .Append(": ")
                .AppendLine(Convert.ToString(value, CultureInfo.InvariantCulture));

        private static string Duration(long? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0)
                return "—";

            TimeSpan value = TimeSpan.FromSeconds(seconds.Value);
            List<string> parts = new List<string>();
            if (value.Days > 0)
                parts.Add(value.Days + " д");
            if (value.Hours > 0)
                parts.Add(value.Hours + " ч");
            if (value.Minutes > 0)
                parts.Add(value.Minutes + " мин");
            if (parts.Count == 0 || (value.Days == 0 && parts.Count < 2))
                parts.Add(value.Seconds + " с");
            return string.Join(" ", parts);
        }
    }
}
