namespace SmokyPluginV2.Warnings
{
    using System;
    using System.Collections.Generic;

    public sealed class WarningDatabase
    {
        public int Version { get; set; } = 3;

        public long NextId { get; set; } = 1;

        public List<WarningRecord> Warnings { get; set; } = new List<WarningRecord>();
    }

    public sealed class WarningRecord
    {
        public long Id { get; set; }

        public string PlayerUserId { get; set; } = string.Empty;

        public string PlayerNickname { get; set; } = string.Empty;

        public string ModeratorUserId { get; set; } = string.Empty;

        public string ModeratorNickname { get; set; } = string.Empty;

        public DateTime IssuedAtUtc { get; set; }

        public string Reason { get; set; } = string.Empty;
    }
}
