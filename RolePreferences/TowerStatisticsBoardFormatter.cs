namespace SmokyPluginV2.RolePreferences
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    using SmokyPluginV2.Statistics;

    internal static class TowerStatisticsBoardFormatter
    {
        public const int PersonalPageCount = 3;

        private const string Accent = "#9718D4";
        private const string ValueAccent = "#C45CFF";
        private const string Text = "#F0ECF2";
        private const string Muted = "#B8B0BC";

        public static string LoadingPersonal(int page) => Build(
            PersonalTitle(page),
            CenteredState("Загрузка статистики...", 17),
            false,
            page);

        public static string PersonalUnavailable(int page, string message) => Build(
            PersonalTitle(page),
            CenteredState(Escape(message), 16),
            false,
            page);

        public static string ServerLoading() => Build(
            "СТАТИСТИКА СЕРВЕРА",
            CenteredState("Загрузка статистики...", 17),
            true,
            0);

        public static string ServerUnavailable(string message) => Build(
            "СТАТИСТИКА СЕРВЕРА",
            CenteredState(Escape(message), 16),
            true,
            0);

        public static string Personal(PlayerStatisticsRecord stats, int requestedPage)
        {
            if (stats is null)
                return PersonalUnavailable(requestedPage, "Статистика пока отсутствует");

            int page = NormalizePage(requestedPage);
            switch (page)
            {
                case 1:
                    return BuildPersonalRecords(stats);
                case 2:
                    return BuildPersonalSpecial(stats);
                default:
                    return BuildPersonalSummary(stats);
            }
        }

        public static string Server(ServerStatisticsRecord stats)
        {
            if (stats is null)
                return ServerUnavailable("Статистика пока отсутствует");

            long totalOutcomes = stats.RoundsCompleted;
            StringBuilder body = new StringBuilder();
            AppendSubheading(body, "РАУНДЫ");
            AppendRow(body, "Сыграно раундов", stats.RoundsCompleted);
            AppendRow(body, "Общее время раундов", Duration(stats.TotalRoundSeconds));
            AppendRow(body, "Самый долгий раунд", Duration(stats.LongestRoundSeconds));
            AppendSpace(body);
            AppendSubheading(body, "ПОБЕДЫ");
            AppendRow(body, "МОГ", Outcome(stats.FoundationWins, totalOutcomes));
            AppendRow(body, "Хаос", Outcome(stats.ChaosWins, totalOutcomes));
            AppendRow(body, "SCP", Outcome(stats.ScpWins, totalOutcomes));
            AppendRow(body, "Ничья", Outcome(stats.Draws, totalOutcomes));
            return Build("СТАТИСТИКА СЕРВЕРА", body.ToString(), true, 0);
        }

        public static int NormalizePage(int page)
        {
            int normalized = page % PersonalPageCount;
            return normalized < 0 ? normalized + PersonalPageCount : normalized;
        }

        private static string BuildPersonalSummary(PlayerStatisticsRecord stats)
        {
            long totalPlaytime = stats.HumanSeconds + stats.ScpSeconds + stats.SpectatorSeconds;
            long totalKills = stats.HumanKillsAsHuman + stats.HumanKillsAsScp + stats.ScpsDestroyed;
            long totalDeaths = stats.HumanDeaths + stats.ScpDeaths;
            string ratio = totalDeaths > 0
                ? ((double)totalKills / totalDeaths).ToString("0.00", CultureInfo.InvariantCulture)
                : totalKills > 0 ? "∞" : "—";

            StringBuilder body = new StringBuilder();
            AppendSubheading(body, "ОБЩЕЕ");
            AppendRow(body, "Игровое время", Duration(totalPlaytime));
            AppendRow(body, "Сыграно раундов", stats.RoundsCompleted);
            AppendSubheading(body, "УБИЙСТВА");
            AppendRow(body, "Людей за человека", stats.HumanKillsAsHuman);
            AppendRow(body, "Людей за SCP", stats.HumanKillsAsScp);
            AppendRow(body, "Уничтожено объектов SCP", stats.ScpsDestroyed);
            AppendSubheading(body, "СМЕРТИ");
            AppendRow(body, "За человека", stats.HumanDeaths);
            AppendRow(body, "За SCP", stats.ScpDeaths);
            AppendSpace(body);
            AppendRow(body, "Соотношение убийств/смертей", ratio);
            return Build("ВАША СТАТИСТИКА", body.ToString(), false, 0);
        }

        private static string BuildPersonalRecords(PlayerStatisticsRecord stats)
        {
            StringBuilder body = new StringBuilder();
            AppendSubheading(body, "УБИЙСТВ ЗА РАУНД");
            AppendRow(body, "За человека", stats.BestHumanKillsRound);
            AppendRow(body, "За SCP", stats.BestScpKillsRound);
            AppendSubheading(body, "САМАЯ ДОЛГАЯ ЖИЗНЬ");
            AppendRow(body, "За человека", Duration(stats.LongestHumanLifeSeconds));
            AppendRow(body, "За SCP", Duration(stats.LongestScpLifeSeconds));
            AppendSubheading(body, "БЫСТРЕЙШИЙ ПОБЕГ");
            AppendRow(body, "За класс D", Duration(stats.FastestClassDEscapeUncuffedSeconds));
            AppendRow(body, "За учёного", Duration(stats.FastestScientistEscapeUncuffedSeconds));
            AppendSpace(body);
            AppendRow(body, "Лучший результат в змейке", stats.BestSnakeScore);
            return Build("ЛИЧНЫЕ РЕКОРДЫ", body.ToString(), false, 1);
        }

        private static string BuildPersonalSpecial(PlayerStatisticsRecord stats)
        {
            StringBuilder body = new StringBuilder();
            AppendSubheading(body, "БОЕГОЛОВКА");
            AppendRow(body, "Запущено", stats.WarheadCountdownsStarted);
            AppendRow(body, "Сдетонировало", stats.WarheadDetonations);
            AppendSubheading(body, "КАРМАННОЕ ИЗМЕРЕНИЕ");
            AppendRow(body, "Попаданий в измерение", stats.PocketEntries);
            AppendRow(body, "Побегов из измерения", stats.PocketEscapes);
            AppendSubheading(body, "ПРОЧЕЕ");
            AppendCompactRow(body, "Возрождено игроков, будучи SCP-049", stats.ZombiesCreated);
            AppendRow(body, "Эвакуировано связанных", stats.ClassDEscorted + stats.ScientistEscorted);
            AppendRow(body, "Активировано генераторов", stats.GeneratorsActivated);
            AppendRow(body, "Убийств Tesla за SCP-079", stats.TeslaKillsAs079);
            AppendRow(body, "Съедено розовых конфет", stats.PinkCandiesEaten);
            return Build("ОСОБЫЕ ДЕЙСТВИЯ", body.ToString(), false, 2);
        }

        private static string Build(string title, string body, bool serverMode, int page)
        {
            string section = serverMode
                ? "СЕРВЕР"
                : $"ЛИЧНАЯ {NormalizePage(page) + 1}/{PersonalPageCount}";

            return
                $"<align=center><size=19><color={Accent}><b>SITE-02</b></color></size>\n" +
                $"<size=17><color={Text}><b>{title}</b></color></size>\n" +
                $"<size=10><color={Accent}>━━━━━━━━━━━━━━━━━━━━━━━━━━━━</color></size>\n" +
                $"<size=11><color={Muted}><b>{section}</b></color></size></align>\n" +
                $"<align=left><size=13><color={Text}><line-height=116%>{body}</line-height></color></size></align>\n" +
                $"<align=center><size=12><color={Muted}>Полная статистика доступна на нашем <color={ValueAccent}><b>Discord-сервере</b></color>\n" +
                "Ссылка — в информации о сервере</color></size></align>";
        }

        private static string PersonalTitle(int page)
        {
            switch (NormalizePage(page))
            {
                case 1: return "ЛИЧНЫЕ РЕКОРДЫ";
                case 2: return "ОСОБЫЕ ДЕЙСТВИЯ";
                default: return "ВАША СТАТИСТИКА";
            }
        }

        private static string CenteredState(string message, int size) =>
            $"<align=center><size=13>\n\n\n\n</size>" +
            $"<size={size}><color={Muted}>{message}</color></size>" +
            "<size=13>\n\n\n\n\n</size></align>";

        private static void AppendSubheading(StringBuilder builder, string title)
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append("<pos=12%><color=").Append(ValueAccent).Append("><b>").Append(title).Append("</b></color>\n");
        }

        private static void AppendSpace(StringBuilder builder) => builder.Append('\n');

        private static void AppendRow(StringBuilder builder, string label, object value)
        {
            builder.Append("<align=left><nobr><pos=12%>")
                .Append(Escape(label))
                .Append("</nobr></align><line-height=0>\n<align=right><margin-right=12%><nobr><color=")
                .Append(ValueAccent)
                .Append("><b>")
                .Append(Escape(Convert.ToString(value, CultureInfo.InvariantCulture)))
                .Append("</b></color></nobr></margin></align><line-height=116%>\n");
        }

        private static void AppendCompactRow(StringBuilder builder, string label, object value)
        {
            builder.Append("<align=left><nobr><pos=12%><size=11>")
                .Append(Escape(label))
                .Append("</size></nobr></align><line-height=0>\n<align=right><margin-right=12%><nobr><color=")
                .Append(ValueAccent)
                .Append("><b>")
                .Append(Escape(Convert.ToString(value, CultureInfo.InvariantCulture)))
                .Append("</b></color></nobr></margin></align><line-height=116%>\n");
        }

        private static string Outcome(long value, long rounds)
        {
            double percent = rounds > 0 ? value * 100d / rounds : 0d;
            return $"{value} · {percent:0}%";
        }

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

        private static string Escape(string value) => (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
