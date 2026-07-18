namespace SmokyPluginV2.RolePreferences
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;

    using HarmonyLib;

    using PlayerRoles;
    using PlayerRoles.RoleAssign;

    internal static class RoleAssignmentAccess
    {
        private static readonly FieldInfo EnqueuedScpsField = RequireField(typeof(ScpSpawner), "EnqueuedScps");
        private static readonly MethodInfo NextScpGetter = RequireMethod(AccessTools.PropertyGetter(typeof(ScpSpawner), "NextScp"), "ScpSpawner.NextScp");
        private static readonly MethodInfo AssignScpMethod = RequireMethod(AccessTools.Method(typeof(ScpSpawner), "AssignScp"), "ScpSpawner.AssignScp");

        private static readonly FieldInfo HumanQueueField = RequireField(typeof(HumanSpawner), "_humanQueue");
        private static readonly FieldInfo QueueClockField = RequireField(typeof(HumanSpawner), "_queueClock");
        private static readonly FieldInfo QueueLengthField = RequireField(typeof(HumanSpawner), "_queueLength");
        private static readonly MethodInfo NextHumanRoleGetter = RequireMethod(AccessTools.PropertyGetter(typeof(HumanSpawner), "NextHumanRoleToSpawn"), "HumanSpawner.NextHumanRoleToSpawn");
        private static readonly FieldInfo HumanHistoryField = RequireField(typeof(HumanSpawner), "History");

        private static readonly Type RoleHistoryType = AccessTools.Inner(typeof(HumanSpawner), "RoleHistory")
            ?? throw new MissingMemberException(typeof(HumanSpawner).FullName, "RoleHistory");
        private static readonly PropertyInfo RoleHistoryProperty = AccessTools.Property(RoleHistoryType, "History")
            ?? throw new MissingMemberException(RoleHistoryType.FullName, "History");
        private static readonly MethodInfo RegisterRoleMethod = RequireMethod(AccessTools.Method(RoleHistoryType, "RegisterRole"), "HumanSpawner.RoleHistory.RegisterRole");

        internal static List<RoleTypeId> GenerateScpRoles(int count)
        {
            List<RoleTypeId> roles = (List<RoleTypeId>)EnqueuedScpsField.GetValue(null);
            roles.Clear();
            for (int i = 0; i < count; i++)
                roles.Add((RoleTypeId)NextScpGetter.Invoke(null, null));

            return roles;
        }

        internal static void AssignScp(List<ReferenceHub> players, RoleTypeId role, List<RoleTypeId> remainingRoles) =>
            AssignScpMethod.Invoke(null, new object[] { players, role, remainingRoles });

        internal static List<RoleTypeId> GenerateHumanRoles(Team[] queue, int queueLength, int count)
        {
            HumanQueueField.SetValue(null, queue);
            QueueClockField.SetValue(null, 0);
            QueueLengthField.SetValue(null, queueLength);

            List<RoleTypeId> roles = new List<RoleTypeId>(count);
            for (int i = 0; i < count; i++)
                roles.Add((RoleTypeId)NextHumanRoleGetter.Invoke(null, null));

            return roles;
        }

        internal static int GetHumanRoleCount(string userId, RoleTypeId role)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return 0;

            IDictionary history = (IDictionary)HumanHistoryField.GetValue(null);
            if (history is null || !history.Contains(userId))
                return 0;

            object record = history[userId];
            RoleTypeId[] roles = (RoleTypeId[])RoleHistoryProperty.GetValue(record);
            int count = 0;
            foreach (RoleTypeId previousRole in roles)
            {
                if (previousRole == role)
                    count++;
            }

            return count;
        }

        internal static void RegisterHumanRole(string userId, RoleTypeId role)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return;

            IDictionary history = (IDictionary)HumanHistoryField.GetValue(null);
            if (history is null)
                throw new InvalidOperationException("HumanSpawner.History has not been initialized.");

            object record;
            if (history.Contains(userId))
            {
                record = history[userId];
            }
            else
            {
                record = Activator.CreateInstance(RoleHistoryType, true);
                history[userId] = record;
            }

            RegisterRoleMethod.Invoke(record, new object[] { role });
        }

        private static FieldInfo RequireField(Type type, string name) =>
            AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);

        private static MethodInfo RequireMethod(MethodInfo method, string name) =>
            method ?? throw new MissingMethodException(name);
    }
}
