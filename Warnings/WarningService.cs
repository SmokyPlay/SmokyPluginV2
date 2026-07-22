namespace SmokyPluginV2.Warnings
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;

    using Exiled.API.Features;

    using SmokyPluginV2.Database;

    using YamlDotNet.Serialization;
    using YamlDotNet.Serialization.NamingConventions;

    internal sealed class WarningService : IDisposable
    {
        private readonly MariaDbService database;

        public WarningService(MariaDbService database, bool importLegacyYaml)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            DirectoryPath = Path.Combine(Paths.Exiled, "Data", "SmokyPluginV2", Server.Port.ToString());
            FilePath = Path.Combine(DirectoryPath, "warnings.yml");
            if (importLegacyYaml)
                ImportLegacyYaml();
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }

        public bool TryAdd(WarningRecord warning, out string error) => database.TryAddWarning(warning, out error);

        public bool TryDelete(long warningId, out WarningRecord warning, out string error) =>
            database.TryDeleteWarning(warningId, out warning, out error);

        public bool TryGet(long warningId, out WarningRecord warning, out string error) =>
            database.TryGetWarning(warningId, out warning, out error);

        public bool TryGetForPlayer(string userId, out IReadOnlyList<WarningRecord> warnings, out string error) =>
            database.TryGetWarnings(userId, out warnings, out error);

        public void Dispose()
        {
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
                LegacyWarningDatabase legacy = deserializer.Deserialize<LegacyWarningDatabase>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new LegacyWarningDatabase();
                IEnumerable<WarningRecord> records = (legacy.Warnings ?? new List<LegacyWarningRecord>())
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
                    });
                database.ImportLegacyWarnings("warnings:" + database.ServerId, records);
                Log.Info($"[Warnings] Legacy YAML was imported once and retained as a backup: {FilePath}");
            }
            catch (Exception exception)
            {
                Log.Error($"[Warnings] Failed to import legacy YAML. The file was not modified:\n{exception}");
            }
        }

        private sealed class LegacyWarningDatabase
        {
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
