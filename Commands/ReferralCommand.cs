namespace SmokyPluginV2.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;

    using CommandSystem;

    using Exiled.API.Features;

    using SmokyPluginV2.Referrals;
    using SmokyPluginV2.Statistics;

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class ReferralCommand : ICommand
    {
        public string Command => "ref";

        public string[] Aliases => new[] { "referral" };

        public string Description => "Показывает реферальную программу или принимает код: .ref / .ref КОД";

        public bool Execute(
            ArraySegment<string> arguments,
            ICommandSender sender,
            out string response)
        {
            ReferralService referrals = Plugin.Instance?.Referrals;
            if (referrals == null ||
                Plugin.Instance?.Config?.EarnedPrivileges?.Referrals?.IsEnabled != true)
            {
                response = "Реферальная программа отключена.";
                return false;
            }

            Player player = Player.Get(sender);
            if (player == null ||
                !player.IsConnected ||
                player.IsHost ||
                player.IsNPC)
            {
                response = "Не удалось определить ваш Steam-аккаунт.";
                return false;
            }

            if (Plugin.Instance?.PlayerAccess?.TryGetResolvedSteamUserId(player, out string steamUserId) != true)
            {
                response = "Для вашего Discord-аккаунта не найдена связка со Steam.";
                return false;
            }

            if (arguments.Count > 1)
            {
                response = $"Использование: .{Command} или .{Command} КОД";
                return false;
            }

            StatisticsService statistics = Plugin.Instance?.Statistics;
            if (statistics != null &&
                !statistics.TryFlushPendingWrites(out string synchronizationError))
            {
                response = synchronizationError;
                return false;
            }

            if (arguments.Count == 0)
            {
                if (!referrals.TryGetOrCreateStatus(
                        steamUserId,
                        out ReferralStatus status,
                        out string statusError))
                {
                    response = statusError;
                    return false;
                }

                response = BuildStatusResponse(status, referrals);
                return true;
            }

            long liveSeconds = statistics?.GetUnpersistedPlaytimeSeconds(player.UserId) ?? 0;
            string code = arguments.Array[arguments.Offset];
            bool accepted = referrals.TryAccept(
                steamUserId,
                code,
                liveSeconds,
                out response);
            if (accepted)
                Plugin.Instance?.PlayerAccess?.Synchronize(player);
            return accepted;
        }

        private static string BuildStatusResponse(
            ReferralStatus status,
            ReferralService referrals)
        {
            int qualificationMinutes = referrals.QualificationMinutes;
            long qualificationSeconds = qualificationMinutes * 60L;
            int requiredReferrals = referrals.RequiredReferrals;
            IReadOnlyList<ReferralParticipant> participants =
                status?.Participants ?? Array.Empty<ReferralParticipant>();
            int qualified = participants.Count(item =>
                item.TotalPlaytimeSeconds >= qualificationSeconds);
            int pending = Math.Max(0, participants.Count - qualified);
            string groupName = DisplayGroupName(
                Plugin.Instance?.Config?.EarnedPrivileges?.GroupName);
            string referralCommand = $".{new ReferralCommand().Command}";
            JanitorCardCommand janitorCommand = new JanitorCardCommand();
            string janitorCommands = $".{janitorCommand.Command}";
            if (janitorCommand.Aliases != null && janitorCommand.Aliases.Length > 0)
            {
                janitorCommands += " или " + string.Join(
                    ", ",
                    janitorCommand.Aliases.Select(alias => "." + alias));
            }

            StringBuilder result = new StringBuilder();
            result.AppendLine("=== РЕФЕРАЛЬНАЯ ПРОГРАММА ===");
            result.AppendLine();
            result.Append("Приглашайте друзей и получите привилегию ")
                .Append(groupName)
                .Append(" после ")
                .Append(requiredReferrals)
                .AppendLine(" подтверждённых приглашений.");
            result.Append("Эта привилегия выдаётся навсегда и даёт повышенный шанс получить выбранную роль — преимущество x")
                .Append(FormatWeight(referrals.EarnedPrivilegeWeight))
                .AppendLine(".");

            if (referrals.CodeEntryMaxMinutes > 0)
            {
                result.Append("Новый игрок должен ввести ваш код в течение первых ")
                    .Append(referrals.CodeEntryMaxMinutes)
                    .AppendLine(" минут игры на сервере.");
            }
            else
            {
                result.AppendLine("Ввод новых реферальных кодов сейчас закрыт конфигурацией сервера.");
            }

            result.AppendLine("После активации кода и до подтверждения приглашения он получит:");
            int rewardNumber = 1;
            if (referrals.PendingReferralWeight > 0)
            {
                result.Append(rewardNumber++)
                    .Append(". Повышенный шанс получить выбранную роль (преимущество x")
                    .Append(FormatWeight(referrals.PendingReferralWeight))
                    .AppendLine(");");
            }

            result.Append(rewardNumber)
                .Append(". Карту уборщика один раз за раунд по команде ")
                .Append(janitorCommands)
                .AppendLine(".");
            result.Append("Приглашение подтверждается после ")
                .Append(qualificationMinutes)
                .AppendLine(" минут общего игрового времени.");
            result.AppendLine();

            result.AppendLine("КОД ДЛЯ КОПИРОВАНИЯ");
            result.Append(referralCommand)
                .Append(' ')
                .AppendLine(status?.ReferralCode ?? "-");
            result.AppendLine();

            result.AppendLine("ПРОГРЕСС");
            if (status?.HasReferralPrivilege == true)
                result.Append("Привилегия получена: ").AppendLine(groupName);
            result.Append("Подтверждено: ")
                .Append(qualified)
                .Append('/')
                .AppendLine(requiredReferrals.ToString(CultureInfo.InvariantCulture));

            result.Append("Ожидают подтверждения: ")
                .AppendLine(pending.ToString(CultureInfo.InvariantCulture));
            result.AppendLine();
            result.AppendLine("ПРИГЛАШЁННЫЕ ИГРОКИ");
            AppendParticipants(
                result,
                participants,
                qualificationSeconds,
                referrals.InGameMaximumDisplayedParticipants);
            result.AppendLine();
            result.Append("Один игрок может использовать только один реферальный код: ")
                .Append(referralCommand)
                .AppendLine(" КОД");
            return result.ToString().TrimEnd();
        }

        private static void AppendParticipants(
            StringBuilder result,
            IReadOnlyList<ReferralParticipant> participants,
            long qualificationSeconds,
            int maximumDisplayedParticipants)
        {
            if (participants == null || participants.Count == 0)
            {
                result.AppendLine("Пока никто не использовал ваш код.");
                return;
            }

            int displayed = maximumDisplayedParticipants == 0
                ? participants.Count
                : Math.Min(participants.Count, maximumDisplayedParticipants);
            for (int index = 0; index < displayed; index++)
            {
                ReferralParticipant participant = participants[index];
                bool isQualified = participant.TotalPlaytimeSeconds >= qualificationSeconds;
                result.Append(isQualified ? "✓ " : "⏳ ")
                    .Append(DisplayParticipantName(participant));
                if (isQualified)
                {
                    result.AppendLine(" — подтверждено");
                }
                else
                {
                    result.Append(" — ")
                        .Append(FormatDuration(participant.TotalPlaytimeSeconds))
                        .Append(" / ")
                        .AppendLine(FormatDuration(qualificationSeconds));
                }
            }

            int omitted = participants.Count - displayed;
            if (omitted > 0)
                result.Append("… и ещё ").Append(omitted).AppendLine(" игроков.");
        }

        private static string DisplayParticipantName(ReferralParticipant participant)
        {
            string value = string.IsNullOrWhiteSpace(participant?.Nickname)
                ? PostgreSqlDisplayId(participant?.PlayerUserId)
                : participant.Nickname.Trim();
            return value.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string DisplayGroupName(string groupName)
        {
            string value = string.IsNullOrWhiteSpace(groupName)
                ? "привилегия"
                : groupName.Trim();
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static string FormatWeight(double weight) =>
            weight.ToString("0.##", CultureInfo.InvariantCulture);

        private static string FormatDuration(long seconds)
        {
            long totalMinutes = Math.Max(0, seconds) / 60;
            long hours = totalMinutes / 60;
            long minutes = totalMinutes % 60;
            if (hours > 0)
                return minutes > 0 ? $"{hours} ч {minutes} мин" : $"{hours} ч";
            return $"{minutes} мин";
        }

        private static string PostgreSqlDisplayId(string playerUserId)
        {
            string value = playerUserId ?? "неизвестный игрок";
            int suffix = value.IndexOf('@');
            return suffix > 0 ? value.Substring(0, suffix) : value;
        }
    }
}
