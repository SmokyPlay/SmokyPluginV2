namespace SmokyPluginV2.Database
{
    using System;
    using System.IO;
    using System.Text;

    using Exiled.API.Features;

    using YamlDotNet.Serialization;
    using YamlDotNet.Serialization.NamingConventions;

    internal sealed class SharedDatabaseSettings
    {
        public string Host { get; set; } = "127.0.0.1";

        public ushort Port { get; set; } = 3306;

        public string Name { get; set; } = "smoky_plugin_v2";

        public string Username { get; set; } = "smoky_plugin_v2";

        public string Password { get; set; } = "CHANGE_ME";

        public bool UseTls { get; set; }

        public uint ConnectionTimeoutSeconds { get; set; } = 5;

        public uint MaximumPoolSize { get; set; } = 10;
    }

    internal static class SharedDatabaseConfig
    {
        private const string PluginConfigDirectoryName = "smoky_plugin_v2";

        public static string FilePath => Path.Combine(
            Paths.Exiled,
            "Configs",
            "Plugins",
            PluginConfigDirectoryName,
            "database.yml");

        public static SharedDatabaseSettings Load()
        {
            if (!File.Exists(FilePath))
            {
                CreateTemplate();
                throw new InvalidOperationException(
                    $"Shared MariaDB configuration was created at '{FilePath}'. Fill it in and restart the server.");
            }

            string yaml = File.ReadAllText(FilePath, Encoding.UTF8);
            SharedDatabaseSettings settings = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<SharedDatabaseSettings>(yaml) ?? new SharedDatabaseSettings();

            if (string.Equals(settings.Password, "CHANGE_ME", StringComparison.Ordinal))
                throw new InvalidOperationException($"Set the MariaDB password in '{FilePath}' and restart the server.");

            return settings;
        }

        private static void CreateTemplate()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string yaml = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build()
                .Serialize(new SharedDatabaseSettings());

            try
            {
                using (FileStream stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(yaml);
            }
            catch (IOException) when (File.Exists(FilePath))
            {
                // Another game-server process created the same shared template first.
            }
        }
    }
}
