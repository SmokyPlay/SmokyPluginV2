namespace SmokyPluginV2.Commands
{
    using System;

    using CommandSystem;

    using Exiled.Permissions.Extensions;

    using SmokyPluginV2.Statistics;

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class StatisticsRecordingCommand : ICommand
    {
        private const string Permission = "smokyplugin.statistics.toggle";

        public string Command => "statstoggle";

        public string[] Aliases => new[] { "togglestats", "ts" };

        public string Description => "Включает или выключает запись статистики до повторной команды или начала следующего раунда.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (arguments.Count != 0)
            {
                response = "Использование: statstoggle";
                return false;
            }

            StatisticsSettings settings = Plugin.Instance?.Config?.Statistics;
            if (!sender.CheckPermission(Permission))
            {
                response = $"Недостаточно прав. Требуется: {Permission}";
                return false;
            }

            StatisticsService statistics = Plugin.Instance?.Statistics;
            if (statistics is null || settings?.IsEnabled != true)
            {
                response = "Система статистики отключена или PostgreSQL недоступна.";
                return false;
            }

            bool changed = statistics.ToggleRecording(out bool isRecording, out response);
            if (changed)
                response = (isRecording ? "[ВКЛЮЧЕНО] " : "[ВЫКЛЮЧЕНО] ") + response;
            return changed;
        }
    }
}
