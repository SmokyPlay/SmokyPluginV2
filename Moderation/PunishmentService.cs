namespace SmokyPluginV2.Moderation
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

    internal sealed class PunishmentService : IDisposable
    {
        private readonly PostgreSqlService database;

        public PunishmentService(PostgreSqlService database, bool importLegacyYaml)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            LegacyFilePath = Path.Combine(Paths.Exiled, "Data", "SmokyPluginV2", Server.Port.ToString(), "warnings.yml");
            if (importLegacyYaml)
                ImportLegacyYaml();
        }

        public string LegacyFilePath { get; }
        public bool TryAdd(PunishmentRecord record, out string error) => database.TryAddPunishment(record, out error);
        public bool TryGet(long id, out PunishmentRecord record, out string error) => database.TryGetPunishment(id, out record, out error);
        public bool TryGetHistory(string userId, out PunishmentHistory history, out string error) => database.TryGetPunishmentHistory(userId, out history, out error);
        public bool TryGetPendingWarningNotifications(string userId, out IReadOnlyList<PunishmentRecord> records, out string error) => database.TryGetPendingWarningNotifications(userId, out records, out error);
        public bool TryGetDiscordUserId(string userId, out ulong discordUserId, out string error) => database.TryGetDiscordUserId(userId, out discordUserId, out error);
        public bool TryMarkNotified(long id, DateTime notifiedAtUtc, out string error) => database.TryMarkPunishmentNotified(id, notifiedAtUtc, out error);
        public bool TryDelete(long id, out PunishmentRecord record, out string error) => database.TryDeletePunishment(id, out record, out error);
        public bool TryDeleteActiveBans(string userId, DateTime nowUtc, out int deleted, out string error) => database.TryDeleteActiveBans(userId, nowUtc, out deleted, out error);
        public void Dispose() { }

        private void ImportLegacyYaml()
        {
            if (!File.Exists(LegacyFilePath))
                return;
            try
            {
                IDeserializer deserializer = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
                LegacyDatabase legacy = deserializer.Deserialize<LegacyDatabase>(File.ReadAllText(LegacyFilePath, Encoding.UTF8)) ?? new LegacyDatabase();
                database.ImportLegacyPunishments("warnings:" + database.ServerId, (legacy.Warnings ?? new List<LegacyWarning>())
                    .Where(item => item.IsActive)
                    .Select(item => new PunishmentRecord
                    {
                        PlayerUserId = item.PlayerUserId,
                        PlayerNickname = item.PlayerNickname,
                        ModeratorUserId = item.ModeratorUserId,
                        Type = PunishmentType.Warning,
                        IssuedAtUtc = item.IssuedAtUtc,
                        NotifiedAtUtc = item.IssuedAtUtc,
                        Reason = item.Reason,
                    }));
                Log.Info($"[Moderation] Legacy warning YAML was imported once and retained as a backup: {LegacyFilePath}");
            }
            catch (Exception exception)
            {
                Log.Error($"[Moderation] Failed to import legacy warning YAML. The file was not modified:\n{exception}");
            }
        }

        private sealed class LegacyDatabase { public List<LegacyWarning> Warnings { get; set; } = new List<LegacyWarning>(); }
        private sealed class LegacyWarning
        {
            public string PlayerUserId { get; set; } = string.Empty;
            public string PlayerNickname { get; set; } = string.Empty;
            public string ModeratorUserId { get; set; } = string.Empty;
            public DateTime IssuedAtUtc { get; set; }
            public string Reason { get; set; } = string.Empty;
            public bool IsActive { get; set; } = true;
        }
    }
}
