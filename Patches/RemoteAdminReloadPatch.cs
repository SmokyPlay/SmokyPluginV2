namespace SmokyPluginV2.Patches
{
    using HarmonyLib;

    using SmokyPluginV2.Discord;

    [HarmonyPatch(
        typeof(Exiled.Events.Commands.Reload.RemoteAdmin),
        nameof(Exiled.Events.Commands.Reload.RemoteAdmin.Execute))]
    internal static class RemoteAdminReloadPatch
    {
        [HarmonyPostfix]
        private static void Postfix(bool __result)
        {
            if (__result)
                DiscordLogService.Current?.RefreshLinkedPlayerGroups();
        }
    }
}
