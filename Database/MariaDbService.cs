namespace SmokyPluginV2.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Globalization;
    using System.Linq;

    using Exiled.API.Features;

    using MySql.Data.MySqlClient;

    using SmokyPluginV2.AccountLinks;
    using SmokyPluginV2.Statistics;
    using SmokyPluginV2.Warnings;

    internal sealed class MariaDbService : IDisposable
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
            "system_reboots_started", "tesla_kills_as_079", "pink_candies_eaten",
        };

        private static readonly HashSet<string> ServerColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "rounds_completed", "total_round_seconds", "longest_round_seconds", "scp_wins", "foundation_wins", "chaos_wins", "draws",
            "warhead_detonations", "automatic_warhead_detonations", "player_warhead_detonations", "mtf_main_waves", "chaos_main_waves",
            "mtf_reinforcement_waves", "chaos_reinforcement_waves",
        };

        private readonly string connectionString;
        private readonly SharedDatabaseSettings settings;
        private readonly string serverName;

        public MariaDbService(SharedDatabaseSettings settings, string serverName)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.serverName = serverName;
            if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Name) || string.IsNullOrWhiteSpace(settings.Username))
                throw new InvalidOperationException("host, name and username in the shared database.yml must not be empty.");
            MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
            {
                Server = settings.Host.Trim(),
                Port = settings.Port,
                Database = settings.Name.Trim(),
                UserID = settings.Username.Trim(),
                Password = settings.Password ?? string.Empty,
                ConnectionTimeout = Math.Max(1u, settings.ConnectionTimeoutSeconds),
                MaximumPoolSize = Math.Max(2u, settings.MaximumPoolSize),
                Pooling = true,
                CharacterSet = "utf8mb4",
                SslMode = settings.UseTls ? MySqlSslMode.Required : MySqlSslMode.None,
            };
            connectionString = builder.ConnectionString;

            InitializeSchema();
            ServerId = ResolveServer();
            EnsureServerStatisticsRow();
            IsAvailable = true;
            Log.Info($"[Database] MariaDB connected. Game port {Server.Port} has server id {ServerId}.");
        }

        public bool IsAvailable { get; private set; }

        public long ServerId { get; private set; }

        public string ServerName => string.IsNullOrWhiteSpace(serverName) ? "Server " + Server.Port : serverName.Trim();

        public void Dispose()
        {
            IsAvailable = false;
        }

        public bool TryGetDiscordUserId(string playerUserId, out ulong discordUserId, out string error)
        {
            discordUserId = 0;
            try
            {
                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection,
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

        public bool TryGetPlayerUserId(ulong discordUserId, out string playerUserId, out string error)
        {
            playerUserId = null;
            try
            {
                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection,
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
                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection,
                    "UPDATE players p JOIN account_links al ON al.player_id=p.id " +
                    "SET p.statistics_private=@is_private WHERE al.discord_user_id=@discord_id"))
                {
                    command.Parameters.AddWithValue("@is_private", isPrivate);
                    command.Parameters.AddWithValue("@discord_id", discordUserId.ToString(CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                }

                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection,
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
                using (MySqlConnection connection = OpenConnection())
                using (MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    long playerId = GetOrCreatePlayerId(connection, transaction, playerUserId, null);
                    using (MySqlCommand command = CreateCommand(connection,
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
            catch (MySqlException exception) when (exception.Number == 1062)
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
                using (MySqlConnection connection = OpenConnection())
                using (MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                using (MySqlCommand lookup = CreateCommand(connection,
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
                    using (MySqlCommand delete = CreateCommand(connection,
                        "DELETE al FROM account_links al JOIN players p ON p.id=al.player_id WHERE p.steam_id=@steam_id", transaction))
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
                using (MySqlConnection connection = OpenConnection())
                using (MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                using (MySqlCommand lookup = CreateCommand(connection,
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
                    using (MySqlCommand delete = CreateCommand(connection,
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

        public bool TryAddWarning(WarningRecord warning, out string error)
        {
            try
            {
                using (MySqlConnection connection = OpenConnection())
                using (MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    long playerId = GetOrCreatePlayerId(connection, transaction, warning.PlayerUserId, warning.PlayerNickname);
                    using (MySqlCommand next = CreateCommand(connection,
                        "SELECT next_id FROM warning_sequences WHERE server_id=@server_id FOR UPDATE", transaction))
                    {
                        next.Parameters.AddWithValue("@server_id", ServerId);
                        warning.Id = Convert.ToInt64(next.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }

                    using (MySqlCommand advance = CreateCommand(connection,
                        "UPDATE warning_sequences SET next_id=next_id+1 WHERE server_id=@server_id", transaction))
                    {
                        advance.Parameters.AddWithValue("@server_id", ServerId);
                        advance.ExecuteNonQuery();
                    }

                    InsertWarning(connection, transaction, warning, playerId);
                    transaction.Commit();
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("warning insertion", exception, out error);
            }
        }

        public bool TryGetWarning(long warningId, out WarningRecord warning, out string error)
        {
            warning = null;
            try
            {
                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection, WarningSelect + " WHERE w.server_id=@server_id AND w.id=@id"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@id", warningId);
                    using (MySqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                            warning = ReadWarning(reader);
                    }
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                return Fail("warning lookup", exception, out error);
            }
        }

        public bool TryGetWarnings(string playerUserId, out IReadOnlyList<WarningRecord> warnings, out string error)
        {
            List<WarningRecord> result = new List<WarningRecord>();
            try
            {
                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection, WarningSelect +
                    " WHERE w.server_id=@server_id AND p.steam_id=@steam_id ORDER BY w.id DESC"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    using (MySqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                            result.Add(ReadWarning(reader));
                    }
                }

                warnings = result;
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                warnings = Array.Empty<WarningRecord>();
                return Fail("warnings lookup", exception, out error);
            }
        }

        public bool TryDeleteWarning(long warningId, out WarningRecord warning, out string error)
        {
            warning = null;
            try
            {
                if (!TryGetWarning(warningId, out warning, out error))
                    return false;
                if (warning == null)
                {
                    error = $"Предупреждение #{warningId} не найдено.";
                    return false;
                }

                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection, "DELETE FROM warnings WHERE server_id=@server_id AND id=@id"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@id", warningId);
                    if (command.ExecuteNonQuery() != 1)
                    {
                        warning = null;
                        error = $"Предупреждение #{warningId} уже удалено.";
                        return false;
                    }
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                warning = null;
                return Fail("warning deletion", exception, out error);
            }
        }

        public void UpdatePlayerStatistics(string playerUserId, string nickname, PlayerStatDelta delta, DateTime seenAtUtc)
        {
            if (string.IsNullOrWhiteSpace(playerUserId))
                return;

            delta = delta ?? new PlayerStatDelta();
            ValidateColumns(delta.Add.Keys.Concat(delta.Maximum.Keys).Concat(delta.MinimumNullable.Keys), PlayerColumns);

            using (MySqlConnection connection = OpenConnection())
            using (MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                long playerId = GetOrCreatePlayerId(connection, transaction, playerUserId, nickname);
                List<string> columns = new List<string> { "server_id", "player_id", "last_seen" };
                List<string> values = new List<string> { "@server_id", "@player_id", "@last_seen" };
                List<string> updates = new List<string> { "last_seen=VALUES(last_seen)" };

                foreach (KeyValuePair<string, long> pair in delta.Add)
                {
                    columns.Add(pair.Key);
                    values.Add("@" + pair.Key);
                    updates.Add(pair.Key + "=" + pair.Key + "+VALUES(" + pair.Key + ")");
                }

                foreach (KeyValuePair<string, long> pair in delta.Maximum)
                {
                    columns.Add(pair.Key);
                    values.Add("@" + pair.Key);
                    updates.Add(pair.Key + "=GREATEST(" + pair.Key + ",VALUES(" + pair.Key + "))");
                }

                foreach (KeyValuePair<string, long> pair in delta.MinimumNullable)
                {
                    columns.Add(pair.Key);
                    values.Add("@" + pair.Key);
                    updates.Add(pair.Key + "=IF(" + pair.Key + " IS NULL,VALUES(" + pair.Key + "),LEAST(" + pair.Key + ",VALUES(" + pair.Key + ")))");
                }

                string sql = "INSERT INTO player_statistics(" + string.Join(",", columns) + ") VALUES(" + string.Join(",", values) +
                    ") ON DUPLICATE KEY UPDATE " + string.Join(",", updates);
                using (MySqlCommand command = CreateCommand(connection, sql, transaction))
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
                updates.Add(pair.Key + "=" + pair.Key + "+VALUES(" + pair.Key + ")");
            }
            foreach (KeyValuePair<string, long> pair in delta.Maximum)
            {
                columns.Add(pair.Key);
                values.Add("@" + pair.Key);
                updates.Add(pair.Key + "=GREATEST(" + pair.Key + ",VALUES(" + pair.Key + "))");
            }
            if (updates.Count == 0)
                return;

            using (MySqlConnection connection = OpenConnection())
            using (MySqlCommand command = CreateCommand(connection,
                "INSERT INTO server_statistics(" + string.Join(",", columns) + ") VALUES(" + string.Join(",", values) +
                ") ON DUPLICATE KEY UPDATE " + string.Join(",", updates)))
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
                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection,
                    "SELECT p.steam_id,p.last_nickname,p.statistics_private,ps.* FROM players p LEFT JOIN player_statistics ps ON ps.player_id=p.id AND ps.server_id=@server_id WHERE p.steam_id=@steam_id LIMIT 1"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                    using (MySqlDataReader reader = command.ExecuteReader())
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
                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection,
                    "DELETE ps FROM player_statistics ps JOIN players p ON p.id=ps.player_id " +
                    "WHERE ps.server_id=@server_id AND p.steam_id=@steam_id"))
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
                using (MySqlConnection connection = OpenConnection())
                using (MySqlCommand command = CreateCommand(connection,
                    "SELECT s.display_name,ss.* FROM servers s JOIN server_statistics ss ON ss.server_id=s.id WHERE s.id=@server_id"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    using (MySqlDataReader reader = command.ExecuteReader())
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
            using (MySqlConnection connection = OpenConnection())
            using (MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                if (LegacyImportExists(connection, transaction, importKey))
                    return;

                int imported = 0;
                foreach (AccountLinkRecord link in links ?? Enumerable.Empty<AccountLinkRecord>())
                {
                    if (string.IsNullOrWhiteSpace(link.PlayerUserId) || link.DiscordUserId == 0)
                        continue;
                    long playerId = GetOrCreatePlayerId(connection, transaction, link.PlayerUserId, null);
                    using (MySqlCommand command = CreateCommand(connection,
                        "INSERT IGNORE INTO account_links(player_id,discord_user_id,linked_at) VALUES(@player_id,@discord_id,@linked_at)", transaction))
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

        public void ImportLegacyWarnings(string importKey, IEnumerable<WarningRecord> warnings)
        {
            using (MySqlConnection connection = OpenConnection())
            using (MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                if (LegacyImportExists(connection, transaction, importKey))
                    return;
                int imported = 0;
                foreach (WarningRecord warning in warnings ?? Enumerable.Empty<WarningRecord>())
                {
                    if (warning == null || warning.Id <= 0 || string.IsNullOrWhiteSpace(warning.PlayerUserId))
                        continue;
                    long playerId = GetOrCreatePlayerId(connection, transaction, warning.PlayerUserId, warning.PlayerNickname);
                    using (MySqlCommand command = CreateCommand(connection,
                        "INSERT IGNORE INTO warnings(server_id,id,player_id,player_nickname,moderator_user_id,moderator_nickname,issued_at,reason) " +
                        "VALUES(@server_id,@id,@player_id,@player_nickname,@moderator_user_id,@moderator_nickname,@issued_at,@reason)", transaction))
                    {
                        AddWarningParameters(command, warning, playerId);
                        imported += command.ExecuteNonQuery();
                    }
                }
                using (MySqlCommand sequence = CreateCommand(connection,
                    "UPDATE warning_sequences SET next_id=GREATEST(next_id,(SELECT COALESCE(MAX(id),0)+1 FROM warnings WHERE server_id=@server_id)) WHERE server_id=@server_id", transaction))
                {
                    sequence.Parameters.AddWithValue("@server_id", ServerId);
                    sequence.ExecuteNonQuery();
                }
                MarkLegacyImport(connection, transaction, importKey);
                transaction.Commit();
                Log.Info($"[Database] Imported {imported} legacy warning(s) from YAML.");
            }
        }

        public static string NormalizeSteamId(string userId)
        {
            string normalized = (userId ?? string.Empty).Trim();
            int separator = normalized.IndexOf('@');
            if (separator >= 0)
                normalized = normalized.Substring(0, separator);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException("Steam ID is empty.", nameof(userId));
            return normalized;
        }

        public static string ToExiledUserId(string steamId) => NormalizeSteamId(steamId) + "@steam";

        private const string WarningSelect =
            "SELECT w.id,p.steam_id,w.player_nickname,w.moderator_user_id,w.moderator_nickname,w.issued_at,w.reason " +
            "FROM warnings w JOIN players p ON p.id=w.player_id";

        private void InitializeSchema()
        {
            using (MySqlConnection connection = OpenConnection())
            {
                bool locked = false;
                try
                {
                    using (MySqlCommand lockCommand = CreateCommand(connection, "SELECT GET_LOCK('smoky_plugin_v2_schema',15)"))
                        locked = Convert.ToInt32(lockCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
                    if (!locked)
                        throw new TimeoutException("Could not acquire the MariaDB schema migration lock.");

                    foreach (string statement in SchemaStatements)
                    {
                        using (MySqlCommand command = CreateCommand(connection, statement))
                            command.ExecuteNonQuery();
                    }
                    using (MySqlCommand privacyMigration = CreateCommand(connection,
                        "ALTER TABLE players ADD COLUMN IF NOT EXISTS statistics_private BOOLEAN NOT NULL DEFAULT FALSE AFTER last_nickname"))
                        privacyMigration.ExecuteNonQuery();
                    using (MySqlCommand version = CreateCommand(connection,
                        "INSERT IGNORE INTO schema_migrations(version,description) VALUES(1,'Initial MariaDB schema')"))
                        version.ExecuteNonQuery();
                    using (MySqlCommand version = CreateCommand(connection,
                        "INSERT IGNORE INTO schema_migrations(version,description) VALUES(2,'Player statistics privacy')"))
                        version.ExecuteNonQuery();
                }
                finally
                {
                    if (locked)
                    {
                        using (MySqlCommand release = CreateCommand(connection, "SELECT RELEASE_LOCK('smoky_plugin_v2_schema')"))
                            release.ExecuteScalar();
                    }
                }
            }
        }

        private long ResolveServer()
        {
            using (MySqlConnection connection = OpenConnection())
            {
                using (MySqlCommand command = CreateCommand(connection,
                    "INSERT INTO servers(display_name,game_port) VALUES(@display_name,@game_port) " +
                    "ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id),display_name=VALUES(display_name)"))
                {
                    command.Parameters.AddWithValue("@display_name", ServerName);
                    command.Parameters.AddWithValue("@game_port", Server.Port);
                    command.ExecuteNonQuery();
                    long id = command.LastInsertedId;
                    if (id > 0)
                        return id;
                }
                using (MySqlCommand lookup = CreateCommand(connection, "SELECT id FROM servers WHERE game_port=@game_port"))
                {
                    lookup.Parameters.AddWithValue("@game_port", Server.Port);
                    return Convert.ToInt64(lookup.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }
        }

        private MySqlConnection OpenConnection()
        {
            MySqlConnection connection = new MySqlConnection(connectionString);
            try
            {
                connection.Open();
                using (MySqlCommand command = CreateCommand(connection, "SET SESSION time_zone = '+00:00'"))
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
            using (MySqlConnection connection = OpenConnection())
            {
                using (MySqlCommand command = CreateCommand(connection, "INSERT IGNORE INTO server_statistics(server_id) VALUES(@server_id)"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.ExecuteNonQuery();
                }
                using (MySqlCommand command = CreateCommand(connection, "INSERT IGNORE INTO warning_sequences(server_id,next_id) VALUES(@server_id,1)"))
                {
                    command.Parameters.AddWithValue("@server_id", ServerId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static MySqlCommand CreateCommand(MySqlConnection connection, string sql, MySqlTransaction transaction = null)
        {
            MySqlCommand command = new MySqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
            return command;
        }

        private static void ValidateColumns(IEnumerable<string> columns, HashSet<string> allowed)
        {
            string invalid = columns.FirstOrDefault(column => !allowed.Contains(column));
            if (invalid != null)
                throw new InvalidOperationException("Unsupported statistics column: " + invalid);
        }

        private long GetOrCreatePlayerId(MySqlConnection connection, MySqlTransaction transaction, string playerUserId, string nickname)
        {
            using (MySqlCommand command = CreateCommand(connection,
                "INSERT INTO players(steam_id,last_nickname) VALUES(@steam_id,@nickname) " +
                "ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id),last_nickname=COALESCE(NULLIF(VALUES(last_nickname),''),last_nickname)", transaction))
            {
                command.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                command.Parameters.AddWithValue("@nickname", nickname ?? string.Empty);
                command.ExecuteNonQuery();
                if (command.LastInsertedId > 0)
                    return command.LastInsertedId;
            }
            using (MySqlCommand lookup = CreateCommand(connection, "SELECT id FROM players WHERE steam_id=@steam_id", transaction))
            {
                lookup.Parameters.AddWithValue("@steam_id", NormalizeSteamId(playerUserId));
                return Convert.ToInt64(lookup.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private void InsertWarning(MySqlConnection connection, MySqlTransaction transaction, WarningRecord warning, long playerId)
        {
            using (MySqlCommand command = CreateCommand(connection,
                "INSERT INTO warnings(server_id,id,player_id,player_nickname,moderator_user_id,moderator_nickname,issued_at,reason) " +
                "VALUES(@server_id,@id,@player_id,@player_nickname,@moderator_user_id,@moderator_nickname,@issued_at,@reason)", transaction))
            {
                AddWarningParameters(command, warning, playerId);
                command.ExecuteNonQuery();
            }
        }

        private void AddWarningParameters(MySqlCommand command, WarningRecord warning, long playerId)
        {
            command.Parameters.AddWithValue("@server_id", ServerId);
            command.Parameters.AddWithValue("@id", warning.Id);
            command.Parameters.AddWithValue("@player_id", playerId);
            command.Parameters.AddWithValue("@player_nickname", warning.PlayerNickname ?? string.Empty);
            command.Parameters.AddWithValue("@moderator_user_id", warning.ModeratorUserId ?? string.Empty);
            command.Parameters.AddWithValue("@moderator_nickname", warning.ModeratorNickname ?? string.Empty);
            command.Parameters.AddWithValue("@issued_at", warning.IssuedAtUtc);
            command.Parameters.AddWithValue("@reason", warning.Reason ?? string.Empty);
        }

        private static WarningRecord ReadWarning(MySqlDataReader reader) => new WarningRecord
        {
            Id = reader.GetInt64("id"),
            PlayerUserId = ToExiledUserId(reader.GetString("steam_id")),
            PlayerNickname = reader.GetString("player_nickname"),
            ModeratorUserId = reader.GetString("moderator_user_id"),
            ModeratorNickname = reader.GetString("moderator_nickname"),
            IssuedAtUtc = DateTime.SpecifyKind(reader.GetDateTime("issued_at"), DateTimeKind.Utc),
            Reason = reader.GetString("reason"),
        };

        private static PlayerStatisticsRecord ReadPlayerStatistics(MySqlDataReader reader) => new PlayerStatisticsRecord
        {
            SteamId = reader.GetString("steam_id"), Nickname = reader.IsDBNull(reader.GetOrdinal("last_nickname")) ? string.Empty : reader.GetString("last_nickname"),
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
        };

        private static ServerStatisticsRecord ReadServerStatistics(MySqlDataReader reader) => new ServerStatisticsRecord
        {
            ServerName = reader.GetString("display_name"), RoundsCompleted = GetInt64(reader, "rounds_completed"), TotalRoundSeconds = GetInt64(reader, "total_round_seconds"),
            LongestRoundSeconds = GetInt64(reader, "longest_round_seconds"), ScpWins = GetInt64(reader, "scp_wins"), FoundationWins = GetInt64(reader, "foundation_wins"),
            ChaosWins = GetInt64(reader, "chaos_wins"), Draws = GetInt64(reader, "draws"),
            WarheadDetonations = GetInt64(reader, "warhead_detonations"),
            AutomaticWarheadDetonations = GetInt64(reader, "automatic_warhead_detonations"), PlayerWarheadDetonations = GetInt64(reader, "player_warhead_detonations"),
            MtfMainWaves = GetInt64(reader, "mtf_main_waves"), ChaosMainWaves = GetInt64(reader, "chaos_main_waves"),
            MtfReinforcementWaves = GetInt64(reader, "mtf_reinforcement_waves"), ChaosReinforcementWaves = GetInt64(reader, "chaos_reinforcement_waves"),
        };

        private static long GetInt64(MySqlDataReader reader, string name) => Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
        private static long? GetNullableInt64(MySqlDataReader reader, string name) => reader[name] == DBNull.Value ? (long?)null : Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);
        private static DateTime? GetNullableDateTime(MySqlDataReader reader, string name) => reader[name] == DBNull.Value ? (DateTime?)null : DateTime.SpecifyKind(Convert.ToDateTime(reader[name], CultureInfo.InvariantCulture), DateTimeKind.Utc);

        private static bool LegacyImportExists(MySqlConnection connection, MySqlTransaction transaction, string importKey)
        {
            using (MySqlCommand command = CreateCommand(connection, "SELECT 1 FROM legacy_imports WHERE import_key=@key FOR UPDATE", transaction))
            {
                command.Parameters.AddWithValue("@key", importKey);
                return command.ExecuteScalar() != null;
            }
        }

        private static void MarkLegacyImport(MySqlConnection connection, MySqlTransaction transaction, string importKey)
        {
            using (MySqlCommand command = CreateCommand(connection, "INSERT INTO legacy_imports(import_key) VALUES(@key)", transaction))
            {
                command.Parameters.AddWithValue("@key", importKey);
                command.ExecuteNonQuery();
            }
        }

        private static bool Fail(string operation, Exception exception, out string error)
        {
            error = "Ошибка MariaDB. Подробности записаны в консоль сервера.";
            Log.Error($"[Database] Failed {operation}:\n{exception}");
            return false;
        }

        private static readonly string[] SchemaStatements =
        {
            "CREATE TABLE IF NOT EXISTS schema_migrations(version INT UNSIGNED NOT NULL PRIMARY KEY,description VARCHAR(255) NOT NULL,applied_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
            "CREATE TABLE IF NOT EXISTS servers(id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,display_name VARCHAR(128) NOT NULL,game_port SMALLINT UNSIGNED NOT NULL UNIQUE,created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
            "CREATE TABLE IF NOT EXISTS players(id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,steam_id VARCHAR(32) NOT NULL UNIQUE,last_nickname VARCHAR(64) NULL,statistics_private BOOLEAN NOT NULL DEFAULT FALSE,created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
            "CREATE TABLE IF NOT EXISTS account_links(player_id BIGINT UNSIGNED NOT NULL PRIMARY KEY,discord_user_id VARCHAR(20) NOT NULL UNIQUE,linked_at DATETIME(6) NOT NULL,CONSTRAINT fk_account_links_player FOREIGN KEY(player_id) REFERENCES players(id) ON DELETE CASCADE) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
            "CREATE TABLE IF NOT EXISTS warnings(server_id BIGINT UNSIGNED NOT NULL,id BIGINT UNSIGNED NOT NULL,player_id BIGINT UNSIGNED NOT NULL,player_nickname VARCHAR(64) NOT NULL,moderator_user_id VARCHAR(64) NOT NULL,moderator_nickname VARCHAR(64) NOT NULL,issued_at DATETIME(6) NOT NULL,reason TEXT NOT NULL,PRIMARY KEY(server_id,id),KEY ix_warnings_player(server_id,player_id),CONSTRAINT fk_warnings_server FOREIGN KEY(server_id) REFERENCES servers(id) ON DELETE CASCADE,CONSTRAINT fk_warnings_player FOREIGN KEY(player_id) REFERENCES players(id) ON DELETE RESTRICT) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
            "CREATE TABLE IF NOT EXISTS warning_sequences(server_id BIGINT UNSIGNED NOT NULL PRIMARY KEY,next_id BIGINT UNSIGNED NOT NULL DEFAULT 1,CONSTRAINT fk_warning_sequences_server FOREIGN KEY(server_id) REFERENCES servers(id) ON DELETE CASCADE) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
            "CREATE TABLE IF NOT EXISTS player_statistics(server_id BIGINT UNSIGNED NOT NULL,player_id BIGINT UNSIGNED NOT NULL,last_seen DATETIME(6) NOT NULL,rounds_completed BIGINT UNSIGNED NOT NULL DEFAULT 0,human_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0,scp_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0,spectator_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0,best_human_kills_round BIGINT UNSIGNED NOT NULL DEFAULT 0,best_scp_kills_round BIGINT UNSIGNED NOT NULL DEFAULT 0,longest_human_life_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0,longest_scp_life_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0,human_kills_as_human BIGINT UNSIGNED NOT NULL DEFAULT 0,human_kills_as_scp BIGINT UNSIGNED NOT NULL DEFAULT 0,scps_destroyed BIGINT UNSIGNED NOT NULL DEFAULT 0,human_deaths BIGINT UNSIGNED NOT NULL DEFAULT 0,scp_deaths BIGINT UNSIGNED NOT NULL DEFAULT 0,classd_escapes_uncuffed BIGINT UNSIGNED NOT NULL DEFAULT 0,fastest_classd_escape_uncuffed_seconds BIGINT UNSIGNED NULL,classd_escapes_cuffed BIGINT UNSIGNED NOT NULL DEFAULT 0,fastest_classd_escape_cuffed_seconds BIGINT UNSIGNED NULL,scientist_escapes_uncuffed BIGINT UNSIGNED NOT NULL DEFAULT 0,fastest_scientist_escape_uncuffed_seconds BIGINT UNSIGNED NULL,scientist_escapes_cuffed BIGINT UNSIGNED NOT NULL DEFAULT 0,fastest_scientist_escape_cuffed_seconds BIGINT UNSIGNED NULL,classd_escorted BIGINT UNSIGNED NOT NULL DEFAULT 0,scientist_escorted BIGINT UNSIGNED NOT NULL DEFAULT 0,warhead_countdowns_started BIGINT UNSIGNED NOT NULL DEFAULT 0,warhead_detonations BIGINT UNSIGNED NOT NULL DEFAULT 0,warhead_countdowns_stopped BIGINT UNSIGNED NOT NULL DEFAULT 0,pocket_entries BIGINT UNSIGNED NOT NULL DEFAULT 0,pocket_escapes BIGINT UNSIGNED NOT NULL DEFAULT 0,longest_pocket_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0,zombies_created BIGINT UNSIGNED NOT NULL DEFAULT 0,generators_activated BIGINT UNSIGNED NOT NULL DEFAULT 0,system_reboots_started BIGINT UNSIGNED NOT NULL DEFAULT 0,tesla_kills_as_079 BIGINT UNSIGNED NOT NULL DEFAULT 0,pink_candies_eaten BIGINT UNSIGNED NOT NULL DEFAULT 0,PRIMARY KEY(server_id,player_id),KEY ix_player_statistics_last_seen(server_id,last_seen),CONSTRAINT fk_player_statistics_server FOREIGN KEY(server_id) REFERENCES servers(id) ON DELETE CASCADE,CONSTRAINT fk_player_statistics_player FOREIGN KEY(player_id) REFERENCES players(id) ON DELETE CASCADE) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
            "CREATE TABLE IF NOT EXISTS server_statistics(server_id BIGINT UNSIGNED NOT NULL PRIMARY KEY,rounds_completed BIGINT UNSIGNED NOT NULL DEFAULT 0,total_round_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0,longest_round_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0,scp_wins BIGINT UNSIGNED NOT NULL DEFAULT 0,foundation_wins BIGINT UNSIGNED NOT NULL DEFAULT 0,chaos_wins BIGINT UNSIGNED NOT NULL DEFAULT 0,draws BIGINT UNSIGNED NOT NULL DEFAULT 0,warhead_detonations BIGINT UNSIGNED NOT NULL DEFAULT 0,automatic_warhead_detonations BIGINT UNSIGNED NOT NULL DEFAULT 0,player_warhead_detonations BIGINT UNSIGNED NOT NULL DEFAULT 0,mtf_main_waves BIGINT UNSIGNED NOT NULL DEFAULT 0,chaos_main_waves BIGINT UNSIGNED NOT NULL DEFAULT 0,mtf_reinforcement_waves BIGINT UNSIGNED NOT NULL DEFAULT 0,chaos_reinforcement_waves BIGINT UNSIGNED NOT NULL DEFAULT 0,CONSTRAINT fk_server_statistics_server FOREIGN KEY(server_id) REFERENCES servers(id) ON DELETE CASCADE) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
            "CREATE TABLE IF NOT EXISTS legacy_imports(import_key VARCHAR(191) NOT NULL PRIMARY KEY,imported_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci",
        };
    }
}
