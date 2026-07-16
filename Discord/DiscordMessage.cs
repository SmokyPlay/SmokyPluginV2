namespace SmokyPluginV2.Discord
{
    internal sealed class DiscordMessage
    {
        public ulong GuildId { get; set; }

        public ulong ChannelId { get; set; }

        public ulong AuthorId { get; set; }

        public string AuthorName { get; set; }

        public ulong[] AuthorRoleIds { get; set; }

        public string Content { get; set; }
    }
}
