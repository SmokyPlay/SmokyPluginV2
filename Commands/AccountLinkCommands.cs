namespace SmokyPluginV2.Commands
{
    using System;

    using CommandSystem;

    using Exiled.API.Features;

    using SmokyPluginV2.AccountLinks;

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class LinkAccountCommand : ICommand
    {
        public string Command => "link";

        public string[] Aliases => Array.Empty<string>();

        public string Description => "Привязывает игровой аккаунт к Discord: .link <код>.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            AccountLinkService service = Plugin.Instance?.AccountLinks;
            if (service is null || Plugin.Instance?.Config?.Discord?.AccountLinking?.IsEnabled != true)
            {
                response = "Привязка аккаунтов отключена.";
                return false;
            }

            Player player = Player.Get(sender);
            if (player is null || !player.IsConnected || player.IsHost)
            {
                response = "Не удалось определить ваш игровой аккаунт. Повторите команду после полной авторизации на сервере.";
                return false;
            }

            if (arguments.Count != 1)
            {
                response = "Использование: .link <код из Discord-команды /link>";
                return false;
            }

            string code = arguments.Array[arguments.Offset];
            if (!service.TryLink(code, player.UserId, out ulong discordUserId, out response))
                return false;

            if (Plugin.Instance.DiscordLogs != null)
            {
                Plugin.Instance.DiscordLogs.SynchronizeLinkedPlayer(player, true);
                response = $"Аккаунт успешно привязан к Discord `{discordUserId}`. Актуальные Discord-роли запрошены.";
            }
            else
            {
                response = $"Аккаунт успешно привязан к Discord `{discordUserId}`, но Discord-бот сейчас недоступен. Роли синхронизируются при следующем входе после запуска бота.";
            }

            return true;
        }
    }

    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class UnlinkAccountCommand : ICommand
    {
        public string Command => "unlink";

        public string[] Aliases => Array.Empty<string>();

        public string Description => "Отвязывает игровой аккаунт от Discord.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (arguments.Count != 0)
            {
                response = "Использование: .unlink";
                return false;
            }

            AccountLinkService service = Plugin.Instance?.AccountLinks;
            if (service is null || Plugin.Instance?.Config?.Discord?.AccountLinking?.IsEnabled != true)
            {
                response = "Привязка аккаунтов отключена.";
                return false;
            }

            Player player = Player.Get(sender);
            if (player is null || !player.IsConnected || player.IsHost)
            {
                response = "Не удалось определить ваш игровой аккаунт.";
                return false;
            }

            if (!service.TryUnlinkPlayer(player.UserId, out _, out response))
                return false;

            Plugin.Instance.DiscordLogs?.RemoveSynchronizedGroup(player);
            response = "Игровой аккаунт отвязан от Discord. Временная Discord-группа снята.";
            return true;
        }
    }
}
