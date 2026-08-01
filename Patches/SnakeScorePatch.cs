namespace SmokyPluginV2.Patches
{
    using System;
    using System.Collections;
    using System.Reflection;

    using Exiled.API.Features;

    using HarmonyLib;

    [HarmonyPatch]
    internal static class SnakeScorePatch
    {
        private const string SnakeEngineTypeName = "InventorySystem.Items.Keycards.Snake.SnakeEngine";
        private const string ChaosKeycardTypeName = "InventorySystem.Items.Keycards.ChaosKeycardItem";

        private static Type snakeEngineType;
        private static FieldInfo snakeSessionsField;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            snakeEngineType = AccessTools.TypeByName(SnakeEngineTypeName);
            Type chaosKeycardType = AccessTools.TypeByName(ChaosKeycardTypeName);
            snakeSessionsField = chaosKeycardType == null
                ? null
                : AccessTools.Field(chaosKeycardType, "SnakeSessions");

            if (snakeEngineType != null && snakeSessionsField != null)
                return true;

            Log.Warn("[Statistics] Snake internals were not found; snake high scores will not be recorded on this game version.");
            return false;
        }

        private static MethodBase TargetMethod() =>
            AccessTools.PropertySetter(snakeEngineType, "Score");

        [HarmonyPostfix]
        private static void Postfix(object __instance, int __0)
        {
            if (__instance is null || __0 <= 0)
                return;

            try
            {
                if (!TryGetItemSerial(__instance, out ushort itemSerial))
                    return;

                Plugin.Instance?.Statistics?.OnSnakeScoreIncreased(itemSerial, __0);
            }
            catch (Exception exception)
            {
                Log.Error($"[Statistics] Failed to process a snake score update:\n{exception}");
            }
        }

        private static bool TryGetItemSerial(object engine, out ushort itemSerial)
        {
            itemSerial = 0;
            if (!(snakeSessionsField?.GetValue(null) is IDictionary sessions))
                return false;

            foreach (DictionaryEntry session in sessions)
            {
                if (!ReferenceEquals(session.Value, engine) || !(session.Key is ushort serial))
                    continue;

                itemSerial = serial;
                return true;
            }

            return false;
        }
    }
}
