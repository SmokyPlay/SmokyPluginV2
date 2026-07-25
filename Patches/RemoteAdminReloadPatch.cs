namespace SmokyPluginV2.Patches
{
    using System.Collections.Generic;
    using System.Linq;

    using CommandSystem;

    using Exiled.Permissions.Extensions;

    using HarmonyLib;

    [HarmonyPatch(
        typeof(Exiled.Events.Commands.Reload.RemoteAdmin),
        nameof(Exiled.Events.Commands.Reload.RemoteAdmin.Execute))]
    internal static class RemoteAdminReloadPatch
    {
        [HarmonyPostfix]
        private static void Postfix(bool __result)
        {
            if (!__result)
                return;

            Plugin.Instance?.PlayerAccess?.RefreshOnlinePlayers(true);
        }
    }

    [HarmonyPatch(
        typeof(Exiled.Events.Commands.Reload.Configs),
        nameof(Exiled.Events.Commands.Reload.Configs.Execute))]
    internal static class PluginConfigReloadPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ICommandSender __1)
        {
            if (__1?.CheckPermission("ee.reloadconfigs") != true)
                return;

            RemoveExiledNestedCommands();
        }

        [HarmonyPostfix]
        private static void Postfix(bool __result)
        {
            if (!__result)
                return;

            Plugin.Instance?.ApplyReloadedConfiguration();
        }

        private static void RemoveExiledNestedCommands()
        {
            Exiled.Events.Events eventsPlugin = Exiled.Events.Events.Instance;
            if (eventsPlugin?.Assembly is null)
                return;

            HashSet<CommandHandler> parentHandlers = new HashSet<CommandHandler>();
            foreach (var registeredByType in eventsPlugin.Commands.Values)
            {
                foreach (ICommand command in registeredByType.Values)
                {
                    if (command is CommandHandler parentHandler)
                        parentHandlers.Add(parentHandler);
                }
            }

            foreach (CommandHandler parentHandler in parentHandlers)
            {
                foreach (ICommand nestedCommand in parentHandler.AllCommands
                             .Where(command => command?.GetType().Assembly == eventsPlugin.Assembly)
                             .Distinct()
                             .ToList())
                {
                    parentHandler.UnregisterCommand(nestedCommand);
                }
            }
        }
    }
}
