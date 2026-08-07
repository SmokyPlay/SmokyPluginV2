namespace SmokyPluginV2.RolePreferences
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    using SmokyPluginV2.Statistics;

    internal static class TowerLeaderboardBoardFormatter
    {
        public const int PageCount = 5;

        private const string Accent = "#9718D4";
        private const string ValueAccent = "#C45CFF";
        private const string Text = "#F0ECF2";
        private const string Muted = "#B8B0BC";
        private const string Gold = "#FFD76A";
        private const string Silver = "#9FCBFF";
        private const string Bronze = "#E0915C";
        private const string ClassD = "#FF7A18";
        private const string Scientist = "#D8B84A";

        public static int NormalizePage(int page)
        {
            int normalized = page % PageCount;
            return normalized < 0 ? normalized + PageCount : normalized;
        }

        public static string Loading(int page) => Build(
            page,
            $"<align=center><size=13>\n\n\n\n</size><size=17><color={Muted}>Загрузка лидеров...</color></size>" +
            "<size=13>\n\n\n\n\n</size></align>");

        public static string Unavailable(int page, string message) => Build(
            page,
            $"<align=center><size=13>\n\n\n\n</size><size=16><color={Muted}>{Escape(message)}</color></size>" +
            "<size=13>\n\n\n\n\n</size></align>");

        public static string Page(LeaderboardRecord record, int requestedPage)
        {
            int page = NormalizePage(requestedPage);
            if (record is null)
                return Unavailable(page, "Таблица лидеров пока недоступна");

            LeaderboardCategory category = GetCategory(page);
            IReadOnlyList<LeaderboardEntry> entries = record.GetEntries(category);
            if (entries.Count == 0)
                return Build(page, $"<align=center><size=13>\n\n\n\n</size><size=16><color={Muted}>Результатов пока нет</color></size>" +
                    "<size=13>\n\n\n\n\n</size></align>");

            StringBuilder body = new StringBuilder();
            bool showRole = category == LeaderboardCategory.FastestEscape;
            body.Append("<size=10><color=").Append(Muted).Append("><b><pos=4%>№<pos=13%>ИГРОК");
            if (showRole)
                body.Append("<pos=70%>РОЛЬ<pos=84%>ВРЕМЯ");
            else
                body.Append("<pos=72%>РЕЗУЛЬТАТ");
            body.Append("</b></color></size>\n");

            for (int index = 0; index < entries.Count && index < 10; index++)
                AppendEntry(body, entries[index], index + 1, category);

            return Build(page, body.ToString());
        }

        private static string Build(int requestedPage, string body)
        {
            int page = NormalizePage(requestedPage);
            return
                $"<align=center><size=19><color={Accent}><b>SITE-02</b></color></size>\n" +
                $"<size=17><color={Text}><b>ЛИДЕРЫ СЕРВЕРА</b></color></size>\n" +
                $"<size=10><color={Accent}>━━━━━━━━━━━━━━━━━━━━━━━━━━━━</color></size>\n" +
                $"<size=11><color={Muted}><b>{GetTitle(page)} · {page + 1}/{PageCount}</b></color></size></align>\n" +
                $"<align=left><size=13><color={Text}><line-height=110%>{body}</line-height></color></size></align>\n" +
                $"<align=center><size=11><color={Muted}>ЗА ВСЁ ВРЕМЯ</color></size></align>";
        }

        private static void AppendEntry(
            StringBuilder builder,
            LeaderboardEntry entry,
            int place,
            LeaderboardCategory category)
        {
            builder.Append("<nobr><pos=4%><color=")
                .Append(GetPlaceColor(place))
                .Append("><b>")
                .Append(place.ToString(CultureInfo.InvariantCulture))
                .Append("</b></color><pos=13%><color=")
                .Append(GetNicknameColor(place))
                .Append(">")
                .Append(Escape(ShortenNickname(entry?.Nickname)))
                .Append("</color>");

            if (category == LeaderboardCategory.FastestEscape)
            {
                builder.Append("<pos=70%>")
                    .Append(FormatEscapeRole(entry?.EscapeRole ?? LeaderboardEscapeRole.None))
                    .Append("<pos=84%><color=")
                    .Append(ValueAccent)
                    .Append("><b>")
                    .Append(Stopwatch(entry?.Value ?? 0))
                    .Append("</b></color>");
            }
            else
            {
                builder.Append("<pos=72%><color=")
                    .Append(ValueAccent)
                    .Append("><b>")
                    .Append(FormatValue(category, entry?.Value ?? 0))
                    .Append("</b></color>");
            }

            builder.Append("</nobr>\n");
        }

        private static LeaderboardCategory GetCategory(int page)
        {
            switch (NormalizePage(page))
            {
                case 1: return LeaderboardCategory.Kills;
                case 2: return LeaderboardCategory.Escapes;
                case 3: return LeaderboardCategory.FastestEscape;
                case 4: return LeaderboardCategory.Snake;
                default: return LeaderboardCategory.Playtime;
            }
        }

        private static string GetTitle(int page)
        {
            switch (NormalizePage(page))
            {
                case 1: return "УБИЙСТВА";
                case 2: return "ПОБЕГИ";
                case 3: return "БЫСТРЕЙШИЙ ПОБЕГ";
                case 4: return "РЕКОРД ЗМЕЙКИ";
                default: return "НАИГРАННОЕ ВРЕМЯ";
            }
        }

        private static string FormatValue(LeaderboardCategory category, long value) =>
            category == LeaderboardCategory.Playtime ? Duration(value) : Number(value);

        private static string Number(long value) =>
            value.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');

        private static string Duration(long seconds)
        {
            if (seconds <= 0)
                return "—";

            TimeSpan value = TimeSpan.FromSeconds(seconds);
            if (value.Days > 0)
                return $"{value.Days} д {value.Hours} ч";
            if (value.Hours > 0)
                return $"{value.Hours} ч {value.Minutes} мин";
            return $"{Math.Max(1, value.Minutes)} мин";
        }

        private static string Stopwatch(long seconds)
        {
            if (seconds <= 0)
                return "—";

            TimeSpan value = TimeSpan.FromSeconds(seconds);
            return value.TotalHours >= 1
                ? $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
                : $"{value.Minutes}:{value.Seconds:00}";
        }

        private static string FormatEscapeRole(LeaderboardEscapeRole role)
        {
            switch (role)
            {
                case LeaderboardEscapeRole.ClassD: return $"<color={ClassD}><b>D</b></color>";
                case LeaderboardEscapeRole.Scientist: return $"<color={Scientist}><b>УЧ</b></color>";
                case LeaderboardEscapeRole.Both: return $"<color={ValueAccent}><b>D/УЧ</b></color>";
                default: return $"<color={Muted}>—</color>";
            }
        }

        private static string GetPlaceColor(int place)
        {
            switch (place)
            {
                case 1: return Gold;
                case 2: return Silver;
                case 3: return Bronze;
                default: return Muted;
            }
        }

        private static string GetNicknameColor(int place) =>
            place >= 1 && place <= 3 ? GetPlaceColor(place) : Text;

        private static string ShortenNickname(string nickname)
        {
            string value = string.IsNullOrWhiteSpace(nickname) ? "Игрок" : nickname.Trim();
            return value.Length <= 20 ? value : value.Substring(0, 19) + "…";
        }

        private static string Escape(string value) => (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
