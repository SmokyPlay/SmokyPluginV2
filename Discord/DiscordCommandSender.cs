namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections.Generic;

    using Exiled.API.Features;

    using NorthwoodLib;

    using ExiledPermissions = Exiled.Permissions.Extensions.Permissions;
    using PermissionGroup = Exiled.Permissions.Features.Group;

    internal sealed class DiscordCommandSender : CommandSender
    {
        private readonly Action<string, bool> reply;
        private readonly UserGroup group;

        public DiscordCommandSender(
            ulong discordUserId,
            string nickname,
            string groupName,
            UserGroup group,
            Action<string, bool> reply)
        {
            DiscordUserId = discordUserId;
            NicknameValue = string.IsNullOrWhiteSpace(nickname) ? discordUserId.ToString() : nickname;
            GroupName = groupName ?? string.Empty;
            this.group = group ?? throw new ArgumentNullException(nameof(group));
            this.reply = reply;
        }

        public ulong DiscordUserId { get; }

        public string GroupName { get; }

        private string NicknameValue { get; }

        public override string SenderId => $"{DiscordUserId}@discord";

        public override string Nickname => NicknameValue;

        public override ulong Permissions => group.Permissions;

        public override byte KickPower => group.KickPower;

        public override bool FullPermissions => false;

        public override string LogName => $"{Nickname} ({SenderId}) [Discord/{GroupName}]";

        public override void RaReply(string text, bool success, bool logToConsole, string overrideDisplay)
        {
            string plainText = GetPlainRemoteAdminResponse(text);
            Log.Info($"[Discord RA response] {LogName} | success={success} | {plainText ?? "<null>"}");
            reply?.Invoke(plainText, success);
        }

        public override void Print(string text)
        {
            string plainText = StripRichText(text);
            Log.Info($"[Discord RA response] {LogName} | print | {plainText ?? "<null>"}");
            reply?.Invoke(plainText, true);
        }

        public override bool Available() => true;

        internal bool CheckExiledPermission(string permission)
        {
            if (string.IsNullOrWhiteSpace(permission) || string.IsNullOrWhiteSpace(GroupName))
                return false;

            string groupName = GroupName.Trim();
            if (ExiledPermissions.Groups is null ||
                !ExiledPermissions.Groups.TryGetValue(groupName, out PermissionGroup permissionGroup) ||
                permissionGroup?.CombinedPermissions is null)
            {
                return false;
            }

            return HasPermission(permissionGroup.CombinedPermissions, permission);
        }

        private static string GetPlainRemoteAdminResponse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            int separatorIndex = text.IndexOf('#');
            if (separatorIndex >= 0)
                text = text.Substring(separatorIndex + 1);

            return StripRichText(text);
        }

        private static string StripRichText(string text) =>
            string.IsNullOrEmpty(text) ? text : StringUtils.StripTags(text).Trim();

        private static bool HasPermission(IEnumerable<string> permissions, string requiredPermission)
        {
            HashSet<string> available = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
            if (available.Contains(".*") || available.Contains(requiredPermission))
                return true;

            string[] parts = requiredPermission.Split('.');
            string prefix = string.Empty;

            for (int index = 0; index < parts.Length - 1; index++)
            {
                prefix = index == 0 ? parts[index] : $"{prefix}.{parts[index]}";
                if (available.Contains($"{prefix}.*"))
                    return true;
            }

            return false;
        }
    }
}
