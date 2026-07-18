namespace SmokyPluginV2.Commands
{
    using System;

    using CommandSystem;

    using Exiled.API.Features;
    using Exiled.Permissions.Extensions;

    using SmokyPluginV2.RolePreferences;

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class EventLobbyCommand : ICommand
    {
        private const string Permission = "smokyplugin.eventlobby";

        public string Command => "eventlobby";

        public string[] Aliases => new[] { "elobby", "eventbriefing" };

        public string Description => "Переключает блокировку лобби и временное ограничение голосового чата для объяснения правил ивента.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(Permission))
            {
                response = $"Недостаточно прав. Требуется: {Permission}";
                return false;
            }

            if (arguments.Count != 0)
            {
                response = "Использование: eventlobby";
                return false;
            }

            RolePreferenceService service = Plugin.Instance?.RolePreferences;
            if (service is null)
            {
                response = "Система выбора ролей в башне отключена.";
                return false;
            }

            if (!service.TryToggleEventBriefing(out bool enabled, out response))
                return false;

            Log.Info($"[Role Preferences] Event briefing {(enabled ? "enabled" : "disabled")} through Remote Admin command.");
            response = enabled
                ? "Лобби заблокировано. Голос участников ограничен, хинт ивента включён. Повторите команду для снятия."
                : "Блокировка лобби и временное ограничение голосового чата сняты.";
            return true;
        }
    }
}
