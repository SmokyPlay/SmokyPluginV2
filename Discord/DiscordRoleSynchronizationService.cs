namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    using Exiled.API.Features;

    internal enum DiscordRoleOperationAction
    {
        Add,
        Remove,
    }

    internal enum DiscordRoleOperationStatus
    {
        Added,
        Removed,
        AlreadyPresent,
        AlreadyAbsent,
        PreservedByDesiredRole,
        Failed,
        Canceled,
    }

    internal sealed class DiscordRoleOperationResult
    {
        public ulong RoleId { get; set; }

        public string RoleLabel { get; set; }

        public DiscordRoleOperationAction Action { get; set; }

        public DiscordRoleOperationStatus Status { get; set; }

        public string Error { get; set; }

        public bool IsSettled =>
            Status == DiscordRoleOperationStatus.Removed ||
            Status == DiscordRoleOperationStatus.AlreadyAbsent ||
            Status == DiscordRoleOperationStatus.PreservedByDesiredRole;
    }

    internal sealed class DiscordRoleReconciliationReport
    {
        public bool IsCanceled { get; set; }

        public IReadOnlyCollection<DiscordRoleOperationResult> Operations { get; set; } =
            Array.Empty<DiscordRoleOperationResult>();

        public bool IsRemovalSettled(ulong roleId) =>
            Operations.Any(operation =>
                operation.RoleId == roleId &&
                operation.Action == DiscordRoleOperationAction.Remove &&
                operation.IsSettled);
    }

    internal sealed class DiscordRoleSynchronizationResult
    {
        public bool IsSuccess { get; set; }

        public bool IsGuildMember { get; set; }

        public bool IsCanceled { get; set; }

        public IReadOnlyCollection<ulong> EffectiveRoleIds { get; set; } = Array.Empty<ulong>();

        public Task<DiscordRoleReconciliationReport> ReconciliationTask { get; set; }

        public string Error { get; set; }
    }

    internal sealed class DiscordRoleSynchronizationService
    {
        private readonly DiscordLogService discord;

        public DiscordRoleSynchronizationService(DiscordLogService discord)
        {
            this.discord = discord ?? throw new ArgumentNullException(nameof(discord));
        }

        public Task<DiscordGuildMemberResult> GetGuildMemberAsync(ulong discordUserId) =>
            discord.GetGuildMemberResultAsync(discordUserId);

        public async Task<DiscordRoleSynchronizationResult> SynchronizeAsync(
            ulong discordUserId,
            IEnumerable<string> desiredGroups,
            IEnumerable<string> authoritativeGroups,
            IEnumerable<DiscordRoleGroupMapping> mappings,
            bool isSteamLinked,
            ulong linkedDiscordRoleId,
            Func<bool> shouldContinue = null)
        {
            DiscordGuildMemberResult member =
                await GetGuildMemberAsync(discordUserId).ConfigureAwait(false);
            return Synchronize(
                discordUserId,
                member,
                desiredGroups,
                authoritativeGroups,
                mappings,
                isSteamLinked,
                linkedDiscordRoleId,
                shouldContinue);
        }

        public DiscordRoleSynchronizationResult Synchronize(
            ulong discordUserId,
            DiscordGuildMemberResult member,
            IEnumerable<string> desiredGroups,
            IEnumerable<string> authoritativeGroups,
            IEnumerable<DiscordRoleGroupMapping> mappings,
            bool isSteamLinked,
            ulong linkedDiscordRoleId,
            Func<bool> shouldContinue = null)
        {
            List<DiscordRoleGroupMapping> validMappings = (mappings ?? Array.Empty<DiscordRoleGroupMapping>())
                .Where(mapping =>
                    mapping != null &&
                    mapping.DiscordRoleId != 0 &&
                    !string.IsNullOrWhiteSpace(mapping.RemoteAdminGroup))
                .ToList();
            HashSet<string> desired = new HashSet<string>(
                desiredGroups ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> authoritative = new HashSet<string>(
                authoritativeGroups ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            List<DiscordRoleGroupMapping> desiredMappings = ResolveMappings(
                desired,
                validMappings,
                discordUserId,
                true);
            List<DiscordRoleGroupMapping> authoritativeMappings = ResolveMappings(
                authoritative,
                validMappings,
                discordUserId,
                false);

            Dictionary<ulong, string> roleLabels = validMappings
                .GroupBy(mapping => mapping.DiscordRoleId)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().RemoteAdminGroup.Trim());
            HashSet<ulong> desiredRoleIds = new HashSet<ulong>(
                desiredMappings.Select(mapping => mapping.DiscordRoleId));
            HashSet<ulong> authoritativeRoleIds = new HashSet<ulong>(
                authoritativeMappings.Select(mapping => mapping.DiscordRoleId));
            if (linkedDiscordRoleId != 0)
            {
                authoritativeRoleIds.Add(linkedDiscordRoleId);
                roleLabels[linkedDiscordRoleId] = "steam-link";
                if (isSteamLinked)
                    desiredRoleIds.Add(linkedDiscordRoleId);
            }

            if (member == null || !member.IsSuccess)
            {
                return new DiscordRoleSynchronizationResult
                {
                    Error = member?.Error ?? "Discord role lookup failed.",
                };
            }

            if (!member.IsGuildMember)
            {
                return new DiscordRoleSynchronizationResult
                {
                    IsSuccess = true,
                    IsGuildMember = false,
                    ReconciliationTask = Task.FromResult(CreateAbsentMemberReport(
                        desiredRoleIds,
                        authoritativeRoleIds,
                        roleLabels)),
                };
            }

            HashSet<ulong> currentRoleIds = new HashSet<ulong>(member.RoleIds ?? Array.Empty<ulong>());
            HashSet<ulong> effectiveRoleIds = new HashSet<ulong>(currentRoleIds.Where(roleId =>
                !authoritativeRoleIds.Contains(roleId) || desiredRoleIds.Contains(roleId)));
            effectiveRoleIds.UnionWith(desiredRoleIds);

            if (shouldContinue?.Invoke() == false)
                return Canceled(effectiveRoleIds);

            Task<DiscordRoleReconciliationReport> reconciliationTask = ReconcileRolesAsync(
                discordUserId,
                currentRoleIds,
                desiredRoleIds,
                authoritativeRoleIds,
                roleLabels,
                shouldContinue);

            return new DiscordRoleSynchronizationResult
            {
                IsSuccess = true,
                IsGuildMember = true,
                EffectiveRoleIds = effectiveRoleIds.ToArray(),
                ReconciliationTask = reconciliationTask,
            };
        }

        private async Task<DiscordRoleReconciliationReport> ReconcileRolesAsync(
            ulong discordUserId,
            ISet<ulong> currentRoleIds,
            ISet<ulong> desiredRoleIds,
            ISet<ulong> authoritativeRoleIds,
            IReadOnlyDictionary<ulong, string> roleLabels,
            Func<bool> shouldContinue)
        {
            List<DiscordRoleOperationResult> operations = new List<DiscordRoleOperationResult>();
            foreach (ulong roleId in desiredRoleIds)
            {
                if (currentRoleIds.Contains(roleId))
                {
                    operations.Add(Operation(
                        roleId,
                        roleLabels,
                        DiscordRoleOperationAction.Add,
                        DiscordRoleOperationStatus.AlreadyPresent));
                    continue;
                }

                if (shouldContinue?.Invoke() == false)
                    return CanceledReport(operations);

                DiscordRoleOperationResult operation;
                try
                {
                    DiscordRoleAssignmentResult assignment =
                        await discord.AddGuildMemberRoleAsync(discordUserId, roleId).ConfigureAwait(false);
                    operation = Operation(
                        roleId,
                        roleLabels,
                        DiscordRoleOperationAction.Add,
                        assignment.IsSuccess
                            ? DiscordRoleOperationStatus.Added
                            : DiscordRoleOperationStatus.Failed,
                        assignment.Error);
                }
                catch (Exception exception)
                {
                    operation = Operation(
                        roleId,
                        roleLabels,
                        DiscordRoleOperationAction.Add,
                        DiscordRoleOperationStatus.Failed,
                        exception.Message);
                }

                operations.Add(operation);
                LogOperation(discordUserId, operation);
            }

            foreach (ulong roleId in authoritativeRoleIds)
            {
                if (desiredRoleIds.Contains(roleId))
                {
                    operations.Add(Operation(
                        roleId,
                        roleLabels,
                        DiscordRoleOperationAction.Remove,
                        DiscordRoleOperationStatus.PreservedByDesiredRole));
                    continue;
                }

                if (!currentRoleIds.Contains(roleId))
                {
                    operations.Add(Operation(
                        roleId,
                        roleLabels,
                        DiscordRoleOperationAction.Remove,
                        DiscordRoleOperationStatus.AlreadyAbsent));
                    continue;
                }

                if (shouldContinue?.Invoke() == false)
                    return CanceledReport(operations);

                DiscordRoleOperationResult operation;
                try
                {
                    DiscordRoleAssignmentResult removal =
                        await discord.RemoveGuildMemberRoleAsync(discordUserId, roleId).ConfigureAwait(false);
                    DiscordRoleOperationStatus status = removal.IsSuccess
                        ? DiscordRoleOperationStatus.Removed
                        : !removal.IsGuildMember
                            ? DiscordRoleOperationStatus.AlreadyAbsent
                            : DiscordRoleOperationStatus.Failed;
                    operation = Operation(
                        roleId,
                        roleLabels,
                        DiscordRoleOperationAction.Remove,
                        status,
                        removal.IsSuccess || !removal.IsGuildMember ? null : removal.Error);
                }
                catch (Exception exception)
                {
                    operation = Operation(
                        roleId,
                        roleLabels,
                        DiscordRoleOperationAction.Remove,
                        DiscordRoleOperationStatus.Failed,
                        exception.Message);
                }

                operations.Add(operation);
                LogOperation(discordUserId, operation);
            }

            return new DiscordRoleReconciliationReport { Operations = operations.ToArray() };
        }

        private static DiscordRoleReconciliationReport CreateAbsentMemberReport(
            IEnumerable<ulong> desiredRoleIds,
            IEnumerable<ulong> authoritativeRoleIds,
            IReadOnlyDictionary<ulong, string> roleLabels)
        {
            HashSet<ulong> desired = new HashSet<ulong>(desiredRoleIds ?? Array.Empty<ulong>());
            return new DiscordRoleReconciliationReport
            {
                Operations = (authoritativeRoleIds ?? Array.Empty<ulong>())
                    .Select(roleId => Operation(
                        roleId,
                        roleLabels,
                        DiscordRoleOperationAction.Remove,
                        desired.Contains(roleId)
                            ? DiscordRoleOperationStatus.PreservedByDesiredRole
                            : DiscordRoleOperationStatus.AlreadyAbsent))
                    .ToArray(),
            };
        }

        private static DiscordRoleOperationResult Operation(
            ulong roleId,
            IReadOnlyDictionary<ulong, string> roleLabels,
            DiscordRoleOperationAction action,
            DiscordRoleOperationStatus status,
            string error = null) =>
            new DiscordRoleOperationResult
            {
                RoleId = roleId,
                RoleLabel = RoleLabel(roleLabels, roleId),
                Action = action,
                Status = status,
                Error = error,
            };

        private static DiscordRoleReconciliationReport CanceledReport(
            IEnumerable<DiscordRoleOperationResult> completedOperations) =>
            new DiscordRoleReconciliationReport
            {
                IsCanceled = true,
                Operations = (completedOperations ?? Array.Empty<DiscordRoleOperationResult>()).ToArray(),
            };

        private static void LogOperation(ulong discordUserId, DiscordRoleOperationResult operation)
        {
            if (operation.Status == DiscordRoleOperationStatus.Failed)
            {
                Log.Error(
                    $"[DiscordRoles] Could not {operation.Action.ToString().ToLowerInvariant()} managed role " +
                    $"{operation.RoleId} ('{operation.RoleLabel}') for user {discordUserId}: " +
                    $"{operation.Error ?? "unknown error"}");
                return;
            }

            if (operation.Status == DiscordRoleOperationStatus.Added ||
                operation.Status == DiscordRoleOperationStatus.Removed)
            {
                Log.Info(
                    $"[DiscordRoles] {operation.Status} managed role {operation.RoleId} " +
                    $"('{operation.RoleLabel}') for user {discordUserId}.");
            }
        }

        private static List<DiscordRoleGroupMapping> ResolveMappings(
            IEnumerable<string> groups,
            IEnumerable<DiscordRoleGroupMapping> mappings,
            ulong discordUserId,
            bool reportMissing)
        {
            List<DiscordRoleGroupMapping> result = new List<DiscordRoleGroupMapping>();
            foreach (string groupName in groups)
            {
                DiscordRoleGroupMapping mapping = mappings.FirstOrDefault(candidate =>
                    string.Equals(candidate.RemoteAdminGroup?.Trim(), groupName?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                {
                    if (reportMissing)
                    {
                        Log.Error(
                            $"[DiscordRoles] Managed privilege '{groupName}' for Discord user {discordUserId} " +
                            "has no valid entry in discord.role_groups.");
                    }

                    continue;
                }

                if (result.All(existing => existing.DiscordRoleId != mapping.DiscordRoleId))
                    result.Add(mapping);
            }

            return result;
        }

        private static DiscordRoleSynchronizationResult Canceled(IEnumerable<ulong> effectiveRoleIds) =>
            new DiscordRoleSynchronizationResult
            {
                IsCanceled = true,
                EffectiveRoleIds = (effectiveRoleIds ?? Array.Empty<ulong>()).ToArray(),
                ReconciliationTask = Task.FromResult(new DiscordRoleReconciliationReport
                {
                    IsCanceled = true,
                }),
            };

        private static string RoleLabel(
            IReadOnlyDictionary<ulong, string> roleLabels,
            ulong roleId) =>
            roleLabels != null && roleLabels.TryGetValue(roleId, out string label)
                ? label
                : "managed";
    }
}
