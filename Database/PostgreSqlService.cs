namespace SmokyPluginV2.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;

    using Exiled.API.Features;

    using Npgsql;

    using SmokyPluginV2.AccountLinks;
    using SmokyPluginV2.Referrals;
    using SmokyPluginV2.Statistics;
    using SmokyPluginV2.Moderation;

    internal sealed class PostgreSqlService : IDisposable
    {
        private const int CommandTimeoutSeconds = 10;

        private static readonly HashSet<string> PlayerColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "rounds_completed", "human_seconds", "scp_seconds", "spectator_seconds",
            "best_human_kills_round", "best_scp_kills_round", "longest_human_life_seconds", "longest_scp_life_seconds",
            "human_kills_as_human", "human_kills_as_scp", "scps_destroyed", "human_deaths", "scp_deaths",
            "classd_escapes_uncuffed", "fastest_classd_escape_uncuffed_seconds", "classd_escapes_cuffed", "fastest_classd_escape_cuffed_seconds",
            "scientist_escapes_uncuffed", "fastest_scientist_escape_uncuffed_seconds", "scientist_escapes_cuffed", "fastest_scientist_escape_cuffed_seconds",
            "classd_escorted", "scientist_escorted", "warhead_countdowns_started", "warhead_detonations", "warhead_countdowns_stopped",
            "pocket_entries", "pocket_escapes", "longest_pocket_seconds", "zombies_created", "generators_activated",
            "system_reboots_started", "tesla_kills_as_079", "pink_candies_eaten", "best_snake_score",
        };

        private static readonly HashSet<string> ServerColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "rounds_completed", "total_round_seconds", "longest_round_seconds", "scp_wins", "foundation_wins", "chaos_wins", "draws",
            "warhead_detonations", "automatic_warhead_detonations", "player_warhead_detonations", "mtf_main_waves", "chaos_main_waves",
            "mtf_reinforcement_waves", "chaos_reinforcement_waves",
        };

        private readonly string connectionString;
        private readonly SharedDatabaseSettings settings;
        private string serverName;

        public PostgreSqlService(SharedDatabaseSettings settings, string serverName)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.serverName = serverName;
            if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Name) || string.IsNullOrWhiteSpace(settings.Username))
                throw new InvalidOperationException("host, name and username in the shared database.yml must not be empty.");
            NpgsqlConnectionStringBuilder builder = new NpgsqlConnectionStringBuilder
            {
                Host = settings.Host.Trim(),
                Port = settings.Port,
                Database = settings.Name.Trim(),
                Username = settings.Username.Trim(),
                Password = settings.Password ?? string.Empty,
                Timeout = (int)Math.Max(1u, settings.ConnectionTimeoutSeconds),
                MaxPoolSize = (int)Math.Max(2u, settings.MaximumPoolSize),
                Pooling = true,
                SslMode = settings.UseTls ? SslMode.Require : SslMode.Disable,
            };
            connectionString = builder.ConnectionString;

            InitializeSchema();
            ServerId = ResolveServer();
            EnsureServerStatisticsRow();
            IsAvailable = true;
            Log.Info($"[Database] PostgreSQL connected. Game port {Server.Port} has server id {ServerId}.");
        }

        public bool IsAvailable { get; private set; }

        public long ServerId { get; private set; }

        public string ServerName => string.IsNullOrWhiteSpace(serverName) ? "Server " + Server.Port : serverName.Trim();

        public bool TryUpdateServerName(string reloadedServerName, out string error)
        {
            string normalized = string.IsNullOrWhiteSpace(reloadedServerName)
                ? "Server " + Server.Port
                : reloadedServerName.Trim();
            if (normalized.Length > 128)
            {
                error = "Название сервера не может быть длиннее 128 символов.";
                return false;
            }

            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "UPDATE servers SET display_name=@display_name,updated_at=CURRENT_TIMESTAMP " +
                    "WHERE id=@server_id AND game_port=@game_port"))
                {
                    command.Parameters.AddWithValue("@display_name", normalized);
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@game_port", (int)Server.Port);
                    if (command.ExecuteNonQuery() != 1)
                    {
                        error = $"Запись сервера с ID {ServerId} и портом {Server.Port} не найдена.";
                        return false;
                    }
                }

                serverName = reloadedServerName;
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("server name update", exception, out error);
            }
        }

        public void Dispose()
        {
            IsAvailable = false;
        }

        public bool TryGetDiscordUserId(string playerUserId, out ulong discordUserId, out string error)
        {
            discordUserId = 0;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT al.discord_user_id FROM account_links al JOIN players p ON p.id=al.player_id WHERE p.steam_id=@steam_id LIMIT 1"))
                {
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    object result = command.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                        discordUserId = ulong.Parse(Convert.ToString(result, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("account-link lookup", exception, out error);
            }
        }

        public bool TryResolveAccessIdentityBySteamId(
            string playerUserId,
            out long playerId,
            out string resolvedPlayerUserId,
            out ulong discordUserId,
            out string error)
        {
            playerId = 0;
            resolvedPlayerUserId = null;
            discordUserId = 0;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    playerId = GetOrCreatePlayerId(connection, transaction, playerUserId, null);
                    resolvedPlayerUserId = ToExiledUserId(NormalizeSteamId(playerUserId));
                    using (NpgsqlCommand link = CreateCommand(connection,
                        "SELECT discord_user_id FROM account_links WHERE player_id=@player_id LIMIT 1", transaction))
                    {
                        link.Parameters.AddWithValue("@player_id", playerId);
                        object value = link.ExecuteScalar();
                        if (value != null && value != DBNull.Value)
                            discordUserId = ulong.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                    }

                    transaction.Commit();
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("Steam access identity lookup", exception, out error);
            }
        }

        public bool TryResolveAccessIdentityByDiscordId(
            ulong discordUserId,
            out long playerId,
            out string playerUserId,
            out string error)
        {
            playerId = 0;
            playerUserId = null;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT p.id,p.steam_id FROM account_links al " +
                    "JOIN players p ON p.id=al.player_id WHERE al.discord_user_id=@discord_id LIMIT 1"))
                {
                    command.Parameters.AddWithValue("@discord_id", discordUserId.ToString(CultureInfo.InvariantCulture));
                    using (NpgsqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            playerId = GetInt64(reader, "id");
                            playerUserId = ToExiledUserId(GetString(reader, "steam_id"));
                        }
                    }
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("Discord access identity lookup", exception, out error);
            }
        }

        public bool TryGetTotalPlaytimeSeconds(long playerId, out long totalPlaytimeSeconds, out string error)
        {
            totalPlaytimeSeconds = 0;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT COALESCE(human_seconds,0)+COALESCE(scp_seconds,0)+COALESCE(spectator_seconds,0) " +
                    "FROM player_statistics WHERE server_id=@server_id AND player_id=@player_id LIMIT 1"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@player_id", playerId);
                    object value = command.ExecuteScalar();
                    totalPlaytimeSeconds = value == null || value == DBNull.Value
                        ? 0
                        : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("player playtime lookup", exception, out error);
            }
        }

        public bool TryGetReferralAccessState(
            long playerId,
            long qualificationSeconds,
            out ReferralAccessState state,
            out string error)
        {
            state = new ReferralAccessState();
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT " +
                    "(SELECT COUNT(*) FROM referrals r WHERE r.inviter_player_id=@player_id AND " +
                    "(SELECT COALESCE(SUM(COALESCE(ps.human_seconds,0)+COALESCE(ps.scp_seconds,0)+COALESCE(ps.spectator_seconds,0)),0) " +
                    "FROM player_statistics ps WHERE ps.player_id=r.invited_player_id)>=@required_seconds) AS qualified_count," +
                    "EXISTS(SELECT 1 FROM referrals r WHERE r.invited_player_id=@player_id AND " +
                    "(SELECT COALESCE(SUM(COALESCE(ps.human_seconds,0)+COALESCE(ps.scp_seconds,0)+COALESCE(ps.spectator_seconds,0)),0) " +
                    "FROM player_statistics ps WHERE ps.player_id=r.invited_player_id)<@required_seconds) AS is_pending"))
                {
                    command.Parameters.AddWithValue("@player_id", playerId);
                    command.Parameters.AddWithValue("@required_seconds", Math.Max(1, qualificationSeconds));
                    using (NpgsqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            state.QualifiedReferralCount = Convert.ToInt32(
                                reader["qualified_count"],
                                CultureInfo.InvariantCulture);
                            state.IsPendingInvitee = Convert.ToBoolean(
                                reader["is_pending"],
                                CultureInfo.InvariantCulture);
                        }
                    }
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                state = null;
                return Fail("referral access lookup", exception, out error);
            }
        }

        public bool TryIsPendingReferral(
            string playerUserId,
            long qualificationSeconds,
            out bool isPending,
            out string error)
        {
            isPending = false;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT EXISTS(SELECT 1 FROM referrals r " +
                    "JOIN players p ON p.id=r.invited_player_id " +
                    "WHERE p.steam_id=@steam_id AND " +
                    "(SELECT COALESCE(SUM(COALESCE(ps.human_seconds,0)+COALESCE(ps.scp_seconds,0)+COALESCE(ps.spectator_seconds,0)),0) " +
                    "FROM player_statistics ps WHERE ps.player_id=r.invited_player_id)<@required_seconds)"))
                {
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    command.Parameters.AddWithValue("@required_seconds", Math.Max(1, qualificationSeconds));
                    isPending = Convert.ToBoolean(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("pending referral lookup", exception, out error);
            }
        }

        public bool TryAcceptReferral(
            string invitedPlayerUserId,
            string referralCode,
            long unpersistedPlaytimeSeconds,
            long entryMaximumSeconds,
            DateTime acceptedAtUtc,
            out string response)
        {
            string normalizedCode = NormalizeReferralCode(referralCode);
            if (string.IsNullOrWhiteSpace(normalizedCode))
            {
                response = "Укажите реферальный код: .ref КОД";
                return false;
            }

            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    long invitedPlayerId = GetOrCreatePlayerId(
                        connection,
                        transaction,
                        invitedPlayerUserId,
                        null);
                    using (NpgsqlCommand lockInvited = CreateCommand(connection,
                        "SELECT id FROM players WHERE id=@player_id FOR UPDATE",
                        transaction))
                    {
                        lockInvited.Parameters.AddWithValue("@player_id", invitedPlayerId);
                        lockInvited.ExecuteScalar();
                    }

                    using (NpgsqlCommand existing = CreateCommand(connection,
                        "SELECT 1 FROM referrals WHERE invited_player_id=@player_id LIMIT 1",
                        transaction))
                    {
                        existing.Parameters.AddWithValue("@player_id", invitedPlayerId);
                        if (existing.ExecuteScalar() != null)
                        {
                            response = "Вы уже использовали реферальный код.";
                            return false;
                        }
                    }

                    long persistedSeconds = GetTotalNetworkPlaytimeSeconds(
                        connection,
                        transaction,
                        invitedPlayerId);
                    long observedSeconds = SaturatingAdd(
                        persistedSeconds,
                        Math.Max(0, unpersistedPlaytimeSeconds));
                    if (observedSeconds >= Math.Max(0, entryMaximumSeconds))
                    {
                        response = $"Реферальный код можно ввести только в первые {Math.Max(0, entryMaximumSeconds / 60L)} минут игры на сервере.";
                        return false;
                    }

                    long inviterPlayerId;
                    using (NpgsqlCommand inviter = CreateCommand(connection,
                        "SELECT id FROM players WHERE referral_code=@code LIMIT 1 FOR UPDATE",
                        transaction))
                    {
                        inviter.Parameters.AddWithValue("@code", normalizedCode);
                        object inviterValue = inviter.ExecuteScalar();
                        if (inviterValue == null || inviterValue == DBNull.Value)
                        {
                            response = "Такой реферальный код не найден.";
                            return false;
                        }

                        inviterPlayerId = Convert.ToInt64(inviterValue, CultureInfo.InvariantCulture);
                    }

                    if (inviterPlayerId == invitedPlayerId)
                    {
                        response = "Нельзя использовать собственный реферальный код.";
                        return false;
                    }

                    using (NpgsqlCommand insert = CreateCommand(connection,
                        "INSERT INTO referrals(invited_player_id,inviter_player_id,accepted_at) " +
                        "VALUES(@invited_player_id,@inviter_player_id,@accepted_at)",
                        transaction))
                    {
                        insert.Parameters.AddWithValue("@invited_player_id", invitedPlayerId);
                        insert.Parameters.AddWithValue("@inviter_player_id", inviterPlayerId);
                        insert.Parameters.AddWithValue("@accepted_at", acceptedAtUtc);
                        insert.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                response = "Реферальный код принят. Бонус действует до подтверждения приглашения.";
                return true;
            }
            catch (Exception exception)
            {
                Fail("referral acceptance", exception, out response);
                return false;
            }
        }

        public bool TryGetOrCreateReferralStatus(
            ulong discordUserId,
            out ReferralStatus status,
            out string error)
        {
            status = null;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    long playerId;
                    string playerUserId;
                    string referralCode;
                    using (NpgsqlCommand player = CreateCommand(connection,
                        "SELECT p.id,p.steam_id,p.referral_code FROM account_links al " +
                        "JOIN players p ON p.id=al.player_id WHERE al.discord_user_id=@discord_id LIMIT 1 FOR UPDATE",
                        transaction))
                    {
                        player.Parameters.AddWithValue(
                            "@discord_id",
                            discordUserId.ToString(CultureInfo.InvariantCulture));
                        using (NpgsqlDataReader reader = player.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                error = "Сначала привяжите игровой аккаунт командой /link.";
                                return false;
                            }

                            playerId = GetInt64(reader, "id");
                            playerUserId = ToExiledUserId(GetString(reader, "steam_id"));
                            referralCode = reader.IsDBNull(reader.GetOrdinal("referral_code"))
                                ? null
                                : GetString(reader, "referral_code");
                        }
                    }

                    if (string.IsNullOrWhiteSpace(referralCode))
                        referralCode = CreateReferralCode(connection, transaction, playerId);

                    List<ReferralParticipant> participants = new List<ReferralParticipant>();
                    using (NpgsqlCommand referrals = CreateCommand(connection,
                        "SELECT p.steam_id,p.last_nickname,r.accepted_at," +
                        "COALESCE(SUM(COALESCE(ps.human_seconds,0)+COALESCE(ps.scp_seconds,0)+COALESCE(ps.spectator_seconds,0)),0) AS total_seconds " +
                        "FROM referrals r JOIN players p ON p.id=r.invited_player_id " +
                        "LEFT JOIN player_statistics ps ON ps.player_id=p.id " +
                        "WHERE r.inviter_player_id=@player_id " +
                        "GROUP BY p.id,p.steam_id,p.last_nickname,r.accepted_at " +
                        "ORDER BY r.accepted_at ASC",
                        transaction))
                    {
                        referrals.Parameters.AddWithValue("@player_id", playerId);
                        using (NpgsqlDataReader reader = referrals.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                participants.Add(new ReferralParticipant
                                {
                                    PlayerUserId = ToExiledUserId(GetString(reader, "steam_id")),
                                    Nickname = reader.IsDBNull(reader.GetOrdinal("last_nickname"))
                                        ? null
                                        : GetString(reader, "last_nickname"),
                                    AcceptedAtUtc = DateTime.SpecifyKind(
                                        GetDateTime(reader, "accepted_at"),
                                        DateTimeKind.Utc),
                                    TotalPlaytimeSeconds = Convert.ToInt64(
                                        reader["total_seconds"],
                                        CultureInfo.InvariantCulture),
                                });
                            }
                        }
                    }

                    transaction.Commit();
                    status = new ReferralStatus
                    {
                        PlayerUserId = playerUserId,
                        ReferralCode = referralCode,
                        Participants = participants,
                    };
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                status = null;
                return Fail("referral status lookup", exception, out error);
            }
        }

        public bool TryGetReferralQualificationTransition(
            string invitedPlayerUserId,
            long addedPlaytimeSeconds,
            long qualificationSeconds,
            int requiredReferrals,
            out ReferralQualificationTransition transition,
            out string error)
        {
            transition = null;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                {
                    long invitedPlayerId;
                    long inviterPlayerId;
                    string inviterSteamId;
                    using (NpgsqlCommand relation = CreateCommand(connection,
                        "SELECT invited.id AS invited_id,inviter.id AS inviter_id,inviter.steam_id AS inviter_steam_id " +
                        "FROM referrals r JOIN players invited ON invited.id=r.invited_player_id " +
                        "JOIN players inviter ON inviter.id=r.inviter_player_id " +
                        "WHERE invited.steam_id=@steam_id LIMIT 1"))
                    {
                        relation.Parameters.AddWithValue(
                            "@steam_id",
                            NormalizeSteamId(invitedPlayerUserId));
                        using (NpgsqlDataReader reader = relation.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                error = null;
                                return true;
                            }

                            invitedPlayerId = GetInt64(reader, "invited_id");
                            inviterPlayerId = GetInt64(reader, "inviter_id");
                            inviterSteamId = ToExiledUserId(GetString(reader, "inviter_steam_id"));
                        }
                    }

                    long totalSeconds = GetTotalNetworkPlaytimeSeconds(
                        connection,
                        null,
                        invitedPlayerId);
                    long threshold = Math.Max(1, qualificationSeconds);
                    bool crossed = totalSeconds >= threshold &&
                        Math.Max(0, totalSeconds - Math.Max(0, addedPlaytimeSeconds)) < threshold;
                    if (!crossed)
                    {
                        error = null;
                        return true;
                    }

                    int qualifiedCount;
                    using (NpgsqlCommand qualified = CreateCommand(connection,
                        "SELECT COUNT(*) FROM referrals r WHERE r.inviter_player_id=@inviter_id AND " +
                        "(SELECT COALESCE(SUM(COALESCE(ps.human_seconds,0)+COALESCE(ps.scp_seconds,0)+COALESCE(ps.spectator_seconds,0)),0) " +
                        "FROM player_statistics ps WHERE ps.player_id=r.invited_player_id)>=@required_seconds"))
                    {
                        qualified.Parameters.AddWithValue("@inviter_id", inviterPlayerId);
                        qualified.Parameters.AddWithValue("@required_seconds", threshold);
                        qualifiedCount = Convert.ToInt32(
                            qualified.ExecuteScalar(),
                            CultureInfo.InvariantCulture);
                    }

                    transition = new ReferralQualificationTransition
                    {
                        InviteeQualified = true,
                        InviterPlayerUserId = inviterSteamId,
                        RewardThresholdReached = qualifiedCount == Math.Max(1, requiredReferrals),
                    };
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                transition = null;
                return Fail("referral qualification check", exception, out error);
            }
        }

        public bool TryGetPlayerUserId(ulong discordUserId, out string playerUserId, out string error)
        {
            playerUserId = null;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT p.steam_id FROM account_links al JOIN players p ON p.id=al.player_id WHERE al.discord_user_id=@discord_id LIMIT 1"))
                {
                    command.Parameters.AddWithValue("@discord_id", discordUserId.ToString(CultureInfo.InvariantCulture));
                    object result = command.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                        playerUserId = ToExiledUserId(Convert.ToString(result, CultureInfo.InvariantCulture));
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("account-link lookup", exception, out error);
            }
        }

        public bool TrySetStatisticsPrivacy(ulong discordUserId, bool isPrivate, out bool accountLinked, out string error)
        {
            accountLinked = false;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "UPDATE players p SET statistics_private=@is_private,updated_at=CURRENT_TIMESTAMP " +
                    "FROM account_links al WHERE al.player_id=p.id AND al.discord_user_id=@discord_id"))
                {
                    command.Parameters.AddWithValue("@is_private", isPrivate);
                    command.Parameters.AddWithValue("@discord_id", discordUserId.ToString(CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                }

                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT 1 FROM account_links WHERE discord_user_id=@discord_id LIMIT 1"))
                {
                    command.Parameters.AddWithValue("@discord_id", discordUserId.ToString(CultureInfo.InvariantCulture));
                    accountLinked = command.ExecuteScalar() != null;
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("statistics privacy update", exception, out error);
            }
        }

        public bool TryLink(string playerUserId, ulong discordUserId, DateTime linkedAtUtc, out string error)
        {
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    long playerId = GetOrCreatePlayerId(connection, transaction, playerUserId, null);
                    using (NpgsqlCommand command = CreateCommand(connection,
                        "INSERT INTO account_links(player_id,discord_user_id,linked_at) VALUES(@player_id,@discord_id,@linked_at)", transaction))
                    {
                        command.Parameters.AddWithValue("@player_id", playerId);
                        command.Parameters.AddWithValue("@discord_id", discordUserId.ToString(CultureInfo.InvariantCulture));
                        command.Parameters.AddWithValue("@linked_at", linkedAtUtc);
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                error = null;
                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                error = "Этот Steam или Discord аккаунт уже связан с другим аккаунтом.";
                return false;
            }
            catch (Exception exception)
            {
                return Fail("account linking", exception, out error);
            }
        }

        public bool TryUnlinkPlayer(string playerUserId, out ulong discordUserId, out string error)
        {
            discordUserId = 0;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                using (NpgsqlCommand lookup = CreateCommand(connection,
                    "SELECT al.discord_user_id FROM account_links al JOIN players p ON p.id=al.player_id WHERE p.steam_id=@steam_id FOR UPDATE", transaction))
                {
                    lookup.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    object result = lookup.ExecuteScalar();
                    if (result == null || result == DBNull.Value)
                    {
                        error = "Игровой аккаунт не привязан к Discord.";
                        return false;
                    }

                    discordUserId = ulong.Parse(Convert.ToString(result, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                    using (NpgsqlCommand delete = CreateCommand(connection,
                        "DELETE FROM account_links al USING players p WHERE p.id=al.player_id AND p.steam_id=@steam_id", transaction))
                    {
                        delete.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                        delete.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("account unlinking", exception, out error);
            }
        }

        public bool TryUnlinkDiscord(ulong discordUserId, out string playerUserId, out string error)
        {
            playerUserId = null;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                using (NpgsqlCommand lookup = CreateCommand(connection,
                    "SELECT p.steam_id FROM account_links al JOIN players p ON p.id=al.player_id WHERE al.discord_user_id=@discord_id FOR UPDATE", transaction))
                {
                    lookup.Parameters.AddWithValue("@discord_id", discordUserId.ToString(CultureInfo.InvariantCulture));
                    object result = lookup.ExecuteScalar();
                    if (result == null || result == DBNull.Value)
                    {
                        error = "Ваш Discord не привязан к игровому аккаунту.";
                        return false;
                    }

                    playerUserId = ToExiledUserId(Convert.ToString(result, CultureInfo.InvariantCulture));
                    using (NpgsqlCommand delete = CreateCommand(connection,
                        "DELETE FROM account_links WHERE discord_user_id=@discord_id", transaction))
                    {
                        delete.Parameters.AddWithValue("@discord_id", discordUserId.ToString(CultureInfo.InvariantCulture));
                        delete.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("account unlinking", exception, out error);
            }
        }

        public bool TryAddPunishment(PunishmentRecord punishment, out string error)
        {
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    long playerId = GetOrCreatePlayerId(connection, transaction, punishment.PlayerUserId, punishment.PlayerNickname);
                    using (NpgsqlCommand command = CreateCommand(connection,
                        "INSERT INTO punishments(server_id,player_id,moderator_user_id,type,reason,issued_at,expires_at,notified_at) " +
                        "VALUES(@server_id,@player_id,@moderator_user_id,@type,@reason,@issued_at,@expires_at,@notified_at) RETURNING id", transaction))
                    {
                        AddPunishmentParameters(command, punishment, playerId);
                        punishment.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }
                    transaction.Commit();
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("punishment insertion", exception, out error);
            }
        }

        public bool TryGetPunishment(long id, out PunishmentRecord punishment, out string error)
        {
            punishment = null;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection, PunishmentSelect + " WHERE pu.server_id=@server_id AND pu.id=@id"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = command.ExecuteReader())
                        if (reader.Read()) punishment = ReadPunishment(reader);
                }
                error = null;
                return true;
            }
            catch (Exception exception) { return Fail("punishment lookup", exception, out error); }
        }

        public bool TryGetPunishmentHistory(string playerUserId, out PunishmentHistory history, out string error)
        {
            history = null;
            try
            {
                List<PunishmentRecord> records = new List<PunishmentRecord>();
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection, PunishmentSelect +
                    " WHERE pu.server_id=@server_id AND p.steam_id=@steam_id ORDER BY pu.id DESC"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    using (NpgsqlDataReader reader = command.ExecuteReader())
                        while (reader.Read()) records.Add(ReadPunishment(reader));
                }
                PunishmentRecord first = records.FirstOrDefault();
                bool playerExists = first != null;
                string nickname = first?.PlayerNickname ?? string.Empty;
                ulong discordUserId = first?.DiscordUserId ?? 0;
                if (first == null)
                {
                    using (NpgsqlConnection connection = OpenConnection())
                    using (NpgsqlCommand profile = CreateCommand(connection,
                        "SELECT p.last_nickname,COALESCE(al.discord_user_id,'') AS discord_user_id FROM players p " +
                        "LEFT JOIN account_links al ON al.player_id=p.id WHERE p.steam_id=@steam_id"))
                    {
                        profile.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                        using (NpgsqlDataReader reader = profile.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                playerExists = true;
                                nickname = reader["last_nickname"] == DBNull.Value ? string.Empty : GetString(reader, "last_nickname");
                                ulong.TryParse(GetString(reader, "discord_user_id"), NumberStyles.None, CultureInfo.InvariantCulture, out discordUserId);
                            }
                        }
                    }
                }
                history = new PunishmentHistory
                {
                    PlayerExists = playerExists,
                    PlayerUserId = ToExiledUserId(NormalizeSteamId(playerUserId)),
                    PlayerNickname = nickname,
                    DiscordUserId = discordUserId,
                    Records = records,
                };
                error = null;
                return true;
            }
            catch (Exception exception) { return Fail("punishment history lookup", exception, out error); }
        }

        public bool TryGetPendingWarningNotifications(
            string playerUserId,
            out IReadOnlyList<PunishmentRecord> records,
            out string error)
        {
            List<PunishmentRecord> result = new List<PunishmentRecord>();
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection, PunishmentSelect +
                    " WHERE pu.server_id=@server_id AND p.steam_id=@steam_id " +
                    "AND pu.type='warning' AND pu.notified_at IS NULL ORDER BY pu.id ASC"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    using (NpgsqlDataReader reader = command.ExecuteReader())
                        while (reader.Read()) result.Add(ReadPunishment(reader));
                }

                records = result;
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                records = Array.Empty<PunishmentRecord>();
                return Fail("pending warning notification lookup", exception, out error);
            }
        }

        public bool TryMarkPunishmentNotified(long id, DateTime notifiedAtUtc, out string error)
        {
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "UPDATE punishments SET notified_at=@notified_at WHERE server_id=@server_id " +
                    "AND id=@id AND type='warning' AND notified_at IS NULL"))
                {
                    command.Parameters.AddWithValue("@notified_at", notifiedAtUtc);
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@id", id);
                    command.ExecuteNonQuery();
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("warning notification update", exception, out error);
            }
        }

        public bool TryDeletePunishment(long id, out PunishmentRecord punishment, out string error)
        {
            punishment = null;
            try
            {
                if (!TryGetPunishment(id, out punishment, out error)) return false;
                if (punishment == null) { error = $"Наказание #{id} не найдено."; return false; }
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection, "DELETE FROM punishments WHERE server_id=@server_id AND id=@id"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@id", id);
                    if (command.ExecuteNonQuery() != 1) { punishment = null; error = $"Наказание #{id} уже удалено."; return false; }
                }
                error = null;
                return true;
            }
            catch (Exception exception) { punishment = null; return Fail("punishment deletion", exception, out error); }
        }

        public bool TryDeleteActiveBans(string playerUserId, DateTime nowUtc, out int deleted, out string error)
        {
            deleted = 0;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "DELETE FROM punishments pu USING players p WHERE pu.player_id=p.id AND pu.server_id=@server_id " +
                    "AND p.steam_id=@steam_id AND pu.type='ban' AND (pu.expires_at IS NULL OR pu.expires_at>@now)"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    command.Parameters.AddWithValue("@now", nowUtc);
                    deleted = command.ExecuteNonQuery();
                }
                error = null;
                return true;
            }
            catch (Exception exception) { return Fail("active ban history deletion", exception, out error); }
        }

        public void UpdatePlayerStatistics(string playerUserId, string nickname, PlayerStatDelta delta, DateTime seenAtUtc)
        {
            if (string.IsNullOrWhiteSpace(playerUserId))
                return;

            delta = delta ?? new PlayerStatDelta();
            ValidateColumns(delta.Add.Keys.Concat(delta.Maximum.Keys).Concat(delta.MinimumNullable.Keys), PlayerColumns);

            using (NpgsqlConnection connection = OpenConnection())
            using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                long playerId = GetOrCreatePlayerId(connection, transaction, playerUserId, nickname);
                List<string> columns = new List<string> { "server_id", "player_id", "last_seen" };
                List<string> values = new List<string> { "@server_id", "@player_id", "@last_seen" };
                List<string> updates = new List<string> { "last_seen=EXCLUDED.last_seen" };

                foreach (KeyValuePair<string, long> pair in delta.Add)
                {
                    columns.Add(pair.Key);
                    values.Add("@" + pair.Key);
                    updates.Add(pair.Key + "=player_statistics." + pair.Key + "+EXCLUDED." + pair.Key);
                }

                foreach (KeyValuePair<string, long> pair in delta.Maximum)
                {
                    columns.Add(pair.Key);
                    values.Add("@" + pair.Key);
                    updates.Add(pair.Key + "=GREATEST(player_statistics." + pair.Key + ",EXCLUDED." + pair.Key + ")");
                }

                foreach (KeyValuePair<string, long> pair in delta.MinimumNullable)
                {
                    columns.Add(pair.Key);
                    values.Add("@" + pair.Key);
                    updates.Add(pair.Key + "=CASE WHEN player_statistics." + pair.Key + " IS NULL THEN EXCLUDED." + pair.Key +
                        " ELSE LEAST(player_statistics." + pair.Key + ",EXCLUDED." + pair.Key + ") END");
                }

                string sql = "INSERT INTO player_statistics(" + string.Join(",", columns) + ") VALUES(" + string.Join(",", values) +
                    ") ON CONFLICT(server_id,player_id) DO UPDATE SET " + string.Join(",", updates);
                using (NpgsqlCommand command = CreateCommand(connection, sql, transaction))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@player_id", playerId);
                    command.Parameters.AddWithValue("@last_seen", seenAtUtc);
                    foreach (KeyValuePair<string, long> pair in delta.Add.Concat(delta.Maximum).Concat(delta.MinimumNullable))
                        command.Parameters.AddWithValue("@" + pair.Key, pair.Value);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        public void UpdateServerStatistics(ServerStatDelta delta)
        {
            delta = delta ?? new ServerStatDelta();
            ValidateColumns(delta.Add.Keys.Concat(delta.Maximum.Keys), ServerColumns);
            List<string> columns = new List<string> { "server_id" };
            List<string> values = new List<string> { "@server_id" };
            List<string> updates = new List<string>();
            foreach (KeyValuePair<string, long> pair in delta.Add)
            {
                columns.Add(pair.Key);
                values.Add("@" + pair.Key);
                updates.Add(pair.Key + "=server_statistics." + pair.Key + "+EXCLUDED." + pair.Key);
            }
            foreach (KeyValuePair<string, long> pair in delta.Maximum)
            {
                columns.Add(pair.Key);
                values.Add("@" + pair.Key);
                updates.Add(pair.Key + "=GREATEST(server_statistics." + pair.Key + ",EXCLUDED." + pair.Key + ")");
            }
            if (updates.Count == 0)
                return;

            using (NpgsqlConnection connection = OpenConnection())
            using (NpgsqlCommand command = CreateCommand(connection,
                "INSERT INTO server_statistics(" + string.Join(",", columns) + ") VALUES(" + string.Join(",", values) +
                ") ON CONFLICT(server_id) DO UPDATE SET " + string.Join(",", updates)))
            {
                command.Parameters.AddWithValue("@server_id", ServerId);
                foreach (KeyValuePair<string, long> pair in delta.Add.Concat(delta.Maximum))
                    command.Parameters.AddWithValue("@" + pair.Key, pair.Value);
                command.ExecuteNonQuery();
            }
        }

        public bool TryGetPlayerStatistics(string playerUserId, out PlayerStatisticsRecord record, out string error)
        {
            record = null;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT p.steam_id,p.last_nickname,p.statistics_private,ps.* FROM players p LEFT JOIN player_statistics ps ON ps.player_id=p.id AND ps.server_id=@server_id WHERE p.steam_id=@steam_id LIMIT 1"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    using (NpgsqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read() && !reader.IsDBNull(reader.GetOrdinal("server_id")))
                            record = ReadPlayerStatistics(reader);
                    }
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("player statistics lookup", exception, out error);
            }
        }

        public bool TryClearPlayerStatistics(string playerUserId, out bool existed, out string error)
        {
            existed = false;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "DELETE FROM player_statistics ps USING players p WHERE p.id=ps.player_id " +
                    "AND ps.server_id=@server_id AND p.steam_id=@steam_id"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    existed = command.ExecuteNonQuery() > 0;
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("player statistics deletion", exception, out error);
            }
        }

        public bool TryGetServerStatistics(out ServerStatisticsRecord record, out string error)
        {
            record = null;
            try
            {
                using (NpgsqlConnection connection = OpenConnection())
                using (NpgsqlCommand command = CreateCommand(connection,
                    "SELECT s.display_name,ss.* FROM servers s JOIN server_statistics ss ON ss.server_id=s.id WHERE s.id=@server_id"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    using (NpgsqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                            record = ReadServerStatistics(reader);
                    }
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("server statistics lookup", exception, out error);
            }
        }

        public void ImportLegacyAccountLinks(string importKey, IEnumerable<AccountLinkRecord> links)
        {
            using (NpgsqlConnection connection = OpenConnection())
            using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                if (LegacyImportExists(connection, transaction, importKey))
                    return;

                int imported = 0;
                foreach (AccountLinkRecord link in links ?? Enumerable.Empty<AccountLinkRecord>())
                {
                    if (string.IsNullOrWhiteSpace(link.PlayerUserId) || link.DiscordUserId == 0)
                        continue;
                    long playerId = GetOrCreatePlayerId(connection, transaction, link.PlayerUserId, null);
                    using (NpgsqlCommand command = CreateCommand(connection,
                        "INSERT INTO account_links(player_id,discord_user_id,linked_at) VALUES(@player_id,@discord_id,@linked_at) ON CONFLICT DO NOTHING", transaction))
                    {
                        command.Parameters.AddWithValue("@player_id", playerId);
                        command.Parameters.AddWithValue("@discord_id", link.DiscordUserId.ToString(CultureInfo.InvariantCulture));
                        command.Parameters.AddWithValue("@linked_at", link.LinkedAtUtc == default ? DateTime.UtcNow : link.LinkedAtUtc);
                        imported += command.ExecuteNonQuery();
                    }
                }
                MarkLegacyImport(connection, transaction, importKey);
                transaction.Commit();
                Log.Info($"[Database] Imported {imported} legacy account link(s) from YAML.");
            }
        }

        public void ImportLegacyPunishments(string importKey, IEnumerable<PunishmentRecord> punishments)
        {
            using (NpgsqlConnection connection = OpenConnection())
            using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                if (LegacyImportExists(connection, transaction, importKey)) return;
                int imported = 0;
                foreach (PunishmentRecord punishment in punishments ?? Enumerable.Empty<PunishmentRecord>())
                {
                    if (punishment == null || string.IsNullOrWhiteSpace(punishment.PlayerUserId)) continue;
                    long playerId = GetOrCreatePlayerId(connection, transaction, punishment.PlayerUserId, punishment.PlayerNickname);
                    using (NpgsqlCommand command = CreateCommand(connection,
                        "INSERT INTO punishments(server_id,player_id,moderator_user_id,type,reason,issued_at,expires_at,notified_at) " +
                        "VALUES(@server_id,@player_id,@moderator_user_id,@type,@reason,@issued_at,@expires_at,@notified_at)", transaction))
                    {
                        AddPunishmentParameters(command, punishment, playerId);
                        imported += command.ExecuteNonQuery();
                    }
                }
                MarkLegacyImport(connection, transaction, importKey);
                transaction.Commit();
                Log.Info($"[Database] Imported {imported} legacy warning(s) into punishment history.");
            }
        }

        public static string NormalizeSteamId(string userId)
        {
            string normalized = (userId ?? string.Empty).Trim();
            if (!IsSteamUserId(normalized))
                throw new ArgumentException("A valid SteamID64 with no provider or the @steam provider is required.", nameof(userId));

            int separator = normalized.IndexOf('@');
            if (separator >= 0)
                normalized = normalized.Substring(0, separator);
            return normalized;
        }

        public static bool IsSteamUserId(string userId)
        {
            string normalized = (userId ?? string.Empty).Trim();
            int separator = normalized.IndexOf('@');
            if (separator >= 0 &&
                !normalized.Substring(separator).Equals("@steam", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (separator >= 0)
                normalized = normalized.Substring(0, separator);

            return normalized.Length == 17 &&
                normalized.All(character => character >= '0' && character <= '9');
        }

        public static bool TryParseDiscordUserId(string userId, out ulong discordUserId)
        {
            discordUserId = 0;
            string normalized = (userId ?? string.Empty).Trim();
            int separator = normalized.IndexOf('@');
            if (separator <= 0 ||
                !normalized.Substring(separator).Equals("@discord", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string id = normalized.Substring(0, separator);
            return id.Length >= 17 && id.Length <= 20 &&
                id.All(character => character >= '0' && character <= '9') &&
                ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out discordUserId) &&
                discordUserId != 0;
        }

        public static string ToDiscordUserId(ulong discordUserId) =>
            discordUserId == 0
                ? throw new ArgumentOutOfRangeException(nameof(discordUserId))
                : discordUserId.ToString(CultureInfo.InvariantCulture) + "@discord";

        public static string ToExiledUserId(string steamId) => NormalizeSteamId(steamId) + "@steam";

        private const string PunishmentSelect =
            "SELECT pu.id,p.steam_id,p.last_nickname,COALESCE(al.discord_user_id,'') AS discord_user_id,pu.moderator_user_id," +
            "COALESCE(ms.last_nickname,md.last_nickname,'') AS moderator_nickname,COALESCE(ms.steam_id,md.steam_id,'') AS moderator_steam_id," +
            "pu.type,pu.issued_at,pu.expires_at,pu.notified_at,pu.reason FROM punishments pu JOIN players p ON p.id=pu.player_id " +
            "LEFT JOIN account_links al ON al.player_id=p.id " +
            "LEFT JOIN players ms ON pu.moderator_user_id LIKE '%@steam' AND ms.steam_id=split_part(pu.moderator_user_id,'@',1) " +
            "LEFT JOIN account_links mal ON pu.moderator_user_id LIKE '%@discord' AND mal.discord_user_id=split_part(pu.moderator_user_id,'@',1) " +
            "LEFT JOIN players md ON md.id=mal.player_id";

        private void InitializeSchema()
        {
            using (NpgsqlConnection connection = OpenConnection())
            {
                bool locked = false;
                try
                {
                    using (NpgsqlCommand lockCommand = CreateCommand(connection, "SELECT pg_advisory_lock(hashtext('smoky_plugin_v2_schema'))"))
                        lockCommand.ExecuteNonQuery();
                    locked = true;

                    CreateCurrentSchema(connection);
                    ApplyLegacySchemaCompatibility(connection);
                    ApplyPendingSchemaMigrations(connection);
                }
                finally
                {
                    if (locked)
                    {
                        using (NpgsqlCommand release = CreateCommand(connection, "SELECT pg_advisory_unlock(hashtext('smoky_plugin_v2_schema'))"))
                            release.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void CreateCurrentSchema(NpgsqlConnection connection)
        {
            foreach (string statement in SchemaStatements)
            {
                using (NpgsqlCommand command = CreateCommand(connection, statement))
                    command.ExecuteNonQuery();
            }
        }

        private static void ApplyLegacySchemaCompatibility(NpgsqlConnection connection)
        {
            using (NpgsqlCommand command = CreateCommand(connection,
                "ALTER TABLE players ADD COLUMN IF NOT EXISTS statistics_private BOOLEAN NOT NULL DEFAULT FALSE"))
                command.ExecuteNonQuery();
            using (NpgsqlCommand command = CreateCommand(connection,
                "ALTER TABLE players ADD COLUMN IF NOT EXISTS referral_code VARCHAR(16) NULL"))
                command.ExecuteNonQuery();
            using (NpgsqlCommand command = CreateCommand(connection,
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_players_referral_code ON players(referral_code)"))
                command.ExecuteNonQuery();
            using (NpgsqlCommand command = CreateCommand(connection, "DROP TABLE IF EXISTS player_privileges"))
                command.ExecuteNonQuery();

            RecordLegacyMigration(connection, 1, "Initial schema");
            RecordLegacyMigration(connection, 2, "Player statistics privacy");
            RecordLegacyMigration(connection, 4, "Computed playtime privileges");
            RecordLegacyMigration(connection, 5, "Referral program");
            RecordLegacyMigration(connection, 6, "PostgreSQL storage");
        }

        private static void RecordLegacyMigration(NpgsqlConnection connection, int version, string description)
        {
            using (NpgsqlCommand command = CreateCommand(connection,
                "INSERT INTO schema_migrations(version,description) VALUES(@version,@description) ON CONFLICT(version) DO NOTHING"))
            {
                command.Parameters.AddWithValue("@version", version);
                command.Parameters.AddWithValue("@description", description);
                command.ExecuteNonQuery();
            }
        }

        private static void ApplyPendingSchemaMigrations(NpgsqlConnection connection)
        {
            // Keep calls ordered by version. Every new schema/data migration starts at version 8.
            ApplySchemaMigration(connection, 7, "Unified punishment history", ApplyUnifiedPunishmentHistoryMigration);
            ApplySchemaMigration(connection, 8, "Warning delivery tracking", ApplyWarningDeliveryTrackingMigration);
            ApplySchemaMigration(connection, 9, "Snake high score", ApplySnakeHighScoreMigration);
            ApplySchemaMigration(connection, 10, "Remove offline ban nickname placeholders", ApplyOfflineBanNicknameCleanupMigration);
        }

        private static void ApplySchemaMigration(
            NpgsqlConnection connection,
            int version,
            string description,
            Action<NpgsqlConnection, NpgsqlTransaction> apply)
        {
            using (NpgsqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                bool alreadyApplied;
                using (NpgsqlCommand check = CreateCommand(connection,
                    "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=@version)", transaction))
                {
                    check.Parameters.AddWithValue("@version", version);
                    alreadyApplied = Convert.ToBoolean(check.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                if (alreadyApplied)
                {
                    transaction.Commit();
                    return;
                }

                apply(connection, transaction);
                using (NpgsqlCommand record = CreateCommand(connection,
                    "INSERT INTO schema_migrations(version,description) VALUES(@version,@description)", transaction))
                {
                    record.Parameters.AddWithValue("@version", version);
                    record.Parameters.AddWithValue("@description", description);
                    record.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        private static void ApplyUnifiedPunishmentHistoryMigration(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            bool legacyWarningsExist;
            using (NpgsqlCommand check = CreateCommand(connection,
                "SELECT to_regclass('public.warnings') IS NOT NULL", transaction))
                legacyWarningsExist = Convert.ToBoolean(check.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (legacyWarningsExist)
            {
                using (NpgsqlCommand copy = CreateCommand(connection,
                    "INSERT INTO punishments(server_id,player_id,moderator_user_id,type,reason,issued_at,expires_at) " +
                    "SELECT server_id,player_id,moderator_user_id,'warning',reason,issued_at,NULL FROM warnings ORDER BY server_id,id", transaction))
                    copy.ExecuteNonQuery();
            }

            using (NpgsqlCommand drop = CreateCommand(connection, "DROP TABLE IF EXISTS warning_sequences", transaction))
                drop.ExecuteNonQuery();
            using (NpgsqlCommand drop = CreateCommand(connection, "DROP TABLE IF EXISTS warnings", transaction))
                drop.ExecuteNonQuery();
        }

        private static void ApplyWarningDeliveryTrackingMigration(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            using (NpgsqlCommand alter = CreateCommand(connection,
                "ALTER TABLE punishments ADD COLUMN IF NOT EXISTS notified_at TIMESTAMP(6) WITHOUT TIME ZONE NULL", transaction))
                alter.ExecuteNonQuery();
            using (NpgsqlCommand backfill = CreateCommand(connection,
                "UPDATE punishments SET notified_at=issued_at WHERE type='warning' AND notified_at IS NULL", transaction))
                backfill.ExecuteNonQuery();
            using (NpgsqlCommand index = CreateCommand(connection,
                "CREATE INDEX IF NOT EXISTS ix_punishments_pending_warnings " +
                "ON punishments(server_id,player_id,id) WHERE type='warning' AND notified_at IS NULL", transaction))
                index.ExecuteNonQuery();
        }

        private static void ApplySnakeHighScoreMigration(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            using (NpgsqlCommand alter = CreateCommand(connection,
                "ALTER TABLE player_statistics ADD COLUMN IF NOT EXISTS best_snake_score BIGINT NOT NULL DEFAULT 0", transaction))
                alter.ExecuteNonQuery();
        }

        private static void ApplyOfflineBanNicknameCleanupMigration(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            using (NpgsqlCommand cleanup = CreateCommand(connection,
                "UPDATE players SET last_nickname=NULL,updated_at=CURRENT_TIMESTAMP " +
                "WHERE LOWER(BTRIM(last_nickname))='unknown - offline ban'", transaction))
                cleanup.ExecuteNonQuery();
        }

        private long ResolveServer()
        {
            using (NpgsqlConnection connection = OpenConnection())
            {
                using (NpgsqlCommand command = CreateCommand(connection,
                    "INSERT INTO servers(display_name,game_port) VALUES(@display_name,@game_port) " +
                    "ON CONFLICT(game_port) DO UPDATE SET display_name=EXCLUDED.display_name,updated_at=CURRENT_TIMESTAMP RETURNING id"))
                {
                    command.Parameters.AddWithValue("@display_name", ServerName);
                    command.Parameters.AddWithValue("@game_port", (int)Server.Port);
                    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }
        }

        private NpgsqlConnection OpenConnection()
        {
            NpgsqlConnection connection = new NpgsqlConnection(connectionString);
            try
            {
                connection.Open();
                using (NpgsqlCommand command = CreateCommand(connection, "SET TIME ZONE 'UTC'"))
                    command.ExecuteNonQuery();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private void EnsureServerStatisticsRow()
        {
            using (NpgsqlConnection connection = OpenConnection())
            {
                using (NpgsqlCommand command = CreateCommand(connection, "INSERT INTO server_statistics(server_id) VALUES(@server_id) ON CONFLICT(server_id) DO NOTHING"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql, NpgsqlTransaction transaction = null)
        {
            NpgsqlCommand command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
            return command;
        }

        private static void ValidateColumns(IEnumerable<string> columns, HashSet<string> allowed)
        {
            string invalid = columns.FirstOrDefault(column => !allowed.Contains(column));
            if (invalid != null)
                throw new InvalidOperationException("Unsupported statistics column: " + invalid);
        }

        private long GetOrCreatePlayerId(NpgsqlConnection connection, NpgsqlTransaction transaction, string playerUserId, string nickname)
        {
            if (!IsSteamUserId(playerUserId))
                throw new ArgumentException("Only real Steam players can be persisted.", nameof(playerUserId));

            using (NpgsqlCommand command = CreateCommand(connection,
                "INSERT INTO players(steam_id,last_nickname) VALUES(@steam_id,@nickname) " +
                "ON CONFLICT(steam_id) DO UPDATE SET last_nickname=COALESCE(NULLIF(EXCLUDED.last_nickname,''),players.last_nickname)," +
                "updated_at=CURRENT_TIMESTAMP RETURNING id", transaction))
            {
                command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                command.Parameters.AddWithValue(
                    "@nickname",
                    NpgsqlTypes.NpgsqlDbType.Varchar,
                    string.IsNullOrWhiteSpace(nickname) ? (object)DBNull.Value : nickname);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static long GetTotalNetworkPlaytimeSeconds(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long playerId)
        {
            using (NpgsqlCommand command = CreateCommand(connection,
                "SELECT COALESCE(SUM(COALESCE(human_seconds,0)+COALESCE(scp_seconds,0)+COALESCE(spectator_seconds,0)),0) " +
                "FROM player_statistics WHERE player_id=@player_id",
                transaction))
            {
                command.Parameters.AddWithValue("@player_id", playerId);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static string CreateReferralCode(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long playerId)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                string code = GenerateReferralCode();
                using (NpgsqlCommand savepoint = CreateCommand(connection, "SAVEPOINT referral_code_attempt", transaction))
                    savepoint.ExecuteNonQuery();
                try
                {
                    using (NpgsqlCommand update = CreateCommand(connection,
                        "UPDATE players SET referral_code=@code WHERE id=@player_id AND referral_code IS NULL",
                        transaction))
                    {
                        update.Parameters.AddWithValue("@code", code);
                        update.Parameters.AddWithValue("@player_id", playerId);
                        if (update.ExecuteNonQuery() == 1)
                        {
                            using (NpgsqlCommand release = CreateCommand(connection, "RELEASE SAVEPOINT referral_code_attempt", transaction))
                                release.ExecuteNonQuery();
                            return code;
                        }
                    }

                    using (NpgsqlCommand existing = CreateCommand(connection,
                        "SELECT referral_code FROM players WHERE id=@player_id",
                        transaction))
                    {
                        existing.Parameters.AddWithValue("@player_id", playerId);
                        object value = existing.ExecuteScalar();
                        if (value != null && value != DBNull.Value)
                        {
                            using (NpgsqlCommand release = CreateCommand(connection, "RELEASE SAVEPOINT referral_code_attempt", transaction))
                                release.ExecuteNonQuery();
                            return Convert.ToString(value, CultureInfo.InvariantCulture);
                        }
                    }

                    using (NpgsqlCommand release = CreateCommand(connection, "RELEASE SAVEPOINT referral_code_attempt", transaction))
                        release.ExecuteNonQuery();
                }
                catch (PostgresException exception) when (exception.SqlState == "23505")
                {
                    using (NpgsqlCommand rollback = CreateCommand(connection, "ROLLBACK TO SAVEPOINT referral_code_attempt", transaction))
                        rollback.ExecuteNonQuery();
                    using (NpgsqlCommand release = CreateCommand(connection, "RELEASE SAVEPOINT referral_code_attempt", transaction))
                        release.ExecuteNonQuery();
                    // Extremely unlikely code collision; the savepoint keeps this transaction usable.
                }
            }

            throw new InvalidOperationException("Could not generate a unique referral code.");
        }

        private static string GenerateReferralCode()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] bytes = new byte[8];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            StringBuilder result = new StringBuilder(9);
            for (int index = 0; index < bytes.Length; index++)
            {
                if (index == 4)
                    result.Append('-');
                result.Append(alphabet[bytes[index] % alphabet.Length]);
            }

            return result.ToString();
        }

        private static string NormalizeReferralCode(string code) =>
            string.IsNullOrWhiteSpace(code)
                ? string.Empty
                : code.Trim().Replace(" ", string.Empty).ToUpperInvariant();

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;

        private void AddPunishmentParameters(NpgsqlCommand command, PunishmentRecord punishment, long playerId)
        {
            command.Parameters.AddWithValue("@server_id", ServerId);
            command.Parameters.AddWithValue("@player_id", playerId);
            command.Parameters.AddWithValue("@moderator_user_id", punishment.ModeratorUserId ?? string.Empty);
            command.Parameters.AddWithValue("@type", PunishmentTypeToDatabase(punishment.Type));
            command.Parameters.AddWithValue("@reason", punishment.Reason ?? string.Empty);
            command.Parameters.AddWithValue("@issued_at", punishment.IssuedAtUtc);
            command.Parameters.AddWithValue("@expires_at", (object)punishment.ExpiresAtUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("@notified_at", (object)punishment.NotifiedAtUtc ?? DBNull.Value);
        }

        private static PunishmentRecord ReadPunishment(NpgsqlDataReader reader)
        {
            string discord = GetString(reader, "discord_user_id");
            ulong.TryParse(discord, NumberStyles.None, CultureInfo.InvariantCulture, out ulong discordUserId);
            return new PunishmentRecord
            {
                Id = GetInt64(reader, "id"),
                PlayerUserId = ToExiledUserId(GetString(reader, "steam_id")),
                PlayerNickname = reader["last_nickname"] == DBNull.Value ? string.Empty : GetString(reader, "last_nickname"),
                DiscordUserId = discordUserId,
                ModeratorUserId = GetString(reader, "moderator_user_id"),
                ModeratorNickname = GetString(reader, "moderator_nickname"),
                ModeratorSteamId = GetString(reader, "moderator_steam_id"),
                Type = PunishmentTypeFromDatabase(GetString(reader, "type")),
                IssuedAtUtc = DateTime.SpecifyKind(GetDateTime(reader, "issued_at"), DateTimeKind.Utc),
                ExpiresAtUtc = GetNullableDateTime(reader, "expires_at"),
                NotifiedAtUtc = GetNullableDateTime(reader, "notified_at"),
                Reason = GetString(reader, "reason"),
            };
        }

        private static string PunishmentTypeToDatabase(PunishmentType type) => type switch
        {
            PunishmentType.Warning => "warning",
            PunishmentType.Kick => "kick",
            PunishmentType.Ban => "ban",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        private static PunishmentType PunishmentTypeFromDatabase(string type) =>
            string.Equals(type, "warning", StringComparison.OrdinalIgnoreCase) ? PunishmentType.Warning :
            string.Equals(type, "kick", StringComparison.OrdinalIgnoreCase) ? PunishmentType.Kick :
            string.Equals(type, "ban", StringComparison.OrdinalIgnoreCase) ? PunishmentType.Ban :
            throw new InvalidOperationException("Unknown punishment type: " + type);

        private static PlayerStatisticsRecord ReadPlayerStatistics(NpgsqlDataReader reader) => new PlayerStatisticsRecord
        {
            SteamId = GetString(reader, "steam_id"), Nickname = reader.IsDBNull(reader.GetOrdinal("last_nickname")) ? string.Empty : GetString(reader, "last_nickname"),
            StatisticsPrivate = Convert.ToBoolean(reader["statistics_private"], CultureInfo.InvariantCulture),
            LastSeenUtc = GetNullableDateTime(reader, "last_seen"), RoundsCompleted = GetInt64(reader, "rounds_completed"),
            HumanSeconds = GetInt64(reader, "human_seconds"), ScpSeconds = GetInt64(reader, "scp_seconds"), SpectatorSeconds = GetInt64(reader, "spectator_seconds"),
            BestHumanKillsRound = GetInt64(reader, "best_human_kills_round"), BestScpKillsRound = GetInt64(reader, "best_scp_kills_round"),
            LongestHumanLifeSeconds = GetInt64(reader, "longest_human_life_seconds"), LongestScpLifeSeconds = GetInt64(reader, "longest_scp_life_seconds"),
            HumanKillsAsHuman = GetInt64(reader, "human_kills_as_human"), HumanKillsAsScp = GetInt64(reader, "human_kills_as_scp"), ScpsDestroyed = GetInt64(reader, "scps_destroyed"),
            HumanDeaths = GetInt64(reader, "human_deaths"), ScpDeaths = GetInt64(reader, "scp_deaths"),
            ClassDEscapesUncuffed = GetInt64(reader, "classd_escapes_uncuffed"), FastestClassDEscapeUncuffedSeconds = GetNullableInt64(reader, "fastest_classd_escape_uncuffed_seconds"),
            ClassDEscapesCuffed = GetInt64(reader, "classd_escapes_cuffed"), FastestClassDEscapeCuffedSeconds = GetNullableInt64(reader, "fastest_classd_escape_cuffed_seconds"),
            ScientistEscapesUncuffed = GetInt64(reader, "scientist_escapes_uncuffed"), FastestScientistEscapeUncuffedSeconds = GetNullableInt64(reader, "fastest_scientist_escape_uncuffed_seconds"),
            ScientistEscapesCuffed = GetInt64(reader, "scientist_escapes_cuffed"), FastestScientistEscapeCuffedSeconds = GetNullableInt64(reader, "fastest_scientist_escape_cuffed_seconds"),
            ClassDEscorted = GetInt64(reader, "classd_escorted"), ScientistEscorted = GetInt64(reader, "scientist_escorted"),
            WarheadCountdownsStarted = GetInt64(reader, "warhead_countdowns_started"), WarheadDetonations = GetInt64(reader, "warhead_detonations"), WarheadCountdownsStopped = GetInt64(reader, "warhead_countdowns_stopped"),
            PocketEntries = GetInt64(reader, "pocket_entries"), PocketEscapes = GetInt64(reader, "pocket_escapes"), LongestPocketSeconds = GetInt64(reader, "longest_pocket_seconds"),
            ZombiesCreated = GetInt64(reader, "zombies_created"), GeneratorsActivated = GetInt64(reader, "generators_activated"), SystemRebootsStarted = GetInt64(reader, "system_reboots_started"),
            TeslaKillsAs079 = GetInt64(reader, "tesla_kills_as_079"), PinkCandiesEaten = GetInt64(reader, "pink_candies_eaten"),
            BestSnakeScore = GetInt64(reader, "best_snake_score"),
        };

        private static ServerStatisticsRecord ReadServerStatistics(NpgsqlDataReader reader) => new ServerStatisticsRecord
        {
            ServerName = GetString(reader, "display_name"), RoundsCompleted = GetInt64(reader, "rounds_completed"), TotalRoundSeconds = GetInt64(reader, "total_round_seconds"),
            LongestRoundSeconds = GetInt64(reader, "longest_round_seconds"), ScpWins = GetInt64(reader, "scp_wins"), FoundationWins = GetInt64(reader, "foundation_wins"),
            ChaosWins = GetInt64(reader, "chaos_wins"), Draws = GetInt64(reader, "draws"),
            WarheadDetonations = GetInt64(reader, "warhead_detonations"),
            AutomaticWarheadDetonations = GetInt64(reader, "automatic_warhead_detonations"), PlayerWarheadDetonations = GetInt64(reader, "player_warhead_detonations"),
            MtfMainWaves = GetInt64(reader, "mtf_main_waves"), ChaosMainWaves = GetInt64(reader, "chaos_main_waves"),
            MtfReinforcementWaves = GetInt64(reader, "mtf_reinforcement_waves"), ChaosReinforcementWaves = GetInt64(reader, "chaos_reinforcement_waves"),
        };

        private static long GetInt64(NpgsqlDataReader reader, string name) => Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
        private static string GetString(NpgsqlDataReader reader, string name) => Convert.ToString(reader[name], CultureInfo.InvariantCulture);
        private static DateTime GetDateTime(NpgsqlDataReader reader, string name) => Convert.ToDateTime(reader[name], CultureInfo.InvariantCulture);
        private static long? GetNullableInt64(NpgsqlDataReader reader, string name) => reader[name] == DBNull.Value ? (long?)null : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
        private static DateTime? GetNullableDateTime(NpgsqlDataReader reader, string name) => reader[name] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind(Convert.ToDateTime(reader[name], CultureInfo.InvariantCulture), DateTimeKind.Utc);

        private static bool LegacyImportExists(NpgsqlConnection connection, NpgsqlTransaction transaction, string importKey)
        {
            using (NpgsqlCommand command = CreateCommand(connection, "SELECT 1 FROM legacy_imports WHERE import_key=@key FOR UPDATE", transaction))
            {
                command.Parameters.AddWithValue("@key", importKey);
                return command.ExecuteScalar() != null;
            }
        }

        private static void MarkLegacyImport(NpgsqlConnection connection, NpgsqlTransaction transaction, string importKey)
        {
            using (NpgsqlCommand command = CreateCommand(connection, "INSERT INTO legacy_imports(import_key) VALUES(@key)", transaction))
            {
                command.Parameters.AddWithValue("@key", importKey);
                command.ExecuteNonQuery();
            }
        }

        private static bool Fail(string operation, Exception exception, out string error)
        {
            error = "Ошибка PostgreSQL. Подробности записаны в консоль сервера.";
            Log.Error($"[Database] Failed {operation}:\n{exception}");
            return false;
        }

        private static readonly string[] SchemaStatements =
        {
            "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER NOT NULL PRIMARY KEY,description VARCHAR(255) NOT NULL,applied_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP(6))",
            "CREATE TABLE IF NOT EXISTS servers(id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,display_name VARCHAR(128) NOT NULL,game_port INTEGER NOT NULL UNIQUE CHECK(game_port BETWEEN 1 AND 65535),created_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP(6),updated_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP(6))",
            "CREATE TABLE IF NOT EXISTS players(id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,steam_id VARCHAR(32) NOT NULL UNIQUE,last_nickname VARCHAR(64) NULL,statistics_private BOOLEAN NOT NULL DEFAULT FALSE,referral_code VARCHAR(16) NULL,created_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP(6),updated_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP(6))",
            "CREATE TABLE IF NOT EXISTS account_links(player_id BIGINT NOT NULL PRIMARY KEY,discord_user_id VARCHAR(20) NOT NULL UNIQUE,linked_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,CONSTRAINT fk_account_links_player FOREIGN KEY(player_id) REFERENCES players(id) ON DELETE CASCADE)",
            "CREATE TABLE IF NOT EXISTS referrals(invited_player_id BIGINT NOT NULL,inviter_player_id BIGINT NOT NULL,accepted_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,PRIMARY KEY(invited_player_id),CONSTRAINT fk_referrals_invited FOREIGN KEY(invited_player_id) REFERENCES players(id) ON DELETE CASCADE,CONSTRAINT fk_referrals_inviter FOREIGN KEY(inviter_player_id) REFERENCES players(id) ON DELETE CASCADE)",
            "CREATE TABLE IF NOT EXISTS punishments(id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,server_id BIGINT NOT NULL,player_id BIGINT NOT NULL,moderator_user_id VARCHAR(64) NOT NULL,type VARCHAR(16) NOT NULL CHECK(type IN ('warning','kick','ban')),reason TEXT NOT NULL,issued_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,expires_at TIMESTAMP(6) WITHOUT TIME ZONE NULL,notified_at TIMESTAMP(6) WITHOUT TIME ZONE NULL,CONSTRAINT fk_punishments_server FOREIGN KEY(server_id) REFERENCES servers(id) ON DELETE CASCADE,CONSTRAINT fk_punishments_player FOREIGN KEY(player_id) REFERENCES players(id) ON DELETE RESTRICT)",
            "CREATE TABLE IF NOT EXISTS player_statistics(server_id BIGINT NOT NULL,player_id BIGINT NOT NULL,last_seen TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,rounds_completed BIGINT NOT NULL DEFAULT 0,human_seconds BIGINT NOT NULL DEFAULT 0,scp_seconds BIGINT NOT NULL DEFAULT 0,spectator_seconds BIGINT NOT NULL DEFAULT 0,best_human_kills_round BIGINT NOT NULL DEFAULT 0,best_scp_kills_round BIGINT NOT NULL DEFAULT 0,longest_human_life_seconds BIGINT NOT NULL DEFAULT 0,longest_scp_life_seconds BIGINT NOT NULL DEFAULT 0,human_kills_as_human BIGINT NOT NULL DEFAULT 0,human_kills_as_scp BIGINT NOT NULL DEFAULT 0,scps_destroyed BIGINT NOT NULL DEFAULT 0,human_deaths BIGINT NOT NULL DEFAULT 0,scp_deaths BIGINT NOT NULL DEFAULT 0,classd_escapes_uncuffed BIGINT NOT NULL DEFAULT 0,fastest_classd_escape_uncuffed_seconds BIGINT NULL,classd_escapes_cuffed BIGINT NOT NULL DEFAULT 0,fastest_classd_escape_cuffed_seconds BIGINT NULL,scientist_escapes_uncuffed BIGINT NOT NULL DEFAULT 0,fastest_scientist_escape_uncuffed_seconds BIGINT NULL,scientist_escapes_cuffed BIGINT NOT NULL DEFAULT 0,fastest_scientist_escape_cuffed_seconds BIGINT NULL,classd_escorted BIGINT NOT NULL DEFAULT 0,scientist_escorted BIGINT NOT NULL DEFAULT 0,warhead_countdowns_started BIGINT NOT NULL DEFAULT 0,warhead_detonations BIGINT NOT NULL DEFAULT 0,warhead_countdowns_stopped BIGINT NOT NULL DEFAULT 0,pocket_entries BIGINT NOT NULL DEFAULT 0,pocket_escapes BIGINT NOT NULL DEFAULT 0,longest_pocket_seconds BIGINT NOT NULL DEFAULT 0,zombies_created BIGINT NOT NULL DEFAULT 0,generators_activated BIGINT NOT NULL DEFAULT 0,system_reboots_started BIGINT NOT NULL DEFAULT 0,tesla_kills_as_079 BIGINT NOT NULL DEFAULT 0,pink_candies_eaten BIGINT NOT NULL DEFAULT 0,best_snake_score BIGINT NOT NULL DEFAULT 0,PRIMARY KEY(server_id,player_id),CONSTRAINT fk_player_statistics_server FOREIGN KEY(server_id) REFERENCES servers(id) ON DELETE CASCADE,CONSTRAINT fk_player_statistics_player FOREIGN KEY(player_id) REFERENCES players(id) ON DELETE CASCADE)",
            "CREATE TABLE IF NOT EXISTS server_statistics(server_id BIGINT NOT NULL PRIMARY KEY,rounds_completed BIGINT NOT NULL DEFAULT 0,total_round_seconds BIGINT NOT NULL DEFAULT 0,longest_round_seconds BIGINT NOT NULL DEFAULT 0,scp_wins BIGINT NOT NULL DEFAULT 0,foundation_wins BIGINT NOT NULL DEFAULT 0,chaos_wins BIGINT NOT NULL DEFAULT 0,draws BIGINT NOT NULL DEFAULT 0,warhead_detonations BIGINT NOT NULL DEFAULT 0,automatic_warhead_detonations BIGINT NOT NULL DEFAULT 0,player_warhead_detonations BIGINT NOT NULL DEFAULT 0,mtf_main_waves BIGINT NOT NULL DEFAULT 0,chaos_main_waves BIGINT NOT NULL DEFAULT 0,mtf_reinforcement_waves BIGINT NOT NULL DEFAULT 0,chaos_reinforcement_waves BIGINT NOT NULL DEFAULT 0,CONSTRAINT fk_server_statistics_server FOREIGN KEY(server_id) REFERENCES servers(id) ON DELETE CASCADE)",
            "CREATE TABLE IF NOT EXISTS legacy_imports(import_key VARCHAR(191) NOT NULL PRIMARY KEY,imported_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP(6))",
            "CREATE INDEX IF NOT EXISTS ix_referrals_inviter ON referrals(inviter_player_id)",
            "CREATE INDEX IF NOT EXISTS ix_punishments_player ON punishments(server_id,player_id,id DESC)",
            "CREATE INDEX IF NOT EXISTS ix_punishments_active_bans ON punishments(server_id,player_id,expires_at) WHERE type='ban'",
            "CREATE INDEX IF NOT EXISTS ix_player_statistics_last_seen ON player_statistics(server_id,last_seen)",
        };
    }
}
