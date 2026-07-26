namespace SmokyPluginV2.Privileges
{
    using System;
    using System.Collections.Generic;

    internal sealed class PlayerAccessSnapshot
    {
        public string PlayerUserId { get; set; }

        public ulong DiscordUserId { get; set; }

        public IReadOnlyList<string> SteamPrivilegeGroups { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> DiscordPrivilegeGroups { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> PrivilegeGroups { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> ManagedDiscordGroups { get; set; } = Array.Empty<string>();

        public long TotalPlaytimeSeconds { get; set; }

        public double? TemporaryRolePreferenceWeight { get; set; }
    }
}
