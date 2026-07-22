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
            if (__0 is not null)
            {
                Plugin.Instance?.Statistics?.OnWaveSpawned(__0);

                string waveType = __0.GetType().Name;
                bool isMiniWave = waveType.IndexOf("Mini", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool isChaos = waveType.IndexOf("Chaos", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool isNtf = waveType.IndexOf("Ntf", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isMiniWave && (isChaos || isNtf))
                {
                    Plugin.Instance?.LateJoinSpawns?.OnMainWaveSpawned(isChaos);
                }
            }

            DiscordLogService.Current?.LogRespawnWave(__0, __result?.Count ?? 0);
        }
    }
}
