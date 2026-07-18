namespace SmokyPluginV2.RolePreferences
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;

    using Exiled.API.Features;

    using HarmonyLib;

    using PlayerRoles;
    using PlayerRoles.RoleAssign;

    [HarmonyPatch]
    internal static class AtomicRoleAssignmentPatch
    {
        private static readonly MethodInfo VanillaScpSpawner = AccessTools.Method(typeof(ScpSpawner), nameof(ScpSpawner.SpawnScps));
        private static readonly MethodInfo VanillaHumanSpawner = AccessTools.Method(typeof(HumanSpawner), nameof(HumanSpawner.SpawnHumans));
        private static readonly MethodInfo PreferredScpSpawner = AccessTools.Method(typeof(AtomicRoleAssignmentPatch), nameof(SpawnPreferredScps));
        private static readonly MethodInfo PreferredHumanSpawner = AccessTools.Method(typeof(AtomicRoleAssignmentPatch), nameof(SpawnPreferredHumans));

        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(RoleAssigner), "OnRoundStarted")
            ?? throw new MissingMethodException(typeof(RoleAssigner).FullName, "OnRoundStarted");

        private static void Prefix()
        {
            Plugin.Instance?.RolePreferences?.BeginRoleAssignment();
        }

        private static Exception Finalizer(Exception __exception)
        {
            Plugin.Instance?.RolePreferences?.EndRoleAssignment();
            return __exception;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int scpCalls = 0;
            int humanCalls = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(VanillaScpSpawner))
                {
                    instruction.operand = PreferredScpSpawner;
                    scpCalls++;
                }
                else if (instruction.Calls(VanillaHumanSpawner))
                {
                    instruction.operand = PreferredHumanSpawner;
                    humanCalls++;
                }

                yield return instruction;
            }

            if (scpCalls != 1 || humanCalls != 1)
                throw new InvalidOperationException($"Expected one SCP and one human spawn call, replaced {scpCalls} and {humanCalls}.");

            Log.Info("[Role Preferences] Atomic pre-spawn role assignment hook has been installed.");
        }

        private static void SpawnPreferredScps(int targetScpNumber)
        {
            RolePreferenceService service = Plugin.Instance?.RolePreferences;
            if (service is null || !service.TrySpawnPreferredScps(targetScpNumber))
                ScpSpawner.SpawnScps(targetScpNumber);
        }

        private static void SpawnPreferredHumans(Team[] queue, int queueLength)
        {
            RolePreferenceService service = Plugin.Instance?.RolePreferences;
            if (service is null || !service.TrySpawnPreferredHumans(queue, queueLength))
                HumanSpawner.SpawnHumans(queue, queueLength);
        }
    }

    [HarmonyPatch(typeof(RoleAssigner), nameof(RoleAssigner.CheckPlayer))]
    internal static class TutorialRoleAssignmentCandidatePatch
    {
        private static void Postfix(ReferenceHub hub, ref bool __result)
        {
            if (!__result && Plugin.Instance?.RolePreferences?.ShouldIncludeTutorial(hub) == true)
                __result = true;
        }
    }
}
