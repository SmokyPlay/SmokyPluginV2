namespace SmokyPluginV2.Patches
{
    using HarmonyLib;

    using RemoteAdmin;

    using SmokyPluginV2.Discord;

    using ExiledPermissions = Exiled.Permissions.Extensions.Permissions;

    [HarmonyPatch(
        typeof(ExiledPermissions),
        nameof(ExiledPermissions.CheckPermission),
        new[] { typeof(CommandSender), typeof(string) })]
    internal static class DiscordPermissionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CommandSender sender, string permission, ref bool __result)
        {
            if (!__result && sender is DiscordCommandSender discordSender)
                __result = discordSender.CheckExiledPermission(permission);
        }
    }
}
