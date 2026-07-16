namespace SmokyPluginV2.AccountLinks
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;

    using Exiled.API.Features;

    using YamlDotNet.Serialization;
    using YamlDotNet.Serialization.NamingConventions;

    internal sealed class AccountLinkService : IDisposable
    {
        private const int CurrentDatabaseVersion = 1;
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private readonly object syncRoot = new object();
        private readonly Dictionary<string, PendingLink> pendingByCode = new Dictionary<string, PendingLink>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, string> pendingCodeByDiscord = new Dictionary<ulong, string>();
        private readonly ISerializer serializer;
        private readonly IDeserializer deserializer;
        private AccountLinkDatabase database = new AccountLinkDatabase();
        private string lastLoadedYaml;

        public AccountLinkService()
        {
            DirectoryPath = Path.Combine(Paths.Exiled, "Data", "SmokyPluginV2", Server.Port.ToString());
            FilePath = Path.Combine(DirectoryPath, "account-links.yml");

            serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .DisableAliases()
                .Build();
            deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            Directory.CreateDirectory(DirectoryPath);
            lock (syncRoot)
            {
                if (!TryReloadLocked(out string error))
                    Log.Error($"[AccountLinks] {error}");
            }
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

                if (!TryReloadLocked(out error))
                    return false;

                AccountLinkRecord existingLink = database.Links.FirstOrDefault(link => link.DiscordUserId == discordUserId);
                if (existingLink != null)
                {
                    error = $"Discord уже привязан к игровому аккаунту `{existingLink.PlayerUserId}`. Сначала используйте `/unlink`.";
                    return false;
                }

                if (pendingCodeByDiscord.TryGetValue(discordUserId, out string existingCode))
                {
                    pendingByCode.Remove(existingCode);
                    pendingCodeByDiscord.Remove(discordUserId);
                }

                do
                {
                    code = GenerateCode();
                }
                while (pendingByCode.ContainsKey(code));

                expiresAtUtc = DateTime.UtcNow.Add(lifetime);
                pendingByCode[code] = new PendingLink
                {
                    DiscordUserId = discordUserId,
                    ExpiresAtUtc = expiresAtUtc,
                };
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
                if (string.IsNullOrEmpty(normalizedCode) || !pendingByCode.TryGetValue(normalizedCode, out PendingLink pending))
                {
                    error = "Код привязки недействителен или истёк. Получите новый код командой `/link` в Discord.";
                    return false;
                }

                if (!TryReloadLocked(out error))
                    return false;

                AccountLinkRecord steamLink = database.Links.FirstOrDefault(link =>
                    string.Equals(link.PlayerUserId, playerUserId, StringComparison.OrdinalIgnoreCase));
                if (steamLink != null)
                {
                    error = steamLink.DiscordUserId == pending.DiscordUserId
                        ? "Этот игровой аккаунт уже привязан к вашему Discord."
                        : "Этот игровой аккаунт уже привязан к другому Discord. Сначала отвяжите его командой `.unlink`.";
                    return false;
                }

                AccountLinkRecord discordLink = database.Links.FirstOrDefault(link => link.DiscordUserId == pending.DiscordUserId);
                if (discordLink != null)
                {
                    error = "Этот Discord уже привязан к другому игровому аккаунту. Сначала используйте `/unlink`.";
                    return false;
                }

                AccountLinkRecord record = new AccountLinkRecord
                {
                    PlayerUserId = playerUserId,
                    DiscordUserId = pending.DiscordUserId,
                    LinkedAtUtc = DateTime.UtcNow,
                };
                database.Links.Add(record);

                if (!SaveLocked(out error))
                {
                    database.Links.Remove(record);
                    return false;
                }

                pendingByCode.Remove(normalizedCode);
                pendingCodeByDiscord.Remove(pending.DiscordUserId);
                discordUserId = pending.DiscordUserId;
                Log.Info($"[AccountLinks] Linked player {playerUserId} to Discord user {discordUserId}.");
                return true;
            }
        }

        public bool TryGetDiscordUserId(string playerUserId, out ulong discordUserId, out string error)
        {
            lock (syncRoot)
            {
                discordUserId = 0;
                if (!TryReloadLocked(out error))
                    return false;

                AccountLinkRecord link = database.Links.FirstOrDefault(entry =>
                    string.Equals(entry.PlayerUserId, playerUserId, StringComparison.OrdinalIgnoreCase));
                if (link != null)
                    discordUserId = link.DiscordUserId;

                error = null;
                return true;
            }
        }

        public bool TryGetPlayerUserId(ulong discordUserId, out string playerUserId, out string error)
        {
            lock (syncRoot)
            {
                playerUserId = null;
                if (!TryReloadLocked(out error))
                    return false;

                playerUserId = database.Links.FirstOrDefault(entry => entry.DiscordUserId == discordUserId)?.PlayerUserId;
                error = null;
                return true;
            }
        }

        public bool TryUnlinkPlayer(string playerUserId, out ulong discordUserId, out string error)
        {
            lock (syncRoot)
            {
                discordUserId = 0;
                if (!TryReloadLocked(out error))
                    return false;

                AccountLinkRecord link = database.Links.FirstOrDefault(entry =>
                    string.Equals(entry.PlayerUserId, playerUserId, StringComparison.OrdinalIgnoreCase));
                if (link is null)
                {
                    error = "Игровой аккаунт не привязан к Discord.";
                    return false;
                }

                database.Links.Remove(link);
                if (!SaveLocked(out error))
                {
                    database.Links.Add(link);
                    return false;
                }

                discordUserId = link.DiscordUserId;
                Log.Info($"[AccountLinks] Unlinked player {playerUserId} from Discord user {discordUserId}.");
                return true;
            }
        }

        public bool TryUnlinkDiscord(ulong discordUserId, out string playerUserId, out string error)
        {
            lock (syncRoot)
            {
                playerUserId = null;
                if (!TryReloadLocked(out error))
                    return false;

                AccountLinkRecord link = database.Links.FirstOrDefault(entry => entry.DiscordUserId == discordUserId);
                if (link is null)
                {
                    error = "Ваш Discord не привязан к игровому аккаунту.";
                    return false;
                }

                database.Links.Remove(link);
                if (!SaveLocked(out error))
                {
                    database.Links.Add(link);
                    return false;
                }

                playerUserId = link.PlayerUserId;
                if (pendingCodeByDiscord.TryGetValue(discordUserId, out string pendingCode))
                {
                    pendingCodeByDiscord.Remove(discordUserId);
                    pendingByCode.Remove(pendingCode);
                }

                Log.Info($"[AccountLinks] Unlinked player {playerUserId} from Discord user {discordUserId}.");
                return true;
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                pendingByCode.Clear();
                pendingCodeByDiscord.Clear();
            }
        }

        private static string GenerateCode()
        {
            byte[] bytes = new byte[10];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
                generator.GetBytes(bytes);

            char[] characters = new char[11];
            for (int index = 0; index < bytes.Length; index++)
            {
                int targetIndex = index < 5 ? index : index + 1;
                characters[targetIndex] = CodeAlphabet[bytes[index] & 31];
            }

            characters[5] = '-';
            return new string(characters);
        }

        private static string NormalizeCode(string code) =>
            (code ?? string.Empty).Trim().ToUpperInvariant();

        private void CleanupExpiredCodesLocked()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string code in pendingByCode.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
            {
                ulong discordUserId = pendingByCode[code].DiscordUserId;
                pendingByCode.Remove(code);
                pendingCodeByDiscord.Remove(discordUserId);
            }
        }

        private bool TryReloadLocked(out string error)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (!File.Exists(FilePath))
                {
                    database = new AccountLinkDatabase { Version = CurrentDatabaseVersion };
                    lastLoadedYaml = null;
                    if (!SaveLocked(out error))
                        return false;

                    Log.Info($"[AccountLinks] Created account link database: {FilePath}");
                    return true;
                }

                string yaml = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.Equals(yaml, lastLoadedYaml, StringComparison.Ordinal))
                {
                    error = null;
                    return true;
                }

                AccountLinkDatabase loaded = deserializer.Deserialize<AccountLinkDatabase>(yaml) ?? new AccountLinkDatabase();
                loaded.Version = CurrentDatabaseVersion;
                loaded.Links ??= new List<AccountLinkRecord>();

                if (loaded.Links.Any(link => string.IsNullOrWhiteSpace(link.PlayerUserId) || link.DiscordUserId == 0) ||
                    loaded.Links.GroupBy(link => link.PlayerUserId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
                    loaded.Links.GroupBy(link => link.DiscordUserId).Any(group => group.Count() > 1))
                {
                    throw new InvalidDataException("Account links must contain unique, non-empty Steam and Discord IDs.");
                }

                database = loaded;
                lastLoadedYaml = yaml;
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Не удалось прочитать файл привязок. Исправьте account-links.yml; файл не будет перезаписан.";
                Log.Error($"[AccountLinks] Failed to reload {FilePath}:\n{exception}");
                return false;
            }
        }

        private bool SaveLocked(out string error)
        {
            string temporaryPath = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(FilePath) && lastLoadedYaml is not null)
                {
                    string currentYaml = File.ReadAllText(FilePath, Encoding.UTF8);
                    if (!string.Equals(currentYaml, lastLoadedYaml, StringComparison.Ordinal))
                    {
                        error = "Файл привязок был изменён одновременно с командой. Повторите команду после завершения ручного редактирования.";
                        return false;
                    }
                }

                string yaml = serializer.Serialize(database);
                File.WriteAllText(temporaryPath, yaml, new UTF8Encoding(false));
                if (!File.Exists(FilePath))
                {
                    File.Move(temporaryPath, FilePath);
                }
                else
                {
                    try
                    {
                        File.Replace(temporaryPath, FilePath, null, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceWithoutBackup(temporaryPath);
                    }
                    catch (IOException)
                    {
                        ReplaceWithoutBackup(temporaryPath);
                    }
                }

                lastLoadedYaml = yaml;
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Не удалось сохранить файл привязок. Подробности записаны в консоль сервера.";
                Log.Error($"[AccountLinks] Failed to save {FilePath}:\n{exception}");
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // The next save safely overwrites a stale temporary file.
                }
            }
        }

        private void ReplaceWithoutBackup(string temporaryPath)
        {
            File.Copy(temporaryPath, FilePath, true);
            File.Delete(temporaryPath);
        }

        private sealed class PendingLink
        {
            public ulong DiscordUserId { get; set; }

            public DateTime ExpiresAtUtc { get; set; }
        }
    }
}
