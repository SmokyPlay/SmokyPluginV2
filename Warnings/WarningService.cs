namespace SmokyPluginV2.Warnings
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;

    using Exiled.API.Features;

    using YamlDotNet.Serialization;
    using YamlDotNet.Serialization.NamingConventions;

    internal sealed class WarningService : IDisposable
    {
        private const int CurrentDatabaseVersion = 3;

        private readonly object syncRoot = new object();
        private readonly ISerializer serializer;
        private readonly IDeserializer deserializer;
        private WarningDatabase database = new WarningDatabase();
        private string lastLoadedYaml;

        public WarningService()
        {
            DirectoryPath = Path.Combine(Paths.Exiled, "Data", "SmokyPluginV2", Server.Port.ToString());
            FilePath = Path.Combine(DirectoryPath, "warnings.yml");

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
                    Log.Error($"[Warnings] {error}");
            }
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }

        public bool TryAdd(WarningRecord warning, out string error)
        {
            lock (syncRoot)
            {
                if (!TryReloadLocked(out error))
                    return false;

                long previousNextId = database.NextId;
                warning.Id = Math.Max(1, database.NextId);
                database.NextId = warning.Id + 1;
                database.Warnings.Add(warning);

                if (SaveLocked(out error))
                    return true;

                database.Warnings.Remove(warning);
                database.NextId = previousNextId;
                return false;
            }
        }

        public bool TryDelete(long warningId, out WarningRecord warning, out string error)
        {
            lock (syncRoot)
            {
                warning = null;
                if (!TryReloadLocked(out error))
                    return false;

                int index = database.Warnings.FindIndex(entry => entry.Id == warningId);
                if (index < 0)
                {
                    error = $"Предупреждение #{warningId} не найдено.";
                    return false;
                }

                warning = database.Warnings[index];
                database.Warnings.RemoveAt(index);

                if (SaveLocked(out error))
                {
                    warning = Clone(warning);
                    return true;
                }

                database.Warnings.Insert(index, warning);
                warning = null;
                return false;
            }
        }

        public bool TryGet(long warningId, out WarningRecord warning, out string error)
        {
            lock (syncRoot)
            {
                warning = null;
                if (!TryReloadLocked(out error))
                    return false;

                warning = Clone(database.Warnings.FirstOrDefault(entry => entry.Id == warningId));
                return true;
            }
        }

        public bool TryGetForPlayer(string userId, out IReadOnlyList<WarningRecord> warnings, out string error)
        {
            lock (syncRoot)
            {
                warnings = Array.Empty<WarningRecord>();
                if (!TryReloadLocked(out error))
                    return false;

                warnings = database.Warnings
                    .Where(entry => string.Equals(entry.PlayerUserId, userId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(entry => entry.Id)
                    .Select(Clone)
                    .ToList();
                return true;
            }
        }

        public void Dispose()
        {
            // Every mutation is persisted immediately. Saving here could overwrite a manual edit.
        }

        private bool TryReloadLocked(out string error)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (!File.Exists(FilePath))
                {
                    database = new WarningDatabase { Version = CurrentDatabaseVersion };
                    lastLoadedYaml = null;
                    if (!SaveLocked(out error))
                        return false;

                    Log.Info($"[Warnings] Created warning database: {FilePath}");
                    return true;
                }

                string yaml = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.Equals(yaml, lastLoadedYaml, StringComparison.Ordinal))
                {
                    error = null;
                    return true;
                }

                WarningDatabase loaded = deserializer.Deserialize<WarningDatabase>(yaml) ?? new WarningDatabase();
                bool migrated = loaded.Version < CurrentDatabaseVersion;
                if (migrated)
                    loaded = MigrateLegacyDatabase(yaml);

                loaded.Version = CurrentDatabaseVersion;
                loaded.Warnings ??= new List<WarningRecord>();
                loaded.NextId = Math.Max(loaded.NextId, loaded.Warnings.Count == 0 ? 1 : loaded.Warnings.Max(entry => entry.Id) + 1);

                database = loaded;
                lastLoadedYaml = yaml;

                if (migrated && !SaveLocked(out error))
                    return false;

                Log.Info($"[Warnings] Loaded {database.Warnings.Count} warning record(s) from {FilePath}");
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Не удалось прочитать файл предупреждений. Исправьте YAML; текущий файл не будет перезаписан.";
                Log.Error($"[Warnings] Failed to reload {FilePath}:\n{exception}");
                return false;
            }
        }

        private WarningDatabase MigrateLegacyDatabase(string yaml)
        {
            LegacyWarningDatabase legacy = deserializer.Deserialize<LegacyWarningDatabase>(yaml) ?? new LegacyWarningDatabase();
            List<LegacyWarningRecord> legacyWarnings = legacy.Warnings ?? new List<LegacyWarningRecord>();
            List<WarningRecord> warnings = legacyWarnings
                .Where(entry => entry.IsActive)
                .Select(entry => new WarningRecord
                {
                    Id = entry.Id,
                    PlayerUserId = entry.PlayerUserId,
                    PlayerNickname = entry.PlayerNickname,
                    ModeratorUserId = entry.ModeratorUserId,
                    ModeratorNickname = entry.ModeratorNickname,
                    IssuedAtUtc = entry.IssuedAtUtc,
                    Reason = entry.Reason,
                })
                .ToList();

            long nextId = Math.Max(legacy.NextId, legacyWarnings.Count == 0 ? 1 : legacyWarnings.Max(entry => entry.Id) + 1);
            Log.Info($"[Warnings] Migrating warning database to version {CurrentDatabaseVersion}. Removed warnings and stored hierarchy values will not be carried over.");
            return new WarningDatabase
            {
                Version = CurrentDatabaseVersion,
                NextId = nextId,
                Warnings = warnings,
            };
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
                        error = "Файл предупреждений был изменён одновременно с командой. Команда отменена; повторите её после завершения ручного редактирования.";
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
                error = "Не удалось сохранить файл предупреждений. Подробности записаны в консоль сервера.";
                Log.Error($"[Warnings] Failed to save {FilePath}:\n{exception}");
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
                    // A stale temporary file is harmless and will be overwritten by the next save.
                }
            }
        }

        private void ReplaceWithoutBackup(string temporaryPath)
        {
            File.Copy(temporaryPath, FilePath, true);
            File.Delete(temporaryPath);
        }

        private static WarningRecord Clone(WarningRecord source)
        {
            if (source is null)
                return null;

            return new WarningRecord
            {
                Id = source.Id,
                PlayerUserId = source.PlayerUserId,
                PlayerNickname = source.PlayerNickname,
                ModeratorUserId = source.ModeratorUserId,
                ModeratorNickname = source.ModeratorNickname,
                IssuedAtUtc = source.IssuedAtUtc,
                Reason = source.Reason,
            };
        }

        private sealed class LegacyWarningDatabase
        {
            public long NextId { get; set; } = 1;

            public List<LegacyWarningRecord> Warnings { get; set; } = new List<LegacyWarningRecord>();
        }

        private sealed class LegacyWarningRecord
        {
            public long Id { get; set; }

            public string PlayerUserId { get; set; } = string.Empty;

            public string PlayerNickname { get; set; } = string.Empty;

            public string ModeratorUserId { get; set; } = string.Empty;

            public string ModeratorNickname { get; set; } = string.Empty;

            public DateTime IssuedAtUtc { get; set; }

            public string Reason { get; set; } = string.Empty;

            public bool IsActive { get; set; } = true;
        }
    }

    internal sealed class WarningModerator
    {
        public string UserId { get; set; }

        public string Nickname { get; set; }

        public byte KickPower { get; set; }

        public bool IsServer { get; set; }
    }
}
