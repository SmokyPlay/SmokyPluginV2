namespace SmokyPluginV2.Patches
{
    using System.Collections.Generic;

    using HarmonyLib;

    using Respawning.Waves;

    using SmokyPluginV2.Discord;

    [HarmonyPatch(
        typeof(WaveSpawner),
        nameof(WaveSpawner.SpawnWave),
        new[] { typeof(SpawnableWaveBase) })]
    internal static class RespawnWaveLoggingPatch
    {
        [HarmonyPostfix]
        private static void Postfix(SpawnableWaveBase __0, List<ReferenceHub> __result)
        {
            DiscordLogService.Current?.LogRespawnWave(__0, __result?.Count ?? 0);
        }
    }
}
