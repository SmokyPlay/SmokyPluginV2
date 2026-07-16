namespace SmokyPluginV2.Discord
{
    internal sealed class DiscordInteraction
    {
        public ulong Id { get; set; }

        public string Token { get; set; }

        public ulong GuildId { get; set; }

        public ulong UserId { get; set; }

        public string CommandName { get; set; }
    }

    internal sealed class DiscordInteractionResponse
    {
        public string Content { get; set; }

        public bool Ephemeral { get; set; } = true;
    }

    internal sealed class DiscordGuildMemberResult
    {
        public bool IsSuccess { get; set; }

        public bool IsGuildMember { get; set; }

        public ulong[] RoleIds { get; set; } = System.Array.Empty<ulong>();

        public string Error { get; set; }
    }
}
