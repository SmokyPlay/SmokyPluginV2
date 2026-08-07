namespace SmokyPluginV2
{
    using System;
    using System.Reflection;

    using Exiled.API.Features;

    using HarmonyLib;

    using SmokyPluginV2.AccountLinks;
    using SmokyPluginV2.Database;
    using SmokyPluginV2.Discord;
    using SmokyPluginV2.Privileges;
    using SmokyPluginV2.Referrals;
    using SmokyPluginV2.RolePreferences;
    using SmokyPluginV2.Statistics;
    using SmokyPluginV2.Moderation;

    using UnityEngine;

    /// <summary>
    /// Main EXILED plugin entry point.
    /// </summary>
    public sealed class Plugin : Plugin<Config>
    {
        private Handlers.EmptyRoundHandler emptyRoundHandler;
        private Handlers.EndRoundFriendlyFireHandler endRoundFriendlyFireHandler;
        private Handlers.LateJoinSpawnHandler lateJoinSpawnHandler;
        private Handlers.PinkCandyHandler pinkCandyHandler;
        private Handlers.GeneralBroadcastHandler generalBroadcastHandler;
        private RolePreferenceService rolePreferenceService;
        private AccountLinkService accountLinkService;
        private PostgreSqlService databaseService;
        private StatisticsService statisticsService;
        private Handlers.DiscordGameEventHandler discordGameEventHandler;
        private Handlers.DiscordModerationHandler discordModerationHandler;
        private DiscordLogService discordLogService;
        private DiscordRoleSynchronizationService discordRoleSynchronizationService;
        private PlayerPrivilegeService playerPrivilegeService;
        private PlayerAccessService playerAccessService;
        private ReferralService referralService;
        private Harmony harmony;
        private PunishmentService punishmentService;
        private WarningNotificationService warningNotificationService;

        /// <summary>
        /// Gets the currently loaded plugin instance.
        /// </summary>
        public static Plugin Instance { get; private set; }

        internal PunishmentService Punishments => punishmentService;

        internal WarningNotificationService WarningNotifications => warningNotificationService;

        internal AccountLinkService AccountLinks => accountLinkService;

        internal PostgreSqlService Database => databaseService;

        internal StatisticsService Statistics => statisticsService;

        internal DiscordLogService DiscordLogs => discordLogService;

        internal DiscordRoleSynchronizationService DiscordRoles => discordRoleSynchronizationService;

        internal PlayerPrivilegeService PlayerPrivileges => playerPrivilegeService;

        internal PlayerAccessService PlayerAccess => playerAccessService;

        internal ReferralService Referrals => referralService;

        internal Handlers.LateJoinSpawnHandler LateJoinSpawns => lateJoinSpawnHandler;

        internal RolePreferenceService RolePreferences => rolePreferenceService;

        /// <inheritdoc />
        public override string Name => "SmokyPluginV2";

        /// <inheritdoc />
        public override string Prefix => "smoky_plugin_v2";

        /// <inheritdoc />
        public override string Author => "Smoky";

        /// <inheritdoc />
        public override Version Version => new(0, 27, 2);

        /// <inheritdoc />
        public override Version RequiredExiledVersion => new(9, 14, 2);

        /// <inheritdoc />
        public override void OnEnabled()
        {
            Instance = this;

            if (Config.Database?.IsEnabled == true)
            {
                try
                {
                    SharedDatabaseSettings sharedDatabaseSettings = SharedDatabaseConfig.Load();
                    databaseService = new PostgreSqlService(sharedDatabaseSettings, Config.Database.ServerName);
                    databaseService.PlayerStatisticsChanged += OnPlayerStatisticsChanged;
                    referralService = new ReferralService(
                        databaseService,
                        Config.EarnedPrivileges?.Referrals);
                    referralService.Register();
                    bool importLegacyYaml = Config.Database.ImportLegacyYaml;
                    if (Config.Warnings?.IsEnabled == true)
                        punishmentService = new PunishmentService(databaseService, importLegacyYaml);
                    if (Config.Discord?.AccountLinking?.IsEnabled == true)
                        accountLinkService = new AccountLinkService(databaseService, importLegacyYaml);
                    if (Config.Statistics?.IsEnabled == true)
                    {
                        statisticsService = new StatisticsService(databaseService);
                        statisticsService.Register();
                    }
                }
                catch (Exception exception)
                {
                    Log.Error($"[Database] PostgreSQL initialization failed. Statistics, warnings and account links are unavailable; other plugin features will continue:\n{exception}");
                    statisticsService?.Dispose();
                    statisticsService = null;
                    punishmentService?.Dispose();
                    punishmentService = null;
                    accountLinkService?.Dispose();
                    accountLinkService = null;
                    referralService?.Dispose();
                    referralService = null;
                    if (databaseService != null)
                        databaseService.PlayerStatisticsChanged -= OnPlayerStatisticsChanged;
                    databaseService?.Dispose();
                    databaseService = null;
                }
            }
            else if (Config.Statistics?.IsEnabled == true || Config.Warnings?.IsEnabled == true || Config.Discord?.AccountLinking?.IsEnabled == true)
            {
                Log.Warn("[Database] PostgreSQL is disabled. Statistics, warnings and account links will not be started.");
            }

            emptyRoundHandler = new Handlers.EmptyRoundHandler();
            Exiled.Events.Handlers.Player.Left += emptyRoundHandler.OnLeft;

            if (Config.EndRoundFriendlyFire?.IsEnabled == true)
            {
                endRoundFriendlyFireHandler = new Handlers.EndRoundFriendlyFireHandler();
                endRoundFriendlyFireHandler.Register();
            }

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

            generalBroadcastHandler = new Handlers.GeneralBroadcastHandler();
            generalBroadcastHandler.Register();

            if (Config.RolePreferences?.IsEnabled == true)
            {
                rolePreferenceService = new RolePreferenceService(Config.RolePreferences);
                rolePreferenceService.Register();
            }

            try
            {
                harmony = new Harmony($"smoky.smokypluginv2.{Assembly.GetExecutingAssembly().GetName().Version}");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                rolePreferenceService?.SetRuntimePatchesAvailable(true);
                Log.Info("[SmokyPluginV2] Runtime patches have been enabled.");
            }
            catch (Exception exception)
            {
                Log.Error($"[SmokyPluginV2] Runtime patches could not be enabled:\n{exception}");
                harmony?.UnpatchAll(harmony.Id);
                harmony = null;
                rolePreferenceService?.SetRuntimePatchesAvailable(false);
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

                }
            }

            if (punishmentService != null || discordLogService != null)
            {
                discordModerationHandler = new Handlers.DiscordModerationHandler(punishmentService);
                discordModerationHandler.Register();
            }

            if (punishmentService != null)
            {
                warningNotificationService = new WarningNotificationService(punishmentService);
                warningNotificationService.Register();
            }

            if (databaseService != null)
            {
                playerPrivilegeService = new PlayerPrivilegeService(
                    databaseService,
                    Config.EarnedPrivileges);
                if (discordLogService != null)
                    discordRoleSynchronizationService = new DiscordRoleSynchronizationService(discordLogService);
                playerAccessService = new PlayerAccessService(
                    playerPrivilegeService,
                    discordRoleSynchronizationService,
                    Config.Discord);
                playerAccessService.Register();
                playerAccessService.RefreshOnlinePlayers();
            }

            base.OnEnabled();
        }

        /// <inheritdoc />
        public override void OnDisabled()
        {
            warningNotificationService?.Unregister();
            warningNotificationService = null;

            statisticsService?.Dispose();
            statisticsService = null;

            playerAccessService?.Dispose();
            playerAccessService = null;
            discordRoleSynchronizationService = null;
            playerPrivilegeService = null;
            referralService?.Dispose();
            referralService = null;

            punishmentService?.Dispose();
            punishmentService = null;

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

            if (databaseService != null)
                databaseService.PlayerStatisticsChanged -= OnPlayerStatisticsChanged;
            databaseService?.Dispose();
            databaseService = null;

            if (emptyRoundHandler is not null)
            {
                Exiled.Events.Handlers.Player.Left -= emptyRoundHandler.OnLeft;
                emptyRoundHandler = null;
            }

            endRoundFriendlyFireHandler?.Unregister();
            endRoundFriendlyFireHandler = null;

            lateJoinSpawnHandler?.Unregister();
            lateJoinSpawnHandler = null;

            pinkCandyHandler?.Unregister();
            pinkCandyHandler = null;

            generalBroadcastHandler?.Unregister();
            generalBroadcastHandler = null;

            rolePreferenceService?.Unregister();
            rolePreferenceService = null;

            Instance = null;
            base.OnDisabled();
        }

        private void OnPlayerStatisticsChanged()
        {
            rolePreferenceService?.RequestTowerLeaderboardRefresh();
        }

        internal void ReloadRolePreferenceConfiguration()
        {
            RolePreferenceService replacement = null;
            try
            {
                if (Config?.RolePreferences?.IsEnabled == true)
                {
                    replacement = new RolePreferenceService(Config.RolePreferences);
                    replacement.Register();
                    replacement.SetRuntimePatchesAvailable(harmony is not null);
                }
            }
            catch (Exception exception)
            {
                replacement?.Unregister();
                Log.Error($"[Role Preferences] Reloaded configuration is invalid. The current runtime configuration remains active:\n{exception}");
                return;
            }

            RolePreferenceService previous = rolePreferenceService;
            Vector3? preservedNativeTutorialSpawn = null;
            if (previous?.TryGetLobbyAnchor(out Vector3 previousLobbyAnchor) == true)
                preservedNativeTutorialSpawn = previousLobbyAnchor;

            previous?.Unregister();
            rolePreferenceService = replacement;

            try
            {
                replacement?.ResumeLobbyAfterConfigReload(preservedNativeTutorialSpawn);
                Log.Info(replacement is null
                    ? "[Role Preferences] Runtime configuration reloaded; the role preference system is disabled."
                    : "[Role Preferences] Runtime configuration reloaded successfully.");
            }
            catch (Exception exception)
            {
                Log.Error($"[Role Preferences] Configuration reloaded, but the tower could not be recreated in the current lobby. It will start normally in the next lobby:\n{exception}");
            }
        }

        internal void ApplyReloadedConfiguration()
        {
            if (databaseService != null)
            {
                if (databaseService.TryUpdateServerName(Config?.Database?.ServerName, out string error))
                    Log.Info($"[Database] Server display name reloaded: {databaseService.ServerName}.");
                else
                    Log.Error($"[Database] Could not reload the server display name: {error}");
            }

            ReloadRolePreferenceConfiguration();
            discordLogService?.ReloadRoleGroupMappings(Config?.Discord);
            referralService?.ReloadSettings(Config?.EarnedPrivileges?.Referrals);
            playerAccessService?.ReloadSettings(Config?.EarnedPrivileges, Config?.Discord);
        }
    }
}
