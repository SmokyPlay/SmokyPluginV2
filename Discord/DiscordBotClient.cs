namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
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

        private readonly DiscordSettings settings;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim gatewaySendLock = new SemaphoreSlim(1, 1);
        private readonly object rateLimitNotificationLock = new object();
        private readonly Dictionary<ulong, DateTime> rateLimitNotificationCooldowns = new Dictionary<ulong, DateTime>();
        private readonly HttpClient httpClient;

        private ClientWebSocket socket;
        private ulong applicationId;
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

            httpClient = new HttpClient
            {
                BaseAddress = new Uri(ApiBaseUrl),
                // Each request has its own timeout below. This lets transient
                // REST failures be retried without cancelling the bot itself.
                Timeout = Timeout.InfiniteTimeSpan,
            };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", settings.Token);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SmokyPluginV2/0.7.0");
        }

        public event Action<DiscordMessage> PrefixedMessageReceived;

        public event Func<DiscordInteraction, DiscordInteractionResponse> InteractionReceived;

        public bool IsReady { get; private set; }

        public void Start()
        {
            Task.Run(() => RunGatewayLoopAsync(cancellation.Token));
        }

        public void QueueEmbed(ulong channelId, string title, string description, int color)
        {
            if (channelId == 0 || disposed)
                return;

            string safeTitle = Limit(title, 256);
            string safeDescription = Limit(description, 4000);
            RunDetached(channelId, () => SendEmbedInternalAsync(channelId, safeTitle, safeDescription, color, cancellation.Token));
        }

        public void QueueText(ulong channelId, string content)
        {
            if (channelId == 0 || disposed || string.IsNullOrWhiteSpace(content))
                return;

            string safeContent = Limit(content, 1990);
            RunDetached(channelId, () => SendTextInternalAsync(channelId, safeContent, cancellation.Token));
        }

        public void QueuePriorityText(ulong channelId, string content)
        {
            if (channelId == 0 || disposed || string.IsNullOrWhiteSpace(content))
                return;

            string safeContent = Limit(content, 1990);
            RunDetached(channelId, () => SendTextInternalAsync(channelId, safeContent, cancellation.Token));
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

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            cancellation.Cancel();
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

                    await socket.ConnectAsync(new Uri(GatewayUrl), token).ConfigureAwait(false);
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

                object[] commands = settings.AccountLinking?.IsEnabled == true
                    ? new object[]
                    {
                    new Dictionary<string, object>
                    {
                        ["name"] = "link",
                        ["description"] = "Получить код привязки игрового аккаунта SCP:SL",
                        ["type"] = 1,
                    },
                    new Dictionary<string, object>
                    {
                        ["name"] = "unlink",
                        ["description"] = "Отвязать игровой аккаунт SCP:SL от Discord",
                        ["type"] = 1,
                    },
                    new Dictionary<string, object>
                    {
                        ["name"] = "link-status",
                        ["description"] = "Проверить состояние привязки игрового аккаунта",
                        ["type"] = 1,
                    },
                    }
                    : new object[0];

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
                                        ? "[Discord] Guild slash commands /link, /unlink and /link-status are registered."
                                        : "[Discord] Account-linking slash commands are disabled and removed from the guild.");
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
                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    ["type"] = 4,
                    ["data"] = new Dictionary<string, object>
                    {
                        ["content"] = Limit(response?.Content, 1900),
                        ["flags"] = response?.Ephemeral == false ? 0 : 64,
                        ["allowed_mentions"] = new Dictionary<string, object>
                        {
                            ["parse"] = new object[0],
                        },
                    },
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
            int intents = 1 | 512;
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

        private void RunDetached(ulong channelId, Func<Task> work)
        {
            Task.Run(async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    Log.Error($"[Discord] Failed to send a message to channel {channelId}: {exception}");
                }
            });
        }

        private async Task SendEmbedInternalAsync(ulong channelId, string title, string description, int color, CancellationToken token)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["embeds"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["title"] = title,
                        ["description"] = description,
                        ["color"] = color,
                        ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        ["footer"] = new Dictionary<string, object>
                        {
                            ["text"] = "SmokyPluginV2",
                        },
                    },
                },
                ["allowed_mentions"] = new Dictionary<string, object>
                {
                    ["parse"] = new object[0],
                },
            };

            await SendMessagePayloadAsync(channelId, payload, token).ConfigureAwait(false);
        }

        private Task SendTextInternalAsync(ulong channelId, string content, CancellationToken token) => SendMessagePayloadAsync(
            channelId,
            new Dictionary<string, object>
            {
                ["content"] = content,
                ["allowed_mentions"] = new Dictionary<string, object>
                {
                    ["parse"] = new object[0],
                },
            },
            token);

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
                                return;

                            string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if ((int)response.StatusCode == 429)
                            {
                                Dictionary<string, object> rateLimit = Json.DeserializeObject(responseText);
                                double retrySeconds = rateLimit != null && rateLimit.TryGetValue("retry_after", out object retry)
                                    ? Convert.ToDouble(retry, CultureInfo.InvariantCulture)
                                    : 1d;

                                NotifyRateLimit(channelId, retrySeconds);
                                AddRateLimitNotice(payload, retrySeconds);
                                json = Json.Serialize(payload);
                                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1d, retrySeconds)), token).ConfigureAwait(false);
                                continue;
                            }

                            if ((int)response.StatusCode == 408 || (int)response.StatusCode >= 500)
                            {
                                if (++transientFailures >= 3)
                                    throw new HttpRequestException($"Discord returned {(int)response.StatusCode} after {transientFailures} attempts: {responseText}");

                                await Task.Delay(TimeSpan.FromMilliseconds(500 * transientFailures), token).ConfigureAwait(false);
                                continue;
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

        private async Task<HttpResponseMessage> SendWithHardTimeoutAsync(
            HttpRequestMessage request,
            CancellationToken token,
            int timeoutSeconds = 10)
        {
            using (CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task<HttpResponseMessage> sendTask = httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token);
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), token);
                Task completed = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);

                if (completed == sendTask)
                    return await sendTask.ConfigureAwait(false);

                requestCancellation.Cancel();
                ObserveAbandonedRequest(sendTask);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"Discord HTTP request exceeded the hard {timeoutSeconds}-second timeout.");
            }
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
            string notice = $"⚠️ Discord ограничил отправку сообщений в канал <#{channelId}>. Ожидание: {waitSeconds} сек.";
            Log.Warn($"[Discord] Rate limit for channel {channelId}: retry after {waitSeconds}s.");

            if (settings.RemoteAdminChannelId != 0 && settings.RemoteAdminChannelId != channelId)
                QueuePriorityText(settings.RemoteAdminChannelId, notice);
        }

        private static void AddRateLimitNotice(Dictionary<string, object> payload, double retrySeconds)
        {
            if (!payload.TryGetValue("content", out object contentValue) || !(contentValue is string content))
                return;

            const string marker = "⏳ Ответ был задержан Discord rate limit";
            if (content.StartsWith(marker, StringComparison.Ordinal))
                return;

            int waitSeconds = Math.Max(1, (int)Math.Ceiling(retrySeconds));
            payload["content"] = Limit($"{marker} на {waitSeconds} сек.\n{content}", 1990);
        }

        private static ulong ParseSnowflake(Dictionary<string, object> source, string key)
        {
            if (source != null && source.TryGetValue(key, out object value) && ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out ulong id))
                return id;

            return 0;
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
