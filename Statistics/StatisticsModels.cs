namespace SmokyPluginV2.Statistics
{
    using System;
    using System.Collections.Generic;

    internal sealed class PlayerStatisticsRecord
    {
        public string SteamId { get; set; }
        public string Nickname { get; set; }
        public bool StatisticsPrivate { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public long RoundsCompleted { get; set; }
        public long HumanSeconds { get; set; }
        public long ScpSeconds { get; set; }
        public long SpectatorSeconds { get; set; }
        public long BestHumanKillsRound { get; set; }
        public long BestScpKillsRound { get; set; }
        public long LongestHumanLifeSeconds { get; set; }
        public long LongestScpLifeSeconds { get; set; }
        public long HumanKillsAsHuman { get; set; }
        public long HumanKillsAsScp { get; set; }
        public long ScpsDestroyed { get; set; }
        public long HumanDeaths { get; set; }
        public long ScpDeaths { get; set; }
        public long ClassDEscapesUncuffed { get; set; }
        public long? FastestClassDEscapeUncuffedSeconds { get; set; }
        public long ClassDEscapesCuffed { get; set; }
        public long? FastestClassDEscapeCuffedSeconds { get; set; }
        public long ScientistEscapesUncuffed { get; set; }
        public long? FastestScientistEscapeUncuffedSeconds { get; set; }
        public long ScientistEscapesCuffed { get; set; }
        public long? FastestScientistEscapeCuffedSeconds { get; set; }
        public long ClassDEscorted { get; set; }
        public long ScientistEscorted { get; set; }
        public long WarheadCountdownsStarted { get; set; }
        public long WarheadDetonations { get; set; }
        public long WarheadCountdownsStopped { get; set; }
        public long PocketEntries { get; set; }
        public long PocketEscapes { get; set; }
        public long LongestPocketSeconds { get; set; }
        public long ZombiesCreated { get; set; }
        public long GeneratorsActivated { get; set; }
        public long SystemRebootsStarted { get; set; }
        public long TeslaKillsAs079 { get; set; }
        public long PinkCandiesEaten { get; set; }
    }

    internal sealed class ServerStatisticsRecord
    {
        public string ServerName { get; set; }
        public long RoundsCompleted { get; set; }
        public long TotalRoundSeconds { get; set; }
        public long LongestRoundSeconds { get; set; }
        public long ScpWins { get; set; }
        public long FoundationWins { get; set; }
        public long ChaosWins { get; set; }
        public long Draws { get; set; }
        public long WarheadDetonations { get; set; }
        public long AutomaticWarheadDetonations { get; set; }
        public long PlayerWarheadDetonations { get; set; }
        public long MtfMainWaves { get; set; }
        public long ChaosMainWaves { get; set; }
        public long MtfReinforcementWaves { get; set; }
        public long ChaosReinforcementWaves { get; set; }
    }

    internal sealed class PlayerStatDelta
    {
        public Dictionary<string, long> Add { get; } = new Dictionary<string, long>();
        public Dictionary<string, long> Maximum { get; } = new Dictionary<string, long>();
        public Dictionary<string, long> MinimumNullable { get; } = new Dictionary<string, long>();

        public bool IsEmpty => Add.Count == 0 && Maximum.Count == 0 && MinimumNullable.Count == 0;

        public PlayerStatDelta Increment(string column, long amount = 1)
        {
            Add[column] = Add.TryGetValue(column, out long current) ? current + amount : amount;
            return this;
        }

        public PlayerStatDelta Max(string column, long value)
        {
            Maximum[column] = Maximum.TryGetValue(column, out long current) ? Math.Max(current, value) : value;
            return this;
        }

        public PlayerStatDelta Min(string column, long value)
        {
            MinimumNullable[column] = MinimumNullable.TryGetValue(column, out long current) ? Math.Min(current, value) : value;
            return this;
        }
    }

    internal sealed class ServerStatDelta
    {
        public Dictionary<string, long> Add { get; } = new Dictionary<string, long>();
        public Dictionary<string, long> Maximum { get; } = new Dictionary<string, long>();

        public ServerStatDelta Increment(string column, long amount = 1)
        {
            Add[column] = Add.TryGetValue(column, out long current) ? current + amount : amount;
            return this;
        }

        public ServerStatDelta Max(string column, long value)
        {
            Maximum[column] = Maximum.TryGetValue(column, out long current) ? Math.Max(current, value) : value;
            return this;
        }
    }
}
