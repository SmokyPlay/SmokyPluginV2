namespace SmokyPluginV2.AccountLinks
{
    using System;
    using System.Collections.Generic;

    public sealed class AccountLinkDatabase
    {
        public int Version { get; set; } = 1;

        public List<AccountLinkRecord> Links { get; set; } = new List<AccountLinkRecord>();
    }

    public sealed class AccountLinkRecord
    {
        public string PlayerUserId { get; set; } = string.Empty;

        public ulong DiscordUserId { get; set; }

        public DateTime LinkedAtUtc { get; set; }
    }
}
