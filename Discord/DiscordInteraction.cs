namespace SmokyPluginV2.Discord
{
    internal sealed class DiscordInteraction
    {
        public ulong Id { get; set; }

        public string Token { get; set; }

        public ulong GuildId { get; set; }

        public ulong UserId { get; set; }

        public ulong TargetDiscordUserId { get; set; }

        public string SteamId { get; set; }

        public string StatisticsVisibility { get; set; }

        public string CommandName { get; set; }
    }

    internal sealed class DiscordInteractionResponse
    {
        public string Content { get; set; }

        public DiscordEmbed Embed { get; set; }

        public bool Ephemeral { get; set; } = true;
    }

    internal sealed class DiscordEmbed
    {
        public string Title { get; set; }

        public string Description { get; set; }

        public int Color { get; set; } = 0x5865F2;

        public DiscordEmbedField[] Fields { get; set; } = System.Array.Empty<DiscordEmbedField>();

        public string Footer { get; set; }
    }

    internal sealed class DiscordEmbedField
    {
        public string Name { get; set; }

        public string Value { get; set; }

        public bool Inline { get; set; }
    }

    internal sealed class DiscordGuildMemberResult
    {
        public bool IsSuccess { get; set; }

        public bool IsGuildMember { get; set; }

        public ulong[] RoleIds { get; set; } = System.Array.Empty<ulong>();

        public string Error { get; set; }
    }
}
