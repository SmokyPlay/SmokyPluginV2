namespace SmokyPluginV2.Privileges
{
    using System;
    using System.Collections.Generic;

    internal sealed class PlayerAccessIdentity
    {
        public long PlayerId { get; set; }

        public string PlayerUserId { get; set; }

        public ulong DiscordUserId { get; set; }
    }

    internal sealed class PendingPrivilegeRevocation
    {
        public long SourceId { get; set; }

        public string SourceType { get; set; }

        public string GroupName { get; set; }
    }

    internal sealed class PlayerAccessSnapshot
    {
        public string PlayerUserId { get; set; }

        public ulong DiscordUserId { get; set; }

        public IReadOnlyList<string> SteamPrivilegeGroups { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> DiscordPrivilegeGroups { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> PrivilegeGroups { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> ManagedDiscordGroups { get; set; } = Array.Empty<string>();

        public IReadOnlyList<PendingPrivilegeRevocation> PendingRevocations { get; set; } =
            Array.Empty<PendingPrivilegeRevocation>();

        public long TotalPlaytimeSeconds { get; set; }

        public double? TemporaryRolePreferenceWeight { get; set; }
    }
}
