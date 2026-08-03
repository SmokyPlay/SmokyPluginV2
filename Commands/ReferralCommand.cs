namespace SmokyPluginV2.Commands
{
    using System;

    using CommandSystem;

    using Exiled.API.Features;

    using SmokyPluginV2.Referrals;
    using SmokyPluginV2.Statistics;

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class ReferralCommand : ICommand
    {
        public string Command => "ref";

        public string[] Aliases => new[] { "referral" };

        public string Description => "Использует реферальный код: .ref КОД";

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

            if (arguments.Count != 1)
            {
                response = "Использование: .ref КОД";
                return false;
            }

            StatisticsService statistics = Plugin.Instance?.Statistics;
            if (statistics != null &&
                !statistics.TryFlushPendingWrites(out string synchronizationError))
            {
                response = synchronizationError;
                return false;
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
    }
}
