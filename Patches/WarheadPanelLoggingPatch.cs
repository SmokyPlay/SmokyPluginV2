namespace SmokyPluginV2.Patches
{
    using System;
    using System.Reflection;

    using Exiled.API.Features;

    using HarmonyLib;

    [HarmonyPatch]
    internal static class WarheadPanelLoggingPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(
                typeof(AlphaWarheadActivationPanel),
                "ServerInteractKeycard",
                new[] { typeof(ReferenceHub) })
            ?? throw new MissingMethodException(
                typeof(AlphaWarheadActivationPanel).FullName,
                "ServerInteractKeycard");

        private static void Prefix(out bool __state) =>
            __state = AlphaWarheadActivationPanel.IsUnlocked;

        private static void Postfix(
            ReferenceHub ply,
            bool __state)
        {
            if (!__state && AlphaWarheadActivationPanel.IsUnlocked)
                Handlers.DiscordGameEventHandler.LogSuccessfulWarheadPanelAccess(Player.Get(ply));
        }
    }
}
