namespace SmokyPluginV2.Commands
{
    using System;

    using CommandSystem;

    using Exiled.API.Features;

    using SmokyPluginV2.Referrals;

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class JanitorCardCommand : ICommand
    {
        public string Command => "janitorcard";

        public string[] Aliases => new[] { "jc" };

        public string Description => "Выдаёт карту уборщика участнику активной реферальной программы.";

        public bool Execute(
            ArraySegment<string> arguments,
            ICommandSender sender,
            out string response)
        {
            if (arguments.Count != 0)
            {
                response = "Использование: .janitorcard";
                return false;
            }

            ReferralService referrals = Plugin.Instance?.Referrals;
            if (referrals == null ||
                Plugin.Instance?.Config?.EarnedPrivileges?.Referrals?.IsEnabled != true)
            {
                response = "Реферальная программа отключена.";
                return false;
            }

            Player player = Player.Get(sender);
            return referrals.TryGiveJanitorCard(player, out response);
        }
    }
}
