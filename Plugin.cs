namespace SmokyPluginV2
{
    using System;
    using System.Reflection;

    using Exiled.API.Features;

    using HarmonyLib;

    using SmokyPluginV2.AccountLinks;
    using SmokyPluginV2.Discord;

    /// <summary>
    /// Main EXILED plugin entry point.
    /// </summary>
    public sealed class Plugin : Plugin<Config>
    {
        private Handlers.EmptyRoundHandler emptyRoundHandler;
        private Handlers.LateJoinSpawnHandler lateJoinSpawnHandler;
        private Handlers.PinkCandyHandler pinkCandyHandler;
        private AccountLinkService accountLinkService;
        private Handlers.DiscordGameEventHandler discordGameEventHandler;
        private Handlers.DiscordModerationHandler discordModerationHandler;
        private DiscordLogService discordLogService;
        private Harmony harmony;
        private Warnings.WarningService warningService;

        /// <summary>
        /// Gets the currently loaded plugin instance.
        /// </summary>
        public static Plugin Instance { get; private set; }

        internal Warnings.WarningService WarningService => warningService;

        internal AccountLinkService AccountLinks => accountLinkService;

        internal DiscordLogService DiscordLogs => discordLogService;

        internal Handlers.LateJoinSpawnHandler LateJoinSpawns => lateJoinSpawnHandler;

        /// <inheritdoc />
        public override string Name => "SmokyPluginV2";

        /// <inheritdoc />
        public override string Prefix => "smoky_plugin_v2";

        /// <inheritdoc />
        public override string Author => "Smoky";

        /// <inheritdoc />
        public override Version Version => new(0, 10, 0);

        /// <inheritdoc />
        public override Version RequiredExiledVersion => new(9, 14, 2);

        /// <inheritdoc />
        public override void OnEnabled()
        {
            Instance = this;

            if (Config.Warnings?.IsEnabled == true)
                warningService = new Warnings.WarningService();

            if (Config.Discord?.AccountLinking?.IsEnabled == true)
                accountLinkService = new AccountLinkService();

            emptyRoundHandler = new Handlers.EmptyRoundHandler();
            Exiled.Events.Handlers.Player.Left += emptyRoundHandler.OnLeft;

            if (Config.LateJoinSpawn?.IsEnabled == true)
            {
                lateJoinSpawnHandler = new Handlers.LateJoinSpawnHandler();
                lateJoinSpawnHandler.Register();
            }

            if (Config.PinkCandy?.IsEnabled == true)
            {
                pinkCandyHandler = new Handlers.PinkCandyHandler();
                pinkCandyHandler.Register();
            }

            try
            {
                harmony = new Harmony($"smoky.smokypluginv2.{Assembly.GetExecutingAssembly().GetName().Version}");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.Info("[SmokyPluginV2] Runtime patches have been enabled.");
            }
            catch (Exception exception)
            {
                Log.Error($"[SmokyPluginV2] Runtime patches could not be enabled:\n{exception}");
                harmony?.UnpatchAll(harmony.Id);
                harmony = null;
            }

            if (Config.Discord?.IsEnabled == true)
            {
                if (string.IsNullOrWhiteSpace(Config.Discord.Token))
                {
                    Log.Warn("[Discord] Bot is enabled, but discord.token is empty. The plugin will continue without Discord.");
                }
                else if (Config.Discord.GuildId == 0)
                {
                    Log.Error("[Discord] discord.guild_id is 0. The bot was not started.");
                }
                else
                {
                    if (Config.Discord.RemoteAdminChannelId == 0)
                        Log.Warn("[Discord] discord.remote_admin_channel_id is 0. Remote Admin command logs will not be sent.");

                    if (Config.Discord.ModerationChannelId == 0)
                        Log.Warn("[Discord] discord.moderation_channel_id is 0. Moderation logs, including warnings, will not be sent.");

                    if ((Config.Discord.ListenForCommands || Config.Discord.AccountLinking?.IsEnabled == true) &&
                        (Config.Discord.RoleGroups is null || !Config.Discord.RoleGroups.Exists(mapping => mapping != null && mapping.DiscordRoleId != 0 && !string.IsNullOrWhiteSpace(mapping.RemoteAdminGroup))))
                        Log.Warn("[Discord] discord.role_groups has no valid mappings. Discord RA commands will be denied and linked players will not receive synchronized groups.");

                    discordLogService = new DiscordLogService(Config.Discord);
                    discordLogService.Start();

                    discordGameEventHandler = new Handlers.DiscordGameEventHandler();
                    discordGameEventHandler.Register();

                    discordModerationHandler = new Handlers.DiscordModerationHandler();
                    discordModerationHandler.Register();

                }
            }

            base.OnEnabled();
        }

        /// <inheritdoc />
        public override void OnDisabled()
        {
            warningService?.Dispose();
            warningService = null;

            discordGameEventHandler?.Unregister();
            discordGameEventHandler = null;

            discordModerationHandler?.Unregister();
            discordModerationHandler = null;

            if (harmony is not null)
            {
                harmony.UnpatchAll(harmony.Id);
                harmony = null;
            }

            discordLogService?.Dispose();
            discordLogService = null;

            accountLinkService?.Dispose();
            accountLinkService = null;

            if (emptyRoundHandler is not null)
            {
                Exiled.Events.Handlers.Player.Left -= emptyRoundHandler.OnLeft;
                emptyRoundHandler = null;
            }

            lateJoinSpawnHandler?.Unregister();
            lateJoinSpawnHandler = null;

            pinkCandyHandler?.Unregister();
            pinkCandyHandler = null;

            Instance = null;
            base.OnDisabled();
        }
    }
}
