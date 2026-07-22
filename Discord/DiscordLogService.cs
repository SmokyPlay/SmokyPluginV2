namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Exiled.API.Extensions;
    using Exiled.API.Features;

    using RemoteAdmin;
    using Respawning.Waves;

    using SmokyPluginV2.AccountLinks;
    using SmokyPluginV2.Database;
    using SmokyPluginV2.Statistics;

    internal sealed class DiscordLogService : IDisposable
    {
        private static readonly MethodInfo RemoteAdminProcessQuery = typeof(CommandProcessor).GetMethod(
            "ProcessQuery",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(CommandSender) },
            null);

        internal const int GameColor = 0x2ECC71;
        internal const int ModerationColor = 0xE67E22;
        internal const int PunishmentColor = 0xE74C3C;

        private readonly DiscordSettings settings;
        private readonly DiscordBotClient client;
        private readonly ConcurrentQueue<string> gameLines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> remoteAdminLines = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<string, string> synchronizedGroups = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object gameFlushLock = new object();
        private readonly object remoteAdminFlushLock = new object();
        private Timer statusTimer;
        private Timer gameFlushTimer;
        private bool disposed;

        public DiscordLogService(DiscordSettings settings)
        {
            this.settings = settings;
            client = new DiscordBotClient(settings);
            client.PrefixedMessageReceived += OnPrefixedMessageReceived;
            client.InteractionReceived += OnInteractionReceived;
        }

        public static DiscordLogService Current { get; private set; }

        public static DiscordEventLogs EventSettings => Plugin.Instance?.Config?.DiscordEventLogs;

        public void Start()
        {
            Current = this;
            client.Start();

            int interval = Math.Max(5, settings.StatusUpdateInterval);
            statusTimer = new Timer(_ => UpdatePresence(), null, TimeSpan.Zero, TimeSpan.FromSeconds(interval));
            gameFlushTimer = new Timer(_ => FlushBufferedLines(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public void LogGameLine(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                gameLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        internal void LogRespawnWave(SpawnableWaveBase wave, int playerCount)
        {
            if (wave is null)
                return;

            string waveType = wave.GetType().Name;
            bool isMiniWave = waveType.IndexOf("Mini", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isChaos = waveType.IndexOf("Chaos", StringComparison.OrdinalIgnoreCase) >= 0;
            DiscordEventLogs eventSettings = EventSettings;

            if (eventSettings is null ||
                (isMiniWave ? !eventSettings.RespawnedReinforcements : !eventSettings.RespawnedMainWave))
            {
                return;
            }

            string team = isChaos ? "Chaos Insurgency" : "Nine-Tailed Fox";
            string emoji = isChaos ? ":spy:" : ":cop:";
            string message = isMiniWave
                ? $"{emoji} {team} reinforcements have arrived with {playerCount} players."
                : $"{emoji} {team} has spawned with {playerCount} players.";

            Log.Info($"[Discord Events] {waveType} spawned with {playerCount} player(s).");
            LogGameLine(message);
        }

        public void LogRemoteAdminCommand(string nickname, string senderId, string role, string command, string arguments)
        {
            if (settings.RemoteAdminChannelId == 0 || string.IsNullOrWhiteSpace(command))
                return;

            string line = $"[{DateTime.Now:HH:mm:ss}] ⌨️ {Escape(nickname)} ({Escape(senderId)}) [{Escape(role)}] used command: {Escape(command)}";
            if (!string.IsNullOrWhiteSpace(arguments))
                line += $" {Escape(arguments)}";

            remoteAdminLines.Enqueue(line);
        }

        public bool LogModeration(string title, string description, bool isPunishment = true)
        {
            if (settings.ModerationChannelId == 0)
                return false;

            client.QueueEmbed(settings.ModerationChannelId, title, description, isPunishment ? PunishmentColor : ModerationColor);
            return true;
        }

        public void UpdatePresence(int? knownPlayerCount = null)
        {
            try
            {
                int players = knownPlayerCount ?? Server.PlayerCount;
                client.UpdatePresence(Math.Max(0, players), Math.Max(0, Server.MaxPlayerCount));
            }
            catch (Exception exception)
            {
                Log.Debug($"[Discord] Could not update presence: {exception.Message}");
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            foreach (Player player in Player.List)
                RemoveSynchronizedGroup(player);

            statusTimer?.Dispose();
            statusTimer = null;
            gameFlushTimer?.Dispose();
            gameFlushTimer = null;
            FlushBufferedLines();
            client.PrefixedMessageReceived -= OnPrefixedMessageReceived;
            client.InteractionReceived -= OnInteractionReceived;
            client.Dispose();

            if (ReferenceEquals(Current, this))
                Current = null;
        }

        public static string PlayerText(Player player)
        {
            if (player is null)
                return "неизвестный игрок";

            return $"**{Escape(player.Nickname)}** (`{Escape(player.UserId)}`), роль: `{player.Role.Type}`";
        }

        public static string Escape(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "неизвестно";

            return value.Replace("`", "ˋ").Replace("@", "@\u200b");
        }

        public void SynchronizeLinkedPlayer(Player player, bool notifyPlayer = false)
        {
            if (disposed || player is null || !player.IsConnected || player.IsHost || settings.AccountLinking?.IsEnabled != true)
                return;

            string playerUserId = player.UserId;
            if (settings.AccountLinking.PreserveNativeGroup &&
                Server.PermissionsHandler?.Members.ContainsKey(playerUserId) == true)
            {
                Log.Debug($"[AccountLinks] Native RA group preserved for {playerUserId}.");
                if (notifyPlayer)
                    player.SendConsoleMessage("Аккаунт привязан. Ваша постоянная группа Remote Admin сохранена и не заменена Discord-группой.", "green");
                return;
            }

            AccountLinkService links = Plugin.Instance?.AccountLinks;
            string error = null;
            ulong discordUserId = 0;
            if (links is null || !links.TryGetDiscordUserId(playerUserId, out discordUserId, out error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Log.Error($"[AccountLinks] Could not resolve link for {playerUserId}: {error}");
                return;
            }

            if (discordUserId == 0)
                return;

            Task.Run(async () =>
            {
                DiscordGuildMemberResult result = await GetGuildMemberResultAsync(discordUserId).ConfigureAwait(false);

                MainThreadDispatcher.Dispatch(
                    () => ApplySynchronizedGroup(playerUserId, discordUserId, result, notifyPlayer),
                    MainThreadDispatcher.DispatchTime.FixedUpdate);
            });
        }

        public void RemoveSynchronizedGroup(Player player)
        {
            string playerUserId = player?.UserId;
            if (string.IsNullOrWhiteSpace(playerUserId) ||
                !synchronizedGroups.TryRemove(playerUserId, out string assignedGroup))
            {
                return;
            }

            string currentGroup = player.Group?.GetKey();
            if (!string.Equals(currentGroup, assignedGroup, StringComparison.OrdinalIgnoreCase))
                return;

            if (Server.PermissionsHandler != null &&
                Server.PermissionsHandler.Members.TryGetValue(playerUserId, out string nativeGroupName) &&
                Server.PermissionsHandler.Groups.TryGetValue(nativeGroupName, out UserGroup nativeGroup))
            {
                player.Group = nativeGroup;
            }
            else
            {
                player.Group = null;
            }
        }

        public void ForgetSynchronizedGroup(Player player)
        {
            string playerUserId = player?.UserId;
            if (!string.IsNullOrWhiteSpace(playerUserId))
                synchronizedGroups.TryRemove(playerUserId, out _);
        }

        internal void ReapplySynchronizedGroups()
        {
            if (disposed || synchronizedGroups.IsEmpty)
                return;

            int reapplied = 0;
            foreach (KeyValuePair<string, string> entry in synchronizedGroups)
            {
                string playerUserId = entry.Key;
                Player player = Player.Get(playerUserId);
                if (player is null || !player.IsConnected)
                {
                    synchronizedGroups.TryRemove(playerUserId, out _);
                    continue;
                }

                if (settings.AccountLinking?.PreserveNativeGroup == true &&
                    Server.PermissionsHandler?.Members.ContainsKey(playerUserId) == true)
                {
                    synchronizedGroups.TryRemove(playerUserId, out _);
                    Log.Debug($"[AccountLinks] Native RA group preserved for {playerUserId} after Remote Admin reload.");
                    continue;
                }

                if (Server.PermissionsHandler is null ||
                    !Server.PermissionsHandler.Groups.TryGetValue(entry.Value, out UserGroup refreshedGroup))
                {
                    synchronizedGroups.TryRemove(playerUserId, out _);
                    Log.Warn($"[AccountLinks] Could not restore Discord RA group '{entry.Value}' for {playerUserId} after Remote Admin reload because the group no longer exists.");
                    continue;
                }

                player.Group = refreshedGroup;
                reapplied++;
            }

            if (reapplied > 0)
                Log.Info($"[AccountLinks] Restored {reapplied} Discord-synchronized RA group(s) after Remote Admin reload.");
        }

        internal void RefreshLinkedPlayerGroups()
        {
            ReapplySynchronizedGroups();

            if (disposed || settings.AccountLinking?.IsEnabled != true)
                return;

            AccountLinkService links = Plugin.Instance?.AccountLinks;
            if (links is null)
                return;

            List<KeyValuePair<string, ulong>> requests = new List<KeyValuePair<string, ulong>>();
            foreach (Player player in Player.List)
            {
                if (player is null || !player.IsConnected || player.IsHost)
                    continue;

                string playerUserId = player.UserId;
                if (string.IsNullOrWhiteSpace(playerUserId))
                    continue;

                if (settings.AccountLinking.PreserveNativeGroup &&
                    Server.PermissionsHandler?.Members.ContainsKey(playerUserId) == true)
                {
                    continue;
                }

                if (links.TryGetDiscordUserId(playerUserId, out ulong discordUserId, out string error) && discordUserId != 0)
                {
                    requests.Add(new KeyValuePair<string, ulong>(playerUserId, discordUserId));
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    Log.Error($"[AccountLinks] Could not resolve link for {playerUserId}: {error}");
                }
            }

            if (requests.Count == 0)
                return;

            Log.Info($"[AccountLinks] Refreshing Discord roles for {requests.Count} linked online player(s) after Remote Admin reload.");
            Task.Run(async () =>
            {
                foreach (KeyValuePair<string, ulong> request in requests)
                {
                    if (disposed)
                        return;

                    DiscordGuildMemberResult result = await GetGuildMemberResultAsync(request.Value).ConfigureAwait(false);
                    string playerUserId = request.Key;
                    ulong discordUserId = request.Value;
                    MainThreadDispatcher.Dispatch(
                        () => ApplySynchronizedGroup(playerUserId, discordUserId, result, false),
                        MainThreadDispatcher.DispatchTime.FixedUpdate);
                }
            });
        }

        internal void ReloadRoleGroupMappings(DiscordSettings reloadedSettings)
        {
            if (disposed || reloadedSettings is null)
                return;

            settings.RoleGroups = reloadedSettings.RoleGroups ?? new List<DiscordRoleGroupMapping>();
            int validMappings = settings.RoleGroups.Count(mapping =>
                mapping != null &&
                mapping.DiscordRoleId != 0 &&
                !string.IsNullOrWhiteSpace(mapping.RemoteAdminGroup));

            Log.Info($"[AccountLinks] Reloaded {validMappings} Discord-to-Remote-Admin role mapping(s) from plugin configuration.");
            RefreshLinkedPlayerGroups();
        }

        private async Task<DiscordGuildMemberResult> GetGuildMemberResultAsync(ulong discordUserId)
        {
            try
            {
                return await client.GetGuildMemberAsync(discordUserId).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return new DiscordGuildMemberResult { Error = exception.Message };
            }
        }

        private void OnPrefixedMessageReceived(DiscordMessage message)
        {
            MainThreadDispatcher.Dispatch(
                () => ExecuteDiscordCommand(message),
                MainThreadDispatcher.DispatchTime.FixedUpdate);
        }

        private DiscordInteractionResponse OnInteractionReceived(DiscordInteraction interaction)
        {
            if (disposed)
                return Ephemeral("Бот завершает работу. Повторите команду после его запуска.");

            if (interaction is null || interaction.GuildId != settings.GuildId || interaction.UserId == 0)
                return Ephemeral("Команда доступна только участникам настроенного Discord-сервера.");

            string commandName = (interaction.CommandName ?? string.Empty).ToLowerInvariant();
            MariaDbService database = Plugin.Instance?.Database;
            if (commandName == "server-stats")
            {
                if (database == null || Plugin.Instance?.Config?.Statistics?.IsEnabled != true)
                    return Ephemeral("Статистика сервера сейчас недоступна.");
                if (!database.TryGetServerStatistics(out ServerStatisticsRecord serverStats, out string serverError))
                    return Ephemeral("❌ " + serverError);
                return new DiscordInteractionResponse
                {
                    Embed = StatisticsEmbedFormatter.Server(serverStats),
                    Ephemeral = false,
                };
            }

            AccountLinkService links = Plugin.Instance?.AccountLinks;
            if (commandName == "stats-privacy")
            {
                if (database == null || Plugin.Instance?.Config?.Statistics?.IsEnabled != true)
                    return Ephemeral("Статистика игроков сейчас недоступна.");

                string visibility = (interaction.StatisticsVisibility ?? string.Empty).Trim().ToLowerInvariant();
                if (visibility != "public" && visibility != "private")
                    return Ephemeral("Выберите открытую или закрытую статистику.");

                bool isPrivate = visibility == "private";
                if (!database.TrySetStatisticsPrivacy(interaction.UserId, isPrivate, out bool accountLinked, out string privacyError))
                    return Ephemeral("❌ " + privacyError);
                if (!accountLinked)
                    return Ephemeral("Сначала привяжите игровой аккаунт командой `/link`.");

                return isPrivate
                    ? Ephemeral("🔒 Ваша статистика теперь недоступна другим пользователям. Вы по-прежнему можете просматривать её самостоятельно.")
                    : Ephemeral("🔓 Ваша статистика теперь доступна другим пользователям.");
            }

            if (commandName == "stats")
            {
                if (database == null || Plugin.Instance?.Config?.Statistics?.IsEnabled != true)
                    return Ephemeral("Статистика игроков сейчас недоступна.");
                bool hasDiscord = interaction.TargetDiscordUserId != 0;
                bool hasSteam = !string.IsNullOrWhiteSpace(interaction.SteamId);
                if (hasDiscord && hasSteam)
                    return Ephemeral("Укажите либо Discord-аккаунт, либо Steam ID, но не оба одновременно.");

                string statsUserId;
                if (hasSteam)
                {
                    string steamId = interaction.SteamId.Trim();
                    if (steamId.Length != 17 || !steamId.All(char.IsDigit) || !ulong.TryParse(steamId, out _))
                        return Ephemeral("Steam ID должен быть корректным 17-значным SteamID64 без `@steam`.");
                    statsUserId = steamId;
                }
                else
                {
                    ulong discordUserId = hasDiscord ? interaction.TargetDiscordUserId : interaction.UserId;
                    if (!database.TryGetPlayerUserId(discordUserId, out statsUserId, out string statsLinkError))
                        return Ephemeral("❌ " + statsLinkError);
                    if (string.IsNullOrWhiteSpace(statsUserId))
                    {
                        return hasDiscord
                            ? Ephemeral("К указанному Discord-аккаунту не привязан Steam ID.")
                            : Ephemeral("Сначала привяжите игровой аккаунт командой `/link` или укажите `steam_id`.");
                    }
                }
                if (!database.TryGetPlayerStatistics(statsUserId, out PlayerStatisticsRecord playerStats, out string statsError))
                    return Ephemeral("❌ " + statsError);
                if (playerStats == null)
                    return Ephemeral("Для этого аккаунта статистика ещё не записана.");

                if (!database.TryGetPlayerUserId(interaction.UserId, out string requesterUserId, out string requesterError))
                    return Ephemeral("❌ " + requesterError);
                bool isOwner = !string.IsNullOrWhiteSpace(requesterUserId) &&
                    string.Equals(
                        MariaDbService.NormalizeSteamId(requesterUserId),
                        MariaDbService.NormalizeSteamId(statsUserId),
                        StringComparison.Ordinal);
                if (playerStats.StatisticsPrivate && !isOwner)
                    return Ephemeral("Этот игрок закрыл доступ к своей статистике.");

                return new DiscordInteractionResponse
                {
                    Embed = StatisticsEmbedFormatter.Player(playerStats, database.ServerName),
                    Ephemeral = false,
                };
            }

            if (links is null || settings.AccountLinking?.IsEnabled != true)
                return Ephemeral("Привязка игровых аккаунтов отключена.");

            switch (commandName)
            {
                case "link":
                    int lifetimeMinutes = Math.Max(1, Math.Min(60, settings.AccountLinking.CodeLifetimeMinutes));
                    if (!links.TryCreateCode(
                            interaction.UserId,
                            TimeSpan.FromMinutes(lifetimeMinutes),
                            out string code,
                            out _,
                            out string linkError))
                    {
                        return Ephemeral($"❌ {linkError}");
                    }

                    return Ephemeral(
                        $"Код действует **{lifetimeMinutes} мин.** Введите в игровой консоли SCP:SL:\n" +
                        $"```text\n.link {code}\n```\n" +
                        "Код одноразовый. Не отправляйте его другим пользователям.");

                case "unlink":
                    if (!links.TryUnlinkDiscord(interaction.UserId, out string playerUserId, out string unlinkError))
                        return Ephemeral($"❌ {unlinkError}");

                    MainThreadDispatcher.Dispatch(
                        () =>
                        {
                            Player onlinePlayer = Player.Get(playerUserId);
                            if (onlinePlayer != null && onlinePlayer.IsConnected)
                                RemoveSynchronizedGroup(onlinePlayer);
                        },
                        MainThreadDispatcher.DispatchTime.FixedUpdate);
                    return Ephemeral($"✅ Игровой аккаунт `{playerUserId}` отвязан от Discord.");

                case "link-status":
                    if (!links.TryGetPlayerUserId(interaction.UserId, out string linkedPlayerUserId, out string statusError))
                        return Ephemeral($"❌ {statusError}");

                    return string.IsNullOrWhiteSpace(linkedPlayerUserId)
                        ? Ephemeral("Ваш Discord пока не привязан. Используйте `/link`.")
                        : Ephemeral($"✅ Discord привязан к игровому аккаунту `{linkedPlayerUserId}`.");

                default:
                    return Ephemeral("Неизвестная slash-команда.");
            }
        }

        private void ExecuteDiscordCommand(DiscordMessage message)
        {
            if (message is null || message.GuildId != settings.GuildId)
                return;

            if (string.IsNullOrWhiteSpace(message.Content))
            {
                SendCommandReply(message.ChannelId, false, "Команда не указана.");
                return;
            }

            if (!TrySelectRoleGroup(message.AuthorRoleIds, out DiscordRoleGroupMapping mapping))
            {
                SendCommandReply(message.ChannelId, false, "У вас нет Discord-роли, связанной с группой Remote Admin.");
                return;
            }

            string groupName = mapping.RemoteAdminGroup.Trim();
            if (Server.PermissionsHandler is null ||
                !Server.PermissionsHandler.Groups.TryGetValue(groupName, out UserGroup group))
            {
                Log.Error($"[Discord] Remote Admin group '{groupName}' is not defined in config_remoteadmin.txt.");
                SendCommandReply(message.ChannelId, false, $"Группа Remote Admin `{groupName}` не найдена на сервере.");
                return;
            }

            DiscordCommandSender sender = new DiscordCommandSender(
                message.AuthorId,
                message.AuthorName,
                groupName,
                group,
                (text, success) => SendCommandReply(message.ChannelId, success, text));

            try
            {
                if (RemoteAdminProcessQuery is null)
                    throw new MissingMethodException(typeof(CommandProcessor).FullName, "ProcessQuery");

                RemoteAdminProcessQuery.Invoke(null, new object[] { message.Content, sender });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                Log.Error($"[Discord] Remote Admin command from {sender.LogName} failed: {exception.InnerException}");
                SendCommandReply(message.ChannelId, false, "При выполнении команды произошла внутренняя ошибка сервера.");
            }
            catch (Exception exception)
            {
                Log.Error($"[Discord] Remote Admin command from {sender.LogName} failed: {exception}");
                SendCommandReply(message.ChannelId, false, "При выполнении команды произошла внутренняя ошибка сервера.");
            }
        }

        private void ApplySynchronizedGroup(
            string playerUserId,
            ulong discordUserId,
            DiscordGuildMemberResult result,
            bool notifyPlayer)
        {
            if (disposed)
                return;

            Player player = Player.Get(playerUserId);
            if (player is null || !player.IsConnected)
                return;

            AccountLinkService links = Plugin.Instance?.AccountLinks;
            if (links is null || !links.TryGetDiscordUserId(playerUserId, out ulong currentDiscordUserId, out _) ||
                currentDiscordUserId != discordUserId)
            {
                return;
            }

            if (settings.AccountLinking.PreserveNativeGroup &&
                Server.PermissionsHandler?.Members.ContainsKey(playerUserId) == true)
            {
                return;
            }

            if (result is null || !result.IsSuccess)
            {
                Log.Error($"[AccountLinks] Discord role synchronization failed for {playerUserId}: {result?.Error ?? "unknown error"}");
                if (notifyPlayer)
                    player.SendConsoleMessage("Аккаунт привязан, но Discord-роли сейчас получить не удалось. Попробуйте переподключиться позже.", "red");
                return;
            }

            if (!result.IsGuildMember)
            {
                RemoveSynchronizedGroup(player);
                if (notifyPlayer)
                    player.SendConsoleMessage("Аккаунт привязан, но вы не состоите в настроенном Discord-сервере.", "yellow");
                return;
            }

            if (!TrySelectRoleGroup(result.RoleIds, out DiscordRoleGroupMapping mapping))
            {
                RemoveSynchronizedGroup(player);
                if (notifyPlayer)
                    player.SendConsoleMessage("Аккаунт привязан, но подходящая Discord-роль для Remote Admin не найдена.", "yellow");
                return;
            }

            string groupName = mapping.RemoteAdminGroup.Trim();
            if (Server.PermissionsHandler is null ||
                !Server.PermissionsHandler.Groups.TryGetValue(groupName, out UserGroup group))
            {
                Log.Error($"[AccountLinks] RA group '{groupName}' mapped from Discord is not defined.");
                if (notifyPlayer)
                    player.SendConsoleMessage($"Discord-роль найдена, но группа Remote Admin '{groupName}' не настроена на сервере.", "red");
                return;
            }

            player.Group = group;
            synchronizedGroups[playerUserId] = groupName;
            Log.Info($"[AccountLinks] Assigned temporary RA group '{groupName}' to {playerUserId} from Discord user {discordUserId}.");
            if (notifyPlayer)
                player.SendConsoleMessage($"Discord-роли синхронизированы. Назначена группа Remote Admin: {groupName}.", "green");
        }

        private static DiscordInteractionResponse Ephemeral(string content) => new DiscordInteractionResponse
        {
            Content = content,
            Ephemeral = true,
        };

        private bool TrySelectRoleGroup(IEnumerable<ulong> memberRoleIds, out DiscordRoleGroupMapping selected)
        {
            selected = null;
            if (settings.RoleGroups is null || settings.RoleGroups.Count == 0 || memberRoleIds is null)
                return false;

            HashSet<ulong> roles = new HashSet<ulong>(memberRoleIds);
            foreach (DiscordRoleGroupMapping mapping in settings.RoleGroups)
            {
                if (mapping is null || mapping.DiscordRoleId == 0 || string.IsNullOrWhiteSpace(mapping.RemoteAdminGroup))
                    continue;

                if (roles.Contains(mapping.DiscordRoleId))
                {
                    selected = mapping;
                    return true;
                }
            }

            return false;
        }

        private void SendCommandReply(ulong channelId, bool success, string response)
        {
            string marker = success ? "✅" : "❌";
            string text = string.IsNullOrWhiteSpace(response) ? "Команда не вернула ответ." : response.Trim();
            client.QueuePriorityText(channelId, $"{marker} {text}");
        }

        private void FlushGameLines()
        {
            if (settings.GameEventsChannelId == 0 || gameLines.IsEmpty)
                return;

            lock (gameFlushLock)
            {
                StringBuilder message = new StringBuilder(1900);
                while (gameLines.TryDequeue(out string line))
                {
                    if (line.Length > 1900)
                        line = line.Substring(0, 1899) + "…";

                    int required = line.Length + (message.Length > 0 ? 1 : 0);
                    if (message.Length > 0 && message.Length + required > 1900)
                    {
                        client.QueueText(settings.GameEventsChannelId, message.ToString());
                        message.Clear();
                    }

                    if (message.Length > 0)
                        message.AppendLine();

                    message.Append(line);
                }

                if (message.Length > 0)
                    client.QueueText(settings.GameEventsChannelId, message.ToString());
            }
        }

        private void FlushRemoteAdminLines()
        {
            if (settings.RemoteAdminChannelId == 0 || remoteAdminLines.IsEmpty)
                return;

            lock (remoteAdminFlushLock)
            {
                StringBuilder message = new StringBuilder(1900);
                while (remoteAdminLines.TryDequeue(out string line))
                {
                    if (line.Length > 1900)
                        line = line.Substring(0, 1899) + "…";

                    int required = line.Length + (message.Length > 0 ? 1 : 0);
                    if (message.Length > 0 && message.Length + required > 1900)
                    {
                        client.QueueText(settings.RemoteAdminChannelId, message.ToString());
                        message.Clear();
                    }

                    if (message.Length > 0)
                        message.AppendLine();

                    message.Append(line);
                }

                if (message.Length > 0)
                    client.QueueText(settings.RemoteAdminChannelId, message.ToString());
            }
        }

        private void FlushBufferedLines()
        {
            FlushGameLines();
            FlushRemoteAdminLines();
        }
    }
}
