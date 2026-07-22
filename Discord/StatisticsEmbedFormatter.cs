namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections.Generic;

    using SmokyPluginV2.Statistics;

    internal static class StatisticsEmbedFormatter
    {
        public static DiscordEmbed Player(PlayerStatisticsRecord stats, string serverName)
        {
            long totalTime = stats.HumanSeconds + stats.ScpSeconds + stats.SpectatorSeconds;
            long totalKills = stats.HumanKillsAsHuman + stats.HumanKillsAsScp + stats.ScpsDestroyed;
            long totalDeaths = stats.HumanDeaths + stats.ScpDeaths;
            List<DiscordEmbedField> fields = new List<DiscordEmbedField>
            {
                Field("📋 Общее",
                    $"**Раундов завершено:** {stats.RoundsCompleted}\n" +
                    $"**Время в игре:** {Duration(totalTime)}\n" +
                    $"↳ За людей: {Duration(stats.HumanSeconds)}\n" +
                    $"↳ За SCP: {Duration(stats.ScpSeconds)}\n" +
                    $"↳ Наблюдателем: {Duration(stats.SpectatorSeconds)}"),
                Field("🏆 Рекорды раунда",
                    $"**Больше всего убито людей за человека:** {stats.BestHumanKillsRound}\n" +
                    $"**Больше всего убито людей за SCP:** {stats.BestScpKillsRound}\n" +
                    $"**Самая долгая жизнь за человека:** {Duration(stats.LongestHumanLifeSeconds)}\n" +
                    $"**Самая долгая жизнь за SCP:** {Duration(stats.LongestScpLifeSeconds)}"),
                Field("⚔️ Убийства",
                    $"**Людей за человека:** {stats.HumanKillsAsHuman}\n" +
                    $"**Людей за SCP:** {stats.HumanKillsAsScp}\n" +
                    $"**Уничтожено объектов SCP:** {stats.ScpsDestroyed}\n" +
                    $"**Всего:** {totalKills}", true),
                Field("💀 Смерти",
                    $"**За человека:** {stats.HumanDeaths}\n" +
                    $"**За SCP:** {stats.ScpDeaths}\n" +
                    $"**Всего:** {totalDeaths}", true),
                Field("\u200B", "\u200B", true),
                Field("🟧 Побеги класса D",
                    $"**Без наручников:** {stats.ClassDEscapesUncuffed}\n" +
                    $"↳ Быстрейший: {Duration(stats.FastestClassDEscapeUncuffedSeconds)}\n" +
                    $"**В наручниках:** {stats.ClassDEscapesCuffed}\n" +
                    $"↳ Быстрейший: {Duration(stats.FastestClassDEscapeCuffedSeconds)}", true),
                Field("🟨 Побеги учёных",
                    $"**Без наручников:** {stats.ScientistEscapesUncuffed}\n" +
                    $"↳ Быстрейший: {Duration(stats.FastestScientistEscapeUncuffedSeconds)}\n" +
                    $"**В наручниках:** {stats.ScientistEscapesCuffed}\n" +
                    $"↳ Быстрейший: {Duration(stats.FastestScientistEscapeCuffedSeconds)}", true),
                Field("\u200B", "\u200B", true),
                Field("🔗 Выведено задержанных",
                    $"**Заключённых класса D:** {stats.ClassDEscorted}\n**Учёных:** {stats.ScientistEscorted}"),
                Field("☢️ Боеголовка",
                    $"**Запущено отсчётов:** {stats.WarheadCountdownsStarted}\n" +
                    $"**Из них сдетонировало:** {stats.WarheadDetonations}\n" +
                    $"**Остановлено детонаций:** {stats.WarheadCountdownsStopped}", true),
                Field("🌀 Карманное измерение",
                    $"**Попаданий:** {stats.PocketEntries}\n" +
                    $"**Побегов:** {stats.PocketEscapes}\n" +
                    $"**Самое долгое нахождение:** {Duration(stats.LongestPocketSeconds)}", true),
                Field("🧪 Прочее",
                    $"**Возрождено игроков, будучи SCP-049:** {stats.ZombiesCreated}\n" +
                    $"**Активировано генераторов полностью:** {stats.GeneratorsActivated}\n" +
                    $"**Запущено перезагрузок:** {stats.SystemRebootsStarted}\n" +
                    $"**Убито теслой за SCP-079:** {stats.TeslaKillsAs079}\n" +
                    $"**Съедено розовых конфет:** {stats.PinkCandiesEaten}"),
            };

            string lastSeen = stats.LastSeenUtc.HasValue
                ? $"<t:{new DateTimeOffset(stats.LastSeenUtc.Value).ToUnixTimeSeconds()}:R>"
                : "неизвестно";
            return new DiscordEmbed
            {
                Title = "📊 Статистика — " + (string.IsNullOrWhiteSpace(stats.Nickname) ? "игрок" : stats.Nickname),
                Description = $"**Steam ID:** `{stats.SteamId}`\n**Последний раз на сервере:** {lastSeen}",
                Color = 0x5865F2,
                Fields = fields.ToArray(),
                Footer = serverName,
            };
        }

        public static DiscordEmbed Server(ServerStatisticsRecord stats)
        {
            string averageRound = stats.RoundsCompleted > 0 ? Duration(stats.TotalRoundSeconds / stats.RoundsCompleted) : "—";
            return new DiscordEmbed
            {
                Title = "📈 Статистика сервера — " + stats.ServerName,
                Color = 0x57F287,
                Fields = new[]
                {
                    Field("📋 Раунды",
                        $"**Завершено:** {stats.RoundsCompleted}\n**Общее время:** {Duration(stats.TotalRoundSeconds)}\n" +
                        $"**Средняя длительность:** {averageRound}\n**Самый долгий:** {Duration(stats.LongestRoundSeconds)}"),
                    Field("🏁 Итоги",
                        $"**Побед SCP:** {stats.ScpWins}\n**Побед Фонда:** {stats.FoundationWins}\n" +
                        $"**Побед Хаоса:** {stats.ChaosWins}\n**Ничьих:** {stats.Draws}"),
                    Field("☢️ Боеголовка",
                        $"**Всего детонаций:** {stats.WarheadDetonations}\n" +
                        $"**Автоматических:** {stats.AutomaticWarheadDetonations}\n**Запущенных игроками:** {stats.PlayerWarheadDetonations}"),
                    Field("🚁 Волны подкреплений",
                        $"**Основных МОГ:** {stats.MtfMainWaves}\n**Дополнительных МОГ:** {stats.MtfReinforcementWaves}\n" +
                        $"**Основных Хаоса:** {stats.ChaosMainWaves}\n**Дополнительных Хаоса:** {stats.ChaosReinforcementWaves}"),
                },
            };
        }

        private static DiscordEmbedField Field(string name, string value, bool inline = false) =>
            new DiscordEmbedField { Name = name, Value = value, Inline = inline };

        private static string Duration(long? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0)
                return "—";
            TimeSpan value = TimeSpan.FromSeconds(seconds.Value);
            List<string> parts = new List<string>();
            if (value.Days > 0) parts.Add(value.Days + " д.");
            if (value.Hours > 0) parts.Add(value.Hours + " ч.");
            if (value.Minutes > 0) parts.Add(value.Minutes + " мин.");
            if (parts.Count == 0 || (value.Days == 0 && parts.Count < 2)) parts.Add(value.Seconds + " сек.");
            return string.Join(" ", parts);
        }
    }
}
