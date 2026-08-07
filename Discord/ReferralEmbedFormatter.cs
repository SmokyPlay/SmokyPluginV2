namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;

    using SmokyPluginV2.Referrals;

    internal static class ReferralEmbedFormatter
    {
        private const int DiscordFieldLimit = 1024;
        private const int MaximumDisplayedNameLength = 48;

        public static DiscordEmbed Create(
            ReferralStatus status,
            string privilegeGroupName,
            int codeEntryMaxMinutes,
            int qualificationMinutes,
            int requiredReferrals,
            double pendingReferralWeight,
            double earnedPrivilegeWeight)
        {
            long requiredSeconds = Math.Max(1, qualificationMinutes) * 60L;
            int requiredCount = Math.Max(1, requiredReferrals);
            IReadOnlyList<ReferralParticipant> participants =
                status?.Participants ?? Array.Empty<ReferralParticipant>();
            int qualified = participants.Count(item =>
                item.TotalPlaytimeSeconds >= requiredSeconds);
            int pending = Math.Max(0, participants.Count - qualified);
            string groupName = DisplayGroupName(privilegeGroupName);
            string progress = (status?.HasReferralPrivilege == true
                    ? $"✅ Привилегия **{groupName}** получена!\n"
                    : string.Empty) +
                $"Подтверждено: **{qualified}/{requiredCount}**\n" +
                $"Ожидают подтверждения: **{pending}**";

            return new DiscordEmbed
            {
                Title = "Реферальная программа",
                Description =
                    $"Приглашайте друзей и получите привилегию **{groupName}** после " +
                    $"**{requiredCount}** подтверждённых приглашений.\n" +
                    $"Эта привилегия выдаётся навсегда и даёт повышенный шанс получить выбранную роль — преимущество **x{FormatWeight(earnedPrivilegeWeight)}**.\n\n" +
                    $"Новый игрок должен ввести ваш код в течение первых **{Math.Max(0, codeEntryMaxMinutes)} минут** игры на сервере.\n" +
                    "После активации кода и до подтверждения приглашения он получит:\n" +
                    $"1. Повышенный шанс получить выбранную роль (преимущество x{FormatWeight(pendingReferralWeight)});\n" +
                    "2. Карту уборщика один раз за раунд по команде `.janitorcard` или `.jc`.\n" +
                    $"Приглашение подтверждается после **{Math.Max(1, qualificationMinutes)} минут** общего игрового времени.\n\n" +
                    "Код для копирования:\n" +
                    $"```text\n.ref {status?.ReferralCode}\n```",
                Color = 0x57F287,
                Fields = new[]
                {
                    new DiscordEmbedField
                    {
                        Name = "Прогресс",
                        Value = progress,
                        Inline = false,
                    },
                    new DiscordEmbedField
                    {
                        Name = "Приглашённые игроки",
                        Value = BuildParticipantList(participants, requiredSeconds),
                        Inline = false,
                    },
                },
                Footer = "Один игрок может использовать только один реферальный код.",
            };
        }

        private static string BuildParticipantList(
            IReadOnlyList<ReferralParticipant> participants,
            long requiredSeconds)
        {
            if (participants == null || participants.Count == 0)
                return "Пока никто не использовал ваш код.";

            List<string> lines = participants.Select(item =>
            {
                bool isQualified = item.TotalPlaytimeSeconds >= requiredSeconds;
                string name = FormatName(item);
                string played = FormatDuration(item.TotalPlaytimeSeconds);
                return isQualified
                    ? $"✅ {name}"
                    : $"⏳ {name} — {played} / {FormatDuration(requiredSeconds)}";
            }).ToList();

            StringBuilder result = new StringBuilder();
            for (int index = 0; index < lines.Count; index++)
            {
                string separator = result.Length == 0 ? string.Empty : "\n";
                string line = lines[index];
                int omittedAfterLine = lines.Count - index - 1;
                string possibleSuffix = omittedAfterLine > 0
                    ? $"\n… и ещё {omittedAfterLine} игроков."
                    : string.Empty;
                if (result.Length + separator.Length + line.Length + possibleSuffix.Length <= DiscordFieldLimit)
                {
                    result.Append(separator).Append(line);
                    continue;
                }

                string omittedSuffix = $"… и ещё {lines.Count - index} игроков.";
                if (result.Length > 0)
                    result.Append('\n');
                int available = DiscordFieldLimit - result.Length;
                if (available > 0)
                    result.Append(omittedSuffix.Substring(0, Math.Min(available, omittedSuffix.Length)));
                break;
            }

            return result.ToString();
        }

        private static string FormatName(ReferralParticipant participant)
        {
            string name = string.IsNullOrWhiteSpace(participant?.Nickname)
                ? PostgreSqlDisplayId(participant?.PlayerUserId)
                : DiscordLogService.Escape(participant.Nickname);
            if (name.Length <= MaximumDisplayedNameLength)
                return name;
            return name.Substring(0, MaximumDisplayedNameLength - 1) + "…";
        }

        private static string DisplayGroupName(string groupName)
        {
            string value = string.IsNullOrWhiteSpace(groupName) ? "привилегия" : groupName.Trim();
            return value.Length == 0
                ? "привилегия"
                : char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static string FormatDuration(long seconds)
        {
            long totalMinutes = Math.Max(0, seconds) / 60;
            long hours = totalMinutes / 60;
            long minutes = totalMinutes % 60;
            if (hours > 0)
                return minutes > 0 ? $"{hours} ч {minutes} мин" : $"{hours} ч";
            return $"{minutes} мин";
        }

        private static string FormatWeight(double weight) =>
            weight > 0 && !double.IsNaN(weight) && !double.IsInfinity(weight)
                ? weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : "0";

        private static string PostgreSqlDisplayId(string playerUserId)
        {
            string value = playerUserId ?? "неизвестный игрок";
            int suffix = value.IndexOf('@');
            return suffix > 0 ? value.Substring(0, suffix) : value;
        }
    }
}
