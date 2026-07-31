namespace SmokyPluginV2.Moderation
{
    using System;
    using System.Collections.Generic;

    public enum PunishmentType
    {
        Warning,
        Kick,
        Ban,
    }

    public sealed class PunishmentRecord
    {
        public long Id { get; set; }
        public string PlayerUserId { get; set; } = string.Empty;
        public string PlayerNickname { get; set; } = string.Empty;
        public ulong DiscordUserId { get; set; }
        public PunishmentType Type { get; set; }
        public string ModeratorUserId { get; set; } = string.Empty;
        public string ModeratorNickname { get; set; } = string.Empty;
        public string ModeratorSteamId { get; set; } = string.Empty;
        public DateTime IssuedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime? NotifiedAtUtc { get; set; }
        public string Reason { get; set; } = string.Empty;

        public DateTime? EffectiveEndUtc => Type == PunishmentType.Ban ? ExpiresAtUtc : IssuedAtUtc;
    }

    public sealed class PunishmentHistory
    {
        public bool PlayerExists { get; set; }

        public string PlayerUserId { get; set; } = string.Empty;
        public string PlayerNickname { get; set; } = string.Empty;
        public ulong DiscordUserId { get; set; }
        public IReadOnlyList<PunishmentRecord> Records { get; set; } = Array.Empty<PunishmentRecord>();
    }

    internal sealed class PunishmentModerator
    {
        public string UserId { get; set; }
        public string Nickname { get; set; }
        public byte KickPower { get; set; }
        public bool IsServer { get; set; }
    }

    internal static class ModerationPermissions
    {
        public const string IssueWarning = "smokyplugin.moderation.warning.issue";
        public const string ViewHistory = "smokyplugin.moderation.history.view";
        public const string DeleteHistory = "smokyplugin.moderation.history.delete";
    }
}
