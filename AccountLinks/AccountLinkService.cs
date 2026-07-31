namespace SmokyPluginV2.AccountLinks
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;

    using Exiled.API.Features;

    using SmokyPluginV2.Database;

    using YamlDotNet.Serialization;
    using YamlDotNet.Serialization.NamingConventions;

    internal sealed class AccountLinkService : IDisposable
    {
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private readonly object syncRoot = new object();
        private readonly PostgreSqlService database;
        private readonly Dictionary<string, PendingLink> pendingByCode = new Dictionary<string, PendingLink>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, string> pendingCodeByDiscord = new Dictionary<ulong, string>();

        public AccountLinkService(PostgreSqlService database, bool importLegacyYaml)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            DirectoryPath = Path.Combine(Paths.Exiled, "Data", "SmokyPluginV2", Server.Port.ToString());
            FilePath = Path.Combine(DirectoryPath, "account-links.yml");
            if (importLegacyYaml)
                ImportLegacyYaml();
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }

        public bool TryCreateCode(ulong discordUserId, TimeSpan lifetime, out string code, out DateTime expiresAtUtc, out string error)
        {
            lock (syncRoot)
            {
                code = null;
                expiresAtUtc = default;
                CleanupExpiredCodesLocked();
                if (!database.TryGetPlayerUserId(discordUserId, out string existingPlayer, out error))
                    return false;
                if (!string.IsNullOrWhiteSpace(existingPlayer))
                {
                    error = $"Discord уже привязан к игровому аккаунту `{existingPlayer}`. Сначала используйте `/unlink`.";
                    return false;
                }

                if (pendingCodeByDiscord.TryGetValue(discordUserId, out string previousCode))
                    pendingByCode.Remove(previousCode);

                do
                {
                    code = GenerateCode();
                }
                while (pendingByCode.ContainsKey(code));

                expiresAtUtc = DateTime.UtcNow.Add(lifetime);
                pendingByCode[code] = new PendingLink { DiscordUserId = discordUserId, ExpiresAtUtc = expiresAtUtc };
                pendingCodeByDiscord[discordUserId] = code;
                error = null;
                return true;
            }
        }

        public bool TryLink(string code, string playerUserId, out ulong discordUserId, out string error)
        {
            lock (syncRoot)
            {
                discordUserId = 0;
                CleanupExpiredCodesLocked();
                string normalizedCode = NormalizeCode(code);
                if (!pendingByCode.TryGetValue(normalizedCode, out PendingLink pending))
                {
                    error = "Код привязки недействителен или истёк. Получите новый код командой `/link` в Discord.";
                    return false;
                }

                if (!database.TryLink(playerUserId, pending.DiscordUserId, DateTime.UtcNow, out error))
                    return false;

                pendingByCode.Remove(normalizedCode);
                pendingCodeByDiscord.Remove(pending.DiscordUserId);
                discordUserId = pending.DiscordUserId;
                Log.Info($"[AccountLinks] Linked player {playerUserId} to Discord user {discordUserId}.");
                return true;
            }
        }

        public bool TryGetDiscordUserId(string playerUserId, out ulong discordUserId, out string error) =>
            database.TryGetDiscordUserId(playerUserId, out discordUserId, out error);

        public bool TryGetPlayerUserId(ulong discordUserId, out string playerUserId, out string error) =>
            database.TryGetPlayerUserId(discordUserId, out playerUserId, out error);

        public bool TryUnlinkPlayer(string playerUserId, out ulong discordUserId, out string error)
        {
            bool success = database.TryUnlinkPlayer(playerUserId, out discordUserId, out error);
            if (success)
                Log.Info($"[AccountLinks] Unlinked player {playerUserId} from Discord user {discordUserId}.");
            return success;
        }

        public bool TryUnlinkDiscord(ulong discordUserId, out string playerUserId, out string error)
        {
            bool success = database.TryUnlinkDiscord(discordUserId, out playerUserId, out error);
            if (!success)
                return false;
            lock (syncRoot)
            {
                if (pendingCodeByDiscord.TryGetValue(discordUserId, out string code))
                {
                    pendingCodeByDiscord.Remove(discordUserId);
                    pendingByCode.Remove(code);
                }
            }
            Log.Info($"[AccountLinks] Unlinked player {playerUserId} from Discord user {discordUserId}.");
            return true;
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                pendingByCode.Clear();
                pendingCodeByDiscord.Clear();
            }
        }

        private void ImportLegacyYaml()
        {
            if (!File.Exists(FilePath))
                return;
            try
            {
                IDeserializer deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                string yaml = File.ReadAllText(FilePath, Encoding.UTF8);
                AccountLinkDatabase legacy = deserializer.Deserialize<AccountLinkDatabase>(yaml) ?? new AccountLinkDatabase();
                database.ImportLegacyAccountLinks("account-links:" + database.ServerId, legacy.Links ?? Enumerable.Empty<AccountLinkRecord>());
                Log.Info($"[AccountLinks] Legacy YAML was imported once and retained as a backup: {FilePath}");
            }
            catch (Exception exception)
            {
                Log.Error($"[AccountLinks] Failed to import legacy YAML. The file was not modified:\n{exception}");
            }
        }

        private static string GenerateCode()
        {
            byte[] bytes = new byte[10];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
                generator.GetBytes(bytes);
            char[] characters = new char[11];
            for (int index = 0; index < bytes.Length; index++)
                characters[index < 5 ? index : index + 1] = CodeAlphabet[bytes[index] & 31];
            characters[5] = '-';
            return new string(characters);
        }

        private static string NormalizeCode(string code) => (code ?? string.Empty).Trim().ToUpperInvariant();

        private void CleanupExpiredCodesLocked()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string code in pendingByCode.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
            {
                pendingCodeByDiscord.Remove(pendingByCode[code].DiscordUserId);
                pendingByCode.Remove(code);
            }
        }

        private sealed class PendingLink
        {
            public ulong DiscordUserId { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }
    }
}
