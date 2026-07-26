namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Exiled.API.Features;

    internal sealed class DiscordBotClient : IDisposable
    {
        private const string GatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
        private const string ApiBaseUrl = "https://discord.com/api/v10/";
        private const int GatewayConnectTimeoutSeconds = 15;
        private const int MaxQueuedMessages = 2000;
        private const int SafeRestIntervalMilliseconds = 125;
        private const int ConfirmedGlobalRateLimitCooldownSeconds = 3600;
        private const int MissingRetryAfterInitialCooldownSeconds = 60;
        private const int MissingRetryAfterMaximumCooldownSeconds = 900;

        private readonly DiscordSettings settings;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim gatewaySendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim restRequestLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim outboundSignal = new SemaphoreSlim(0);
        private readonly ConcurrentQueue<OutboundMessage> priorityOutbound = new ConcurrentQueue<OutboundMessage>();
        private readonly ConcurrentQueue<OutboundMessage> normalOutbound = new ConcurrentQueue<OutboundMessage>();
        private readonly ConcurrentDictionary<ulong, byte> pendingGuildMembers = new ConcurrentDictionary<ulong, byte>();
        private readonly object restRateLimitLock = new object();
        private readonly object rateLimitNotificationLock = new object();
        private readonly Dictionary<ulong, DateTime> rateLimitNotificationCooldowns = new Dictionary<ulong, DateTime>();
        private readonly HashSet<ulong> blockedMessageChannels = new HashSet<ulong>();
        private readonly HttpClient httpClient;

        private ClientWebSocket socket;
        private DateTime restBlockedUntilUtc;
        private DateTime nextRestRequestUtc;
        private ulong applicationId;
        private int queuedMessages;
        private int droppedMessages;
        private int consecutiveMissingRetryAfterResponses;
        private int slashCommandRegistrationRunning;
        private long? sequence;
        private string presenceText = "0 / 0 в игре";
        private string presenceStatus = "idle";
        private bool disposed;

        public DiscordBotClient(DiscordSettings settings)
        {
            this.settings = settings;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16);

            HttpClientHandler httpHandler = new HttpClientHandler
            {
                // The game server reaches Discord through its routed VPN.
                // Do not let Mono pick up a stale HTTP(S)_PROXY value from
                // the hosting environment.
                UseProxy = false,
            };

            httpClient = new HttpClient(httpHandler)
            {
                BaseAddress = new Uri(ApiBaseUrl),
                // Each request has its own timeout below. This lets transient
                // REST failures be retried without cancelling the bot itself.
                Timeout = Timeout.InfiniteTimeSpan,
            };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", settings.Token);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/SmokyPlay/SmokyPluginV2, 0.19.0)");
        }

        public event Action<DiscordMessage> PrefixedMessageReceived;

        public event Func<DiscordInteraction, DiscordInteractionResponse> InteractionReceived;

        public event Action<DiscordGuildMemberEvent> GuildMemberAvailable;

        public bool IsReady { get; private set; }

        public void Start()
        {
            Task.Run(() => ProcessOutboundQueueAsync(cancellation.Token));
            Task.Run(() => RunGatewayLoopAsync(cancellation.Token));
        }

        public void QueueEmbed(ulong channelId, string title, string description, int color)
        {
            if (channelId == 0 || disposed)
                return;

            EnqueueOutbound(OutboundMessage.Embed(
                channelId,
                Limit(title, 256),
                Limit(description, 4000),
                color),
                false);
        }

        public void QueueText(ulong channelId, string content)
        {
            if (channelId == 0 || disposed || string.IsNullOrWhiteSpace(content))
                return;

            EnqueueOutbound(OutboundMessage.Text(channelId, Limit(content, 1990)), false);
        }

        public void QueuePriorityText(ulong channelId, string content)
        {
            if (channelId == 0 || disposed || string.IsNullOrWhiteSpace(content))
                return;

            EnqueueOutbound(OutboundMessage.Text(channelId, Limit(content, 1990)), true);
        }

        public void UpdatePresence(int players, int maxPlayers)
        {
            presenceText = settings.StatusText
                .Replace("{players}", players.ToString(CultureInfo.InvariantCulture))
                .Replace("{max_players}", maxPlayers.ToString(CultureInfo.InvariantCulture));
            presenceStatus = players > 0 ? "online" : "idle";

            if (IsReady)
                Task.Run(() => SendPresenceAsync(cancellation.Token));
        }

        public async Task<DiscordGuildMemberResult> GetGuildMemberAsync(ulong discordUserId)
        {
            if (discordUserId == 0 || settings.GuildId == 0)
                return new DiscordGuildMemberResult { Error = "Некорректный Discord или Guild ID." };

            int attempts = 0;
            while (!cancellation.IsCancellationRequested && attempts++ < 3)
            {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"guilds/{settings.GuildId}/members/{discordUserId}"))
                using (HttpResponseMessage response = await SendWithHardTimeoutAsync(request, cancellation.Token).ConfigureAwait(false))
                {
                    string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        Dictionary<string, object> member = Json.DeserializeObject(responseText);
                        return new DiscordGuildMemberResult
                        {
                            IsSuccess = true,
                            IsGuildMember = true,
                            RoleIds = ParseRoleIds(member),
                        };
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new DiscordGuildMemberResult
                        {
                            IsSuccess = true,
                            IsGuildMember = false,
                        };
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        Dictionary<string, object> rateLimit = Json.DeserializeObject(responseText);
                        double retrySeconds = rateLimit != null && rateLimit.TryGetValue("retry_after", out object retry)
                            ? Convert.ToDouble(retry, CultureInfo.InvariantCulture)
                            : 1d;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1d, retrySeconds)), cancellation.Token).ConfigureAwait(false);
                        continue;
                    }

                    return new DiscordGuildMemberResult
                    {
                        Error = $"Discord вернул {(int)response.StatusCode}: {responseText}",
                    };
                }
            }

            return new DiscordGuildMemberResult { Error = "Не удалось получить участника Discord после нескольких попыток." };
        }

        public async Task<DiscordRoleAssignmentResult> AddGuildMemberRoleAsync(ulong discordUserId, ulong roleId)
        {
            if (discordUserId == 0 || roleId == 0 || settings.GuildId == 0)
                return new DiscordRoleAssignmentResult { Error = "Некорректный Discord, Guild или Role ID." };

            int attempts = 0;
            while (!cancellation.IsCancellationRequested && attempts++ < 3)
            {
                using (HttpRequestMessage request = new HttpRequestMessage(
                           HttpMethod.Put,
                           $"guilds/{settings.GuildId}/members/{discordUserId}/roles/{roleId}"))
                using (HttpResponseMessage response = await SendWithHardTimeoutAsync(request, cancellation.Token).ConfigureAwait(false))
                {
                    string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return new DiscordRoleAssignmentResult { IsSuccess = true };

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new DiscordRoleAssignmentResult
                        {
                            IsGuildMember = false,
                            Error = "Связанный пользователь не состоит на Discord-сервере или роль не существует.",
                        };
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        Dictionary<string, object> rateLimit = Json.DeserializeObject(responseText);
                        double retrySeconds = rateLimit != null && rateLimit.TryGetValue("retry_after", out object retry)
                            ? Convert.ToDouble(retry, CultureInfo.InvariantCulture)
                            : 1d;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1d, retrySeconds)), cancellation.Token).ConfigureAwait(false);
                        continue;
                    }

                    return new DiscordRoleAssignmentResult
                    {
                        Error = $"Discord вернул {(int)response.StatusCode}: {responseText}",
                    };
                }
            }

            return new DiscordRoleAssignmentResult { Error = "Не удалось назначить Discord-роль после нескольких попыток." };
        }

        public async Task<DiscordRoleAssignmentResult> RemoveGuildMemberRoleAsync(ulong discordUserId, ulong roleId)
        {
            if (discordUserId == 0 || roleId == 0 || settings.GuildId == 0)
                return new DiscordRoleAssignmentResult { Error = "Некорректный Discord, Guild или Role ID." };

            int attempts = 0;
            while (!cancellation.IsCancellationRequested && attempts++ < 3)
            {
                using (HttpRequestMessage request = new HttpRequestMessage(
                           HttpMethod.Delete,
                           $"guilds/{settings.GuildId}/members/{discordUserId}/roles/{roleId}"))
                using (HttpResponseMessage response = await SendWithHardTimeoutAsync(request, cancellation.Token).ConfigureAwait(false))
                {
                    string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return new DiscordRoleAssignmentResult { IsSuccess = true };

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new DiscordRoleAssignmentResult
                        {
                            IsGuildMember = false,
                            Error = "Связанный пользователь не состоит на Discord-сервере или роль не существует.",
                        };
                    }

                    if ((int)response.StatusCode == 429)
                    {
                        Dictionary<string, object> rateLimit = Json.DeserializeObject(responseText);
                        double retrySeconds = rateLimit != null && rateLimit.TryGetValue("retry_after", out object retry)
                            ? Convert.ToDouble(retry, CultureInfo.InvariantCulture)
                            : 1d;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1d, retrySeconds)), cancellation.Token).ConfigureAwait(false);
                        continue;
                    }

                    return new DiscordRoleAssignmentResult
                    {
                        Error = $"Discord вернул {(int)response.StatusCode}: {responseText}",
                    };
                }
            }

            return new DiscordRoleAssignmentResult { Error = "Не удалось снять Discord-роль после нескольких попыток." };
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            cancellation.Cancel();
            try
            {
                outboundSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            IsReady = false;

            try
            {
                socket?.Abort();
                socket?.Dispose();
            }
            catch (Exception exception)
            {
                Log.Debug($"[Discord] Error while closing the gateway: {exception.Message}");
            }

            httpClient.Dispose();
            gatewaySendLock.Dispose();
            cancellation.Dispose();
        }

        private async Task RunGatewayLoopAsync(CancellationToken token)
        {
            int reconnectDelay = 2;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    IsReady = false;
                    sequence = null;
                    socket?.Dispose();
                    socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    socket.Options.Proxy = DirectConnectionProxy.Instance;

                    Log.Info("[Discord] Connecting to the Gateway...");
                    await ConnectGatewayWithTimeoutAsync(socket, token).ConfigureAwait(false);
                    Log.Info("[Discord] Gateway connection established.");
                    reconnectDelay = 2;
                    await ReceiveGatewayAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Log.Error($"[Discord] Gateway connection error: {exception}");
                }

                IsReady = false;
                if (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), token).ConfigureAwait(false);
                    reconnectDelay = Math.Min(reconnectDelay * 2, 30);
                }
            }
        }

        private static async Task ConnectGatewayWithTimeoutAsync(ClientWebSocket gatewaySocket, CancellationToken token)
        {
            using (CancellationTokenSource connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task connectTask = gatewaySocket.ConnectAsync(new Uri(GatewayUrl), connectCancellation.Token);
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(GatewayConnectTimeoutSeconds), token);
                Task completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                if (completed == connectTask)
                {
                    await connectTask.ConfigureAwait(false);
                    return;
                }

                connectCancellation.Cancel();
                try
                {
                    gatewaySocket.Abort();
                }
                catch
                {
                }

                ObserveAbandonedTask(connectTask);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"Discord Gateway connection exceeded the hard {GatewayConnectTimeoutSeconds}-second timeout.");
            }
        }

        private async Task ReceiveGatewayAsync(CancellationToken token)
        {
            byte[] buffer = new byte[16384];

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using (MemoryStream message = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;

                    string payload = Encoding.UTF8.GetString(message.ToArray());
                    await HandleGatewayPayloadAsync(payload, token).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleGatewayPayloadAsync(string payload, CancellationToken token)
        {
            Dictionary<string, object> root = Json.DeserializeObject(payload);
            if (root is null || !root.TryGetValue("op", out object operationValue))
                return;

            if (root.TryGetValue("s", out object sequenceValue) && sequenceValue != null)
                sequence = Convert.ToInt64(sequenceValue, CultureInfo.InvariantCulture);

            int operation = Convert.ToInt32(operationValue, CultureInfo.InvariantCulture);
            switch (operation)
            {
                case 0:
                    HandleDispatch(root);
                    break;
                case 7:
                    socket.Abort();
                    break;
                case 9:
                    await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                    await IdentifyAsync(token).ConfigureAwait(false);
                    break;
                case 10:
                    Dictionary<string, object> hello = Json.Object(root["d"]);
                    int interval = Convert.ToInt32(hello["heartbeat_interval"], CultureInfo.InvariantCulture);
                    ClientWebSocket activeSocket = socket;
                    _ = Task.Run(() => HeartbeatLoopAsync(interval, activeSocket, token));
                    await IdentifyAsync(token).ConfigureAwait(false);
                    break;
                case 11:
                    break;
            }
        }

        private void HandleDispatch(Dictionary<string, object> root)
        {
            string type = root.TryGetValue("t", out object typeValue) ? typeValue as string : null;
            Dictionary<string, object> data = root.TryGetValue("d", out object dataValue) ? Json.Object(dataValue) : null;

            if (type == "READY")
            {
                IsReady = true;
                Dictionary<string, object> application = data != null && data.TryGetValue("application", out object applicationValue)
                    ? Json.Object(applicationValue)
                    : null;
                Dictionary<string, object> botUser = data != null && data.TryGetValue("user", out object userValue)
                    ? Json.Object(userValue)
                    : null;
                applicationId = ParseSnowflake(application, "id");
                if (applicationId == 0)
                    applicationId = ParseSnowflake(botUser, "id");

                Log.Info("[Discord] Bot is ready.");
                Task.Run(() => SendPresenceAsync(cancellation.Token));
                Task.Run(() => RegisterGuildCommandsAsync(cancellation.Token));
                return;
            }

            if (type == "INTERACTION_CREATE" && data != null)
            {
                HandleInteraction(data);
                return;
            }

            if (type == "GUILD_MEMBER_ADD" && data != null)
            {
                HandleGuildMemberAdd(data);
                return;
            }

            if (type == "GUILD_MEMBER_UPDATE" && data != null)
            {
                HandleGuildMemberUpdate(data);
                return;
            }

            if (type == "GUILD_MEMBER_REMOVE" && data != null)
            {
                if (ParseSnowflake(data, "guild_id") != settings.GuildId)
                    return;

                Dictionary<string, object> removedUser = data.TryGetValue("user", out object removedUserValue)
                    ? Json.Object(removedUserValue)
                    : null;
                pendingGuildMembers.TryRemove(ParseSnowflake(removedUser, "id"), out _);
                return;
            }

            if (type != "MESSAGE_CREATE" || !settings.ListenForCommands || data is null)
                return;

            ulong guildId = ParseSnowflake(data, "guild_id");
            string content = data.TryGetValue("content", out object contentValue) ? contentValue as string : null;
            Dictionary<string, object> author = data.TryGetValue("author", out object authorValue) ? Json.Object(authorValue) : null;
            Dictionary<string, object> member = data.TryGetValue("member", out object memberValue) ? Json.Object(memberValue) : null;
            bool isBot = author != null && author.TryGetValue("bot", out object botValue) && Convert.ToBoolean(botValue, CultureInfo.InvariantCulture);

            string prefix = string.IsNullOrEmpty(settings.Prefix) ? "+" : settings.Prefix;
            if (isBot || guildId != settings.GuildId || string.IsNullOrEmpty(content) || !content.StartsWith(prefix, StringComparison.Ordinal))
                return;

            string authorName = author != null && author.TryGetValue("username", out object nameValue) ? nameValue as string : "Unknown";
            if (member != null && member.TryGetValue("nick", out object nicknameValue) && nicknameValue is string nickname && !string.IsNullOrWhiteSpace(nickname))
                authorName = nickname;

            DiscordMessage message = new DiscordMessage
            {
                GuildId = guildId,
                ChannelId = ParseSnowflake(data, "channel_id"),
                AuthorId = ParseSnowflake(author, "id"),
                AuthorName = authorName,
                AuthorRoleIds = ParseRoleIds(member),
                Content = content.Substring(prefix.Length).Trim(),
            };

            // Just like established Discord clients, a message event is
            // detached from the Gateway receive loop immediately.
            Task.Run(() =>
            {
                try
                {
                    PrefixedMessageReceived?.Invoke(message);
                }
                catch (Exception exception)
                {
                    Log.Error($"[Discord] Message handler failed: {exception}");
                }
            });
        }

        private void HandleGuildMemberAdd(Dictionary<string, object> data)
        {
            ulong guildId = ParseSnowflake(data, "guild_id");
            Dictionary<string, object> user = data.TryGetValue("user", out object userValue)
                ? Json.Object(userValue)
                : null;
            ulong userId = ParseSnowflake(user, "id");
            if (guildId != settings.GuildId || userId == 0 || IsBotUser(user))
                return;

            bool pending = data.TryGetValue("pending", out object pendingValue) &&
                pendingValue != null &&
                Convert.ToBoolean(pendingValue, CultureInfo.InvariantCulture);
            if (pending)
            {
                pendingGuildMembers[userId] = 0;
                return;
            }

            RaiseGuildMemberAvailable(guildId, userId);
        }

        private void HandleGuildMemberUpdate(Dictionary<string, object> data)
        {
            ulong guildId = ParseSnowflake(data, "guild_id");
            Dictionary<string, object> user = data.TryGetValue("user", out object userValue)
                ? Json.Object(userValue)
                : null;
            ulong userId = ParseSnowflake(user, "id");
            if (guildId != settings.GuildId || userId == 0 || IsBotUser(user))
                return;

            if (!data.TryGetValue("pending", out object pendingValue) || pendingValue == null)
                return;

            bool pending = Convert.ToBoolean(pendingValue, CultureInfo.InvariantCulture);
            if (!pending && pendingGuildMembers.TryRemove(userId, out _))
                RaiseGuildMemberAvailable(guildId, userId);
        }

        private void RaiseGuildMemberAvailable(ulong guildId, ulong userId)
        {
            Task.Run(() =>
            {
                try
                {
                    GuildMemberAvailable?.Invoke(new DiscordGuildMemberEvent
                    {
                        GuildId = guildId,
                        UserId = userId,
                    });
                }
                catch (Exception exception)
                {
                    Log.Error($"[Discord] Guild member handler failed: {exception}");
                }
            });
        }

        private static bool IsBotUser(Dictionary<string, object> user) =>
            user != null &&
            user.TryGetValue("bot", out object botValue) &&
            botValue != null &&
            Convert.ToBoolean(botValue, CultureInfo.InvariantCulture);

        private void HandleInteraction(Dictionary<string, object> data)
        {
            int interactionType = data.TryGetValue("type", out object typeValue)
                ? Convert.ToInt32(typeValue, CultureInfo.InvariantCulture)
                : 0;
            if (interactionType != 2)
                return;

            Dictionary<string, object> commandData = data.TryGetValue("data", out object commandValue) ? Json.Object(commandValue) : null;
            Dictionary<string, object> member = data.TryGetValue("member", out object memberValue) ? Json.Object(memberValue) : null;
            Dictionary<string, object> user = member != null && member.TryGetValue("user", out object memberUserValue)
                ? Json.Object(memberUserValue)
                : data.TryGetValue("user", out object directUserValue) ? Json.Object(directUserValue) : null;

            DiscordInteraction interaction = new DiscordInteraction
            {
                Id = ParseSnowflake(data, "id"),
                Token = data.TryGetValue("token", out object tokenValue) ? tokenValue as string : null,
                GuildId = ParseSnowflake(data, "guild_id"),
                UserId = ParseSnowflake(user, "id"),
                TargetDiscordUserId = ParseCommandOptionSnowflake(commandData, "discord"),
                SteamId = ParseCommandOptionString(commandData, "steam_id"),
                StatisticsVisibility = ParseCommandOptionString(commandData, "доступ"),
                CommandName = commandData != null && commandData.TryGetValue("name", out object nameValue) ? nameValue as string : null,
            };

            DiscordInteractionResponse interactionResponse;
            try
            {
                interactionResponse = InteractionReceived?.Invoke(interaction) ?? new DiscordInteractionResponse
                {
                    Content = "Эта команда сейчас недоступна.",
                };
            }
            catch (Exception exception)
            {
                Log.Error($"[Discord] Interaction handler failed: {exception}");
                interactionResponse = new DiscordInteractionResponse
                {
                    Content = "При обработке команды произошла внутренняя ошибка.",
                };
            }

            Task.Run(() => SendInteractionResponseAsync(interaction, interactionResponse, cancellation.Token));
        }

        private async Task RegisterGuildCommandsAsync(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref slashCommandRegistrationRunning, 1, 0) != 0)
                return;

            try
            {
                if (applicationId == 0 || settings.GuildId == 0)
                {
                    Log.Error("[Discord] Slash commands were not registered because the application or guild ID is missing.");
                    return;
                }

                List<object> commandList = new List<object>();
                if (settings.AccountLinking?.IsEnabled == true)
                {
                    commandList.Add(new Dictionary<string, object>
                    {
                        ["name"] = "link",
                        ["description"] = "Получить код привязки игрового аккаунта SCP:SL",
                        ["type"] = 1,
                    });
                    commandList.Add(new Dictionary<string, object>
                    {
                        ["name"] = "unlink",
                        ["description"] = "Отвязать игровой аккаунт SCP:SL от Discord",
                        ["type"] = 1,
                    });
                    commandList.Add(new Dictionary<string, object>
                    {
                        ["name"] = "link-status",
                        ["description"] = "Проверить состояние привязки игрового аккаунта",
                        ["type"] = 1,
                    });
                    if (Plugin.Instance?.Config?.EarnedPrivileges?.Referrals?.IsEnabled == true &&
                        Plugin.Instance.Referrals != null)
                    {
                        commandList.Add(new Dictionary<string, object>
                        {
                            ["name"] = "referral",
                            ["description"] = "Показать ваш реферальный код и прогресс приглашений",
                            ["type"] = 1,
                        });
                    }
                }
                if (Plugin.Instance?.Config?.Statistics?.IsEnabled == true && Plugin.Instance.Database != null)
                {
                    commandList.Add(new Dictionary<string, object>
                    {
                        ["name"] = "stats",
                        ["description"] = "Показать игровую статистику игрока",
                        ["type"] = 1,
                        ["options"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["name"] = "discord",
                                ["description"] = "Можно выбрать Discord-аккаунт другого игрока, если он привязан к Steam-аккаунту",
                                ["type"] = 6,
                                ["required"] = false,
                            },
                            new Dictionary<string, object>
                            {
                                ["name"] = "steam_id",
                                ["description"] = "Или указать его SteamID64 без @steam",
                                ["type"] = 3,
                                ["required"] = false,
                                ["min_length"] = 17,
                                ["max_length"] = 17,
                            },
                        },
                    });
                    commandList.Add(new Dictionary<string, object>
                    {
                        ["name"] = "server-stats",
                        ["description"] = "Показать общую статистику сервера",
                        ["type"] = 1,
                    });
                    commandList.Add(new Dictionary<string, object>
                    {
                        ["name"] = "stats-privacy",
                        ["description"] = "Настроить доступ других пользователей к своей статистике",
                        ["type"] = 1,
                        ["options"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["name"] = "доступ",
                                ["description"] = "Выберите, кто сможет просматривать вашу статистику",
                                ["type"] = 3,
                                ["required"] = true,
                                ["choices"] = new object[]
                                {
                                    new Dictionary<string, object> { ["name"] = "Открытая", ["value"] = "public" },
                                    new Dictionary<string, object> { ["name"] = "Закрытая", ["value"] = "private" },
                                },
                            },
                        },
                    });
                }
                object[] commands = commandList.ToArray();

                int failedAttempts = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using (HttpRequestMessage request = new HttpRequestMessage(
                            HttpMethod.Put,
                            $"applications/{applicationId}/guilds/{settings.GuildId}/commands"))
                        {
                            request.Content = new StringContent(Json.Serialize(commands), Encoding.UTF8, "application/json");
                            using (HttpResponseMessage response = await SendWithHardTimeoutAsync(request, token, 30).ConfigureAwait(false))
                            {
                                string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (response.IsSuccessStatusCode)
                                {
                                    Log.Info(commands.Length > 0
                                        ? $"[Discord] Registered {commands.Length} guild slash command(s)."
                                        : "[Discord] Plugin slash commands are disabled and removed from the guild.");
                                    return;
                                }

                                if ((int)response.StatusCode == 429)
                                {
                                    Dictionary<string, object> rateLimit = Json.DeserializeObject(responseText);
                                    double retrySeconds = rateLimit != null && rateLimit.TryGetValue("retry_after", out object retry)
                                        ? Convert.ToDouble(retry, CultureInfo.InvariantCulture)
                                        : 5d;
                                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1d, retrySeconds)), token).ConfigureAwait(false);
                                    continue;
                                }

                                if ((int)response.StatusCode == 408 || (int)response.StatusCode >= 500)
                                    throw new HttpRequestException($"Discord returned {(int)response.StatusCode}: {responseText}");

                                Log.Error($"[Discord] Slash commands cannot be registered: Discord returned {(int)response.StatusCode}: {responseText}");
                                return;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception) when (exception is TimeoutException || exception is HttpRequestException)
                    {
                        failedAttempts++;
                        int retrySeconds = Math.Min(60, 5 * (1 << Math.Min(failedAttempts - 1, 3)));
                        Log.Warn($"[Discord] Slash command registration attempt {failedAttempts} failed: {exception.Message} Retrying in {retrySeconds}s.");
                        await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Log.Error($"[Discord] Failed to register guild slash commands: {exception}");
            }
            finally
            {
                Interlocked.Exchange(ref slashCommandRegistrationRunning, 0);
            }
        }

        private async Task SendInteractionResponseAsync(
            DiscordInteraction interaction,
            DiscordInteractionResponse response,
            CancellationToken token)
        {
            try
            {
                Dictionary<string, object> responseData = new Dictionary<string, object>
                {
                    ["flags"] = response?.Ephemeral == false ? 0 : 64,
                    ["allowed_mentions"] = new Dictionary<string, object>
                    {
                        ["parse"] = new object[0],
                    },
                };
                if (!string.IsNullOrWhiteSpace(response?.Content))
                    responseData["content"] = Limit(response.Content, 1900);
                if (response?.Embed != null)
                {
                    DiscordEmbed embed = response.Embed;
                    Dictionary<string, object> embedPayload = new Dictionary<string, object>
                    {
                        ["title"] = Limit(embed.Title, 256),
                        ["description"] = Limit(embed.Description, 4096),
                        ["color"] = embed.Color,
                        ["fields"] = (embed.Fields ?? Array.Empty<DiscordEmbedField>()).Select(field => new Dictionary<string, object>
                        {
                            ["name"] = Limit(field.Name, 256),
                            ["value"] = Limit(field.Value, 1024),
                            ["inline"] = field.Inline,
                        }).ToArray(),
                    };
                    if (!string.IsNullOrWhiteSpace(embed.Footer))
                    {
                        embedPayload["footer"] = new Dictionary<string, object>
                        {
                            ["text"] = Limit(embed.Footer, 2048),
                        };
                    }
                    responseData["embeds"] = new object[] { embedPayload };
                }

                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    ["type"] = 4,
                    ["data"] = responseData,
                };

                using (HttpRequestMessage request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"interactions/{interaction.Id}/{interaction.Token}/callback"))
                {
                    request.Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");
                    using (HttpResponseMessage httpResponse = await SendWithHardTimeoutAsync(request, token).ConfigureAwait(false))
                    {
                        if (!httpResponse.IsSuccessStatusCode)
                        {
                            string responseText = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            Log.Error($"[Discord] Interaction response failed with {(int)httpResponse.StatusCode}: {responseText}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Log.Error($"[Discord] Failed to respond to interaction: {exception}");
            }
        }

        private async Task IdentifyAsync(CancellationToken token)
        {
            int intents = 1 | 2 | 512;
            if (settings.ListenForCommands)
                intents |= 32768;

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["op"] = 2,
                ["d"] = new Dictionary<string, object>
                {
                    ["token"] = settings.Token,
                    ["intents"] = intents,
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["os"] = Environment.OSVersion.Platform.ToString(),
                        ["browser"] = "SmokyPluginV2",
                        ["device"] = "SmokyPluginV2",
                    },
                },
            };

            await SendGatewayAsync(payload, token).ConfigureAwait(false);
        }

        private async Task HeartbeatLoopAsync(int intervalMilliseconds, ClientWebSocket activeSocket, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && ReferenceEquals(socket, activeSocket) && activeSocket.State == WebSocketState.Open)
                {
                    await Task.Delay(intervalMilliseconds, token).ConfigureAwait(false);
                    if (!ReferenceEquals(socket, activeSocket) || activeSocket.State != WebSocketState.Open)
                        return;

                    await SendGatewayAsync(new Dictionary<string, object>
                    {
                        ["op"] = 1,
                        ["d"] = sequence,
                    }, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Log.Debug($"[Discord] Heartbeat stopped: {exception.Message}");
            }
        }

        private Task SendPresenceAsync(CancellationToken token) => SendGatewayAsync(new Dictionary<string, object>
        {
            ["op"] = 3,
            ["d"] = new Dictionary<string, object>
            {
                ["since"] = null,
                ["activities"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = presenceText,
                        ["type"] = 0,
                    },
                },
                ["status"] = presenceStatus,
                ["afk"] = presenceStatus == "idle",
            },
        }, token);

        private async Task SendGatewayAsync(object payload, CancellationToken token)
        {
            if (socket is null || socket.State != WebSocketState.Open)
                return;

            byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(payload));
            await gatewaySendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                gatewaySendLock.Release();
            }
        }

        private void EnqueueOutbound(OutboundMessage message, bool priority)
        {
            lock (restRateLimitLock)
            {
                if (blockedMessageChannels.Contains(message.ChannelId))
                    return;
            }

            int queued = Interlocked.Increment(ref queuedMessages);
            if (queued > MaxQueuedMessages)
            {
                Interlocked.Decrement(ref queuedMessages);
                int dropped = Interlocked.Increment(ref droppedMessages);
                if (dropped == 1 || dropped % 100 == 0)
                    Log.Warn($"[Discord] Outbound log queue is full. {dropped} message(s) will be represented by a later summary.");

                return;
            }

            if (priority)
                priorityOutbound.Enqueue(message);
            else
                normalOutbound.Enqueue(message);

            outboundSignal.Release();
        }

        private async Task ProcessOutboundQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await outboundSignal.WaitAsync(token).ConfigureAwait(false);
                    if (!TryDequeueOutbound(out OutboundMessage message, out bool priority))
                        continue;

                    List<OutboundMessage> batch = new List<OutboundMessage> { message };
                    if (!priority && message.IsEmbed)
                    {
                        int embedCharacters = (message.Title?.Length ?? 0) + (message.Content?.Length ?? 0);
                        while (batch.Count < 10 && normalOutbound.TryPeek(out OutboundMessage next) &&
                               next.IsEmbed && next.ChannelId == message.ChannelId)
                        {
                            int nextCharacters = (next.Title?.Length ?? 0) + (next.Content?.Length ?? 0);
                            if (embedCharacters + nextCharacters > 5800 || !normalOutbound.TryDequeue(out next))
                                break;

                            outboundSignal.Wait(0);
                            Interlocked.Decrement(ref queuedMessages);
                            batch.Add(next);
                            embedCharacters += nextCharacters;
                        }
                    }

                    await SendMessagePayloadAsync(message.ChannelId, BuildPayload(batch), token).ConfigureAwait(false);
                    QueueDroppedMessageSummaryIfReady();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    Log.Error($"[Discord] Outbound message worker recovered from an error: {exception}");
                }
            }
        }

        private bool TryDequeueOutbound(out OutboundMessage message, out bool priority)
        {
            if (priorityOutbound.TryDequeue(out message))
            {
                priority = true;
                Interlocked.Decrement(ref queuedMessages);
                return true;
            }

            if (normalOutbound.TryDequeue(out message))
            {
                priority = false;
                Interlocked.Decrement(ref queuedMessages);
                return true;
            }

            priority = false;
            return false;
        }

        private void QueueDroppedMessageSummaryIfReady()
        {
            if (Volatile.Read(ref queuedMessages) >= MaxQueuedMessages / 2)
                return;

            int dropped = Interlocked.Exchange(ref droppedMessages, 0);
            if (dropped <= 0 || settings.RemoteAdminChannelId == 0)
                return;

            EnqueueOutbound(
                OutboundMessage.Text(
                    settings.RemoteAdminChannelId,
                    $"⚠️ Во время всплеска логов очередь объединила {dropped} избыточных сообщений. Сервер и Discord rate limit защищены."),
                true);
        }

        private static Dictionary<string, object> BuildPayload(IReadOnlyList<OutboundMessage> messages)
        {
            OutboundMessage first = messages[0];
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["allowed_mentions"] = new Dictionary<string, object>
                {
                    ["parse"] = new object[0],
                },
            };

            if (!first.IsEmbed)
            {
                payload["content"] = first.Content;
                return payload;
            }

            object[] embeds = new object[messages.Count];
            for (int index = 0; index < messages.Count; index++)
            {
                OutboundMessage message = messages[index];
                embeds[index] = new Dictionary<string, object>
                {
                    ["title"] = message.Title,
                    ["description"] = message.Content,
                    ["color"] = message.Color,
                    ["timestamp"] = message.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    ["footer"] = new Dictionary<string, object>
                    {
                        ["text"] = "SmokyPluginV2",
                    },
                };
            }

            payload["embeds"] = embeds;
            return payload;
        }

        private async Task SendMessagePayloadAsync(
            ulong channelId,
            Dictionary<string, object> payload,
            CancellationToken token)
        {
            payload["nonce"] = Guid.NewGuid().ToString("N").Substring(0, 25);
            payload["enforce_nonce"] = true;

            await SendMessageWithRetryAsync(channelId, payload, token).ConfigureAwait(false);
        }

        private async Task SendMessageWithRetryAsync(
            ulong channelId,
            Dictionary<string, object> payload,
            CancellationToken token)
        {
            if (IsMessageChannelBlocked(channelId))
                return;

            string json = Json.Serialize(payload);
            int transientFailures = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages"))
                    {
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                        using (HttpResponseMessage response = await SendWithHardTimeoutAsync(request, token).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                await WaitForExhaustedMessageBucketAsync(response, token).ConfigureAwait(false);
                                return;
                            }

                            string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if ((int)response.StatusCode == 429)
                            {
                                double retrySeconds = GetRemainingRestDelaySeconds();
                                NotifyRateLimit(channelId, retrySeconds);
                                await WaitForRestWindowAsync(token).ConfigureAwait(false);
                                continue;
                            }

                            if ((int)response.StatusCode == 408 || (int)response.StatusCode >= 500)
                            {
                                if (++transientFailures >= 3)
                                    throw new HttpRequestException($"Discord returned {(int)response.StatusCode} after {transientFailures} attempts: {responseText}");

                                await Task.Delay(TimeSpan.FromMilliseconds(500 * transientFailures), token).ConfigureAwait(false);
                                continue;
                            }

                            if ((int)response.StatusCode == 401)
                            {
                                BlockRestRequests(ConfirmedGlobalRateLimitCooldownSeconds);
                                Log.Error("[Discord] REST authorization was rejected. All Discord REST requests are paused for one hour to prevent an invalid-request ban.");
                                return;
                            }

                            if ((int)response.StatusCode == 403 || (int)response.StatusCode == 404)
                            {
                                BlockMessageChannel(channelId, response.StatusCode, responseText);
                                return;
                            }

                            if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                            {
                                BlockMessageChannel(channelId, response.StatusCode, responseText);
                                return;
                            }

                            throw new InvalidOperationException($"Discord returned {(int)response.StatusCode}: {responseText}");
                        }
                    }
                }
                catch (TimeoutException exception)
                {
                    if (++transientFailures >= 3)
                        throw new TimeoutException($"Discord message request to channel {channelId} timed out after {transientFailures} attempts.", exception);

                    await Task.Delay(TimeSpan.FromMilliseconds(500 * transientFailures), token).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (transientFailures < 2)
                {
                    transientFailures++;
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * transientFailures), token).ConfigureAwait(false);
                }
            }
        }

        private static async Task WaitForExhaustedMessageBucketAsync(HttpResponseMessage response, CancellationToken token)
        {
            if (!TryGetRateLimitHeader(response, "X-RateLimit-Remaining", out double remaining) || remaining > 0 ||
                !TryGetRateLimitHeader(response, "X-RateLimit-Reset-After", out double resetAfter) || resetAfter <= 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(resetAfter + 0.1d), token).ConfigureAwait(false);
        }

        private static bool TryGetRateLimitHeader(HttpResponseMessage response, string name, out double value)
        {
            value = 0;
            if (response?.Headers is null || !response.Headers.TryGetValues(name, out IEnumerable<string> values))
                return false;

            string raw = null;
            foreach (string candidate in values)
            {
                raw = candidate;
                break;
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private async Task<HttpResponseMessage> SendWithHardTimeoutAsync(
            HttpRequestMessage request,
            CancellationToken token,
            int timeoutSeconds = 10)
        {
            await restRequestLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await WaitForRestWindowAsync(token).ConfigureAwait(false);

                using (CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    Task<HttpResponseMessage> sendTask = httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        requestCancellation.Token);
                    Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), token);
                    Task completed = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);

                    if (completed == sendTask)
                    {
                        HttpResponseMessage response = await sendTask.ConfigureAwait(false);
                        lock (restRateLimitLock)
                            nextRestRequestUtc = DateTime.UtcNow.AddMilliseconds(SafeRestIntervalMilliseconds);

                        if ((int)response.StatusCode == 429)
                            await ApplyGlobalRateLimitAsync(response).ConfigureAwait(false);
                        else
                            Interlocked.Exchange(ref consecutiveMissingRetryAfterResponses, 0);

                        return response;
                    }

                    requestCancellation.Cancel();
                    ObserveAbandonedRequest(sendTask);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException($"Discord HTTP request exceeded the hard {timeoutSeconds}-second timeout.");
                }
            }
            finally
            {
                restRequestLock.Release();
            }
        }

        private async Task WaitForRestWindowAsync(CancellationToken token)
        {
            while (true)
            {
                TimeSpan delay;
                lock (restRateLimitLock)
                {
                    DateTime allowedAt = restBlockedUntilUtc > nextRestRequestUtc ? restBlockedUntilUtc : nextRestRequestUtc;
                    delay = allowedAt - DateTime.UtcNow;
                }

                if (delay <= TimeSpan.Zero)
                    return;

                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }

        private async Task ApplyGlobalRateLimitAsync(HttpResponseMessage response)
        {
            string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            double retrySeconds = GetRetryAfterSeconds(
                response,
                responseText,
                out bool suppliedByDiscord,
                out bool confirmedGlobalBlock,
                out int fallbackAttempt);
            BlockRestRequests(retrySeconds);

            response.Content.Dispose();
            response.Content = new StringContent(responseText ?? string.Empty, Encoding.UTF8, "application/json");

            if (confirmedGlobalBlock)
            {
                Log.Error("[Discord] Discord confirmed a temporary global API block without Retry-After. All REST traffic is paused for one hour.");
            }
            else if (!suppliedByDiscord)
            {
                Log.Warn($"[Discord] Discord returned 429 without Retry-After. All REST traffic is paused for {Math.Ceiling(retrySeconds):0}s (fallback attempt {fallbackAttempt}).");
            }
        }

        private void BlockRestRequests(double retrySeconds)
        {
            DateTime blockedUntil = DateTime.UtcNow.AddSeconds(Math.Max(0.25d, retrySeconds) + 0.25d);
            lock (restRateLimitLock)
            {
                if (blockedUntil > restBlockedUntilUtc)
                    restBlockedUntilUtc = blockedUntil;
            }
        }

        private double GetRemainingRestDelaySeconds()
        {
            lock (restRateLimitLock)
                return Math.Max(0.1d, (restBlockedUntilUtc - DateTime.UtcNow).TotalSeconds);
        }

        private double GetRetryAfterSeconds(
            HttpResponseMessage response,
            string responseText,
            out bool suppliedByDiscord,
            out bool confirmedGlobalBlock,
            out int fallbackAttempt)
        {
            suppliedByDiscord = false;
            confirmedGlobalBlock = false;
            fallbackAttempt = 0;
            try
            {
                Dictionary<string, object> rateLimit = Json.DeserializeObject(responseText);
                if (rateLimit != null && rateLimit.TryGetValue("retry_after", out object retry))
                {
                    double seconds = Convert.ToDouble(retry, CultureInfo.InvariantCulture);
                    if (seconds >= 0)
                    {
                        suppliedByDiscord = true;
                        Interlocked.Exchange(ref consecutiveMissingRetryAfterResponses, 0);
                        return Math.Max(0.1d, seconds);
                    }
                }
            }
            catch
            {
            }

            if (response?.Headers?.RetryAfter?.Delta is TimeSpan delta && delta >= TimeSpan.Zero)
            {
                suppliedByDiscord = true;
                Interlocked.Exchange(ref consecutiveMissingRetryAfterResponses, 0);
                return Math.Max(0.1d, delta.TotalSeconds);
            }

            if (response?.Headers?.RetryAfter?.Date is DateTimeOffset date)
            {
                suppliedByDiscord = true;
                Interlocked.Exchange(ref consecutiveMissingRetryAfterResponses, 0);
                return Math.Max(0.1d, (date - DateTimeOffset.UtcNow).TotalSeconds);
            }

            if (IsConfirmedGlobalApiBlock(responseText))
            {
                confirmedGlobalBlock = true;
                Interlocked.Exchange(ref consecutiveMissingRetryAfterResponses, 0);
                return ConfirmedGlobalRateLimitCooldownSeconds;
            }

            fallbackAttempt = Interlocked.Increment(ref consecutiveMissingRetryAfterResponses);
            int exponent = Math.Min(fallbackAttempt - 1, 4);
            return Math.Min(
                MissingRetryAfterMaximumCooldownSeconds,
                MissingRetryAfterInitialCooldownSeconds * (1 << exponent));
        }

        private static bool IsConfirmedGlobalApiBlock(string responseText) =>
            !string.IsNullOrWhiteSpace(responseText) &&
            responseText.IndexOf("being blocked from accessing our API temporarily", StringComparison.OrdinalIgnoreCase) >= 0 &&
            responseText.IndexOf("global rate limits", StringComparison.OrdinalIgnoreCase) >= 0;

        private bool IsMessageChannelBlocked(ulong channelId)
        {
            lock (restRateLimitLock)
                return blockedMessageChannels.Contains(channelId);
        }

        private void BlockMessageChannel(ulong channelId, HttpStatusCode statusCode, string responseText)
        {
            bool added;
            lock (restRateLimitLock)
                added = blockedMessageChannels.Add(channelId);

            if (added)
                Log.Error($"[Discord] Channel {channelId} rejected log delivery with {(int)statusCode}. Further messages to this channel are disabled until restart: {responseText}");
        }

        private static void ObserveAbandonedRequest(Task<HttpResponseMessage> sendTask)
        {
            sendTask.ContinueWith(
                task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                        task.Result.Dispose();
                    else if (task.IsFaulted)
                    {
                        AggregateException ignored = task.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void ObserveAbandonedTask(Task task)
        {
            task.ContinueWith(
                completedTask =>
                {
                    if (completedTask.IsFaulted)
                    {
                        AggregateException ignored = completedTask.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private sealed class DirectConnectionProxy : IWebProxy
        {
            public static readonly DirectConnectionProxy Instance = new DirectConnectionProxy();

            private DirectConnectionProxy()
            {
            }

            public ICredentials Credentials { get; set; }

            public Uri GetProxy(Uri destination) => destination;

            public bool IsBypassed(Uri host) => true;
        }

        private sealed class OutboundMessage
        {
            private OutboundMessage(ulong channelId, string title, string content, int color, bool isEmbed)
            {
                ChannelId = channelId;
                Title = title;
                Content = content;
                Color = color;
                IsEmbed = isEmbed;
                CreatedAtUtc = DateTime.UtcNow;
            }

            public ulong ChannelId { get; }

            public string Title { get; }

            public string Content { get; }

            public int Color { get; }

            public bool IsEmbed { get; }

            public DateTime CreatedAtUtc { get; }

            public static OutboundMessage Embed(ulong channelId, string title, string description, int color) =>
                new OutboundMessage(channelId, title, description, color, true);

            public static OutboundMessage Text(ulong channelId, string content) =>
                new OutboundMessage(channelId, null, content, 0, false);
        }

        private void NotifyRateLimit(ulong channelId, double retrySeconds)
        {
            DateTime now = DateTime.UtcNow;
            bool shouldNotify;
            lock (rateLimitNotificationLock)
            {
                shouldNotify = !rateLimitNotificationCooldowns.TryGetValue(channelId, out DateTime cooldown) || cooldown <= now;
                if (shouldNotify)
                    rateLimitNotificationCooldowns[channelId] = now.AddSeconds(Math.Max(30d, retrySeconds));
            }

            if (!shouldNotify)
                return;

            int waitSeconds = Math.Max(1, (int)Math.Ceiling(retrySeconds));
            Log.Warn($"[Discord] Rate limit for channel {channelId}: all REST requests paused for {waitSeconds}s.");
        }

        private static ulong ParseSnowflake(Dictionary<string, object> source, string key)
        {
            if (source != null && source.TryGetValue(key, out object value) && ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out ulong id))
                return id;

            return 0;
        }

        private static ulong ParseCommandOptionSnowflake(Dictionary<string, object> commandData, string optionName)
        {
            string value = ParseCommandOptionString(commandData, optionName);
            return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0;
        }

        private static string ParseCommandOptionString(Dictionary<string, object> commandData, string optionName)
        {
            if (commandData is null || !commandData.TryGetValue("options", out object optionsValue))
                return null;

            object[] options = Json.Array(optionsValue);
            if (options is null)
                return null;

            foreach (object item in options)
            {
                Dictionary<string, object> option = Json.Object(item);
                if (option != null && option.TryGetValue("name", out object nameValue) &&
                    string.Equals(nameValue as string, optionName, StringComparison.Ordinal) &&
                    option.TryGetValue("value", out object value))
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }

            return null;
        }

        private static ulong[] ParseRoleIds(Dictionary<string, object> member)
        {
            if (member is null || !member.TryGetValue("roles", out object rolesValue))
                return Array.Empty<ulong>();

            object[] roles = Json.Array(rolesValue);
            if (roles is null || roles.Length == 0)
                return Array.Empty<ulong>();

            List<ulong> result = new List<ulong>(roles.Length);
            foreach (object role in roles)
            {
                if (ulong.TryParse(Convert.ToString(role, CultureInfo.InvariantCulture), out ulong roleId))
                    result.Add(roleId);
            }

            return result.ToArray();
        }

        private static string Limit(string value, int maxLength)
        {
            value = string.IsNullOrWhiteSpace(value) ? "—" : value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 1) + "…";
        }

    }
}
