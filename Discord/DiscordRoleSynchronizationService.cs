namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    using Exiled.API.Features;

    internal sealed class DiscordRoleSynchronizationResult
    {
        public bool IsSuccess { get; set; }

        public bool IsGuildMember { get; set; }

        public bool IsCanceled { get; set; }

        public IReadOnlyCollection<ulong> EffectiveRoleIds { get; set; } = Array.Empty<ulong>();

        public Task ReconciliationTask { get; set; }

        public string Error { get; set; }
    }

    internal sealed class DiscordRoleSynchronizationService
    {
        private readonly DiscordLogService discord;

        public DiscordRoleSynchronizationService(DiscordLogService discord)
        {
            this.discord = discord ?? throw new ArgumentNullException(nameof(discord));
        }

        public async Task<DiscordRoleSynchronizationResult> SynchronizeAsync(
            ulong discordUserId,
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

            DiscordGuildMemberResult member =
                await discord.GetGuildMemberResultAsync(discordUserId).ConfigureAwait(false);
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
                };
            }

            HashSet<ulong> currentRoleIds = new HashSet<ulong>(member.RoleIds ?? Array.Empty<ulong>());
            HashSet<ulong> desiredRoleIds = new HashSet<ulong>(
                desiredMappings.Select(mapping => mapping.DiscordRoleId));
            HashSet<ulong> authoritativeRoleIds = new HashSet<ulong>(
                authoritativeMappings.Select(mapping => mapping.DiscordRoleId));
            Dictionary<ulong, string> roleLabels = validMappings
                .GroupBy(mapping => mapping.DiscordRoleId)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().RemoteAdminGroup);
            if (linkedDiscordRoleId != 0)
            {
                authoritativeRoleIds.Add(linkedDiscordRoleId);
                roleLabels[linkedDiscordRoleId] = "steam-link";
                if (isSteamLinked)
                    desiredRoleIds.Add(linkedDiscordRoleId);
            }

            HashSet<ulong> effectiveRoleIds = new HashSet<ulong>(currentRoleIds.Where(roleId =>
                !authoritativeRoleIds.Contains(roleId) || desiredRoleIds.Contains(roleId)));
            effectiveRoleIds.UnionWith(desiredRoleIds);

            if (shouldContinue?.Invoke() == false)
                return Canceled(effectiveRoleIds);

            ulong[] roleIdsToAdd = desiredRoleIds
                .Where(roleId => !currentRoleIds.Contains(roleId))
                .ToArray();
            ulong[] roleIdsToRemove = authoritativeRoleIds
                .Where(roleId => currentRoleIds.Contains(roleId) && !desiredRoleIds.Contains(roleId))
                .ToArray();
            Task reconciliationTask = ReconcileRolesAsync(
                discordUserId,
                roleIdsToAdd,
                roleIdsToRemove,
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

        private async Task ReconcileRolesAsync(
            ulong discordUserId,
            IEnumerable<ulong> roleIdsToAdd,
            IEnumerable<ulong> roleIdsToRemove,
            IReadOnlyDictionary<ulong, string> roleLabels,
            Func<bool> shouldContinue)
        {
            try
            {
                foreach (ulong roleId in roleIdsToAdd ?? Array.Empty<ulong>())
                {
                    if (shouldContinue?.Invoke() == false)
                        return;

                    DiscordRoleAssignmentResult assignment =
                        await discord.AddGuildMemberRoleAsync(discordUserId, roleId).ConfigureAwait(false);
                    string roleLabel = RoleLabel(roleLabels, roleId);
                    if (!assignment.IsSuccess)
                    {
                        Log.Error(
                            $"[DiscordRoles] Could not assign managed role {roleId} " +
                            $"('{roleLabel}') to user {discordUserId}: " +
                            $"{assignment.Error ?? "unknown error"}");
                    }
                    else
                    {
                        Log.Info(
                            $"[DiscordRoles] Assigned managed role {roleId} " +
                            $"('{roleLabel}') to user {discordUserId}.");
                    }
                }

                foreach (ulong roleId in roleIdsToRemove ?? Array.Empty<ulong>())
                {
                    if (shouldContinue?.Invoke() == false)
                        return;

                    DiscordRoleAssignmentResult removal =
                        await discord.RemoveGuildMemberRoleAsync(discordUserId, roleId).ConfigureAwait(false);
                    string roleLabel = RoleLabel(roleLabels, roleId);
                    if (!removal.IsSuccess)
                    {
                        Log.Error(
                            $"[DiscordRoles] Could not remove obsolete managed role {roleId} " +
                            $"('{roleLabel}') from user {discordUserId}: " +
                            $"{removal.Error ?? "unknown error"}");
                    }
                    else
                    {
                        Log.Info(
                            $"[DiscordRoles] Removed obsolete managed role {roleId} " +
                            $"('{roleLabel}') from user {discordUserId}.");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"[DiscordRoles] Background role reconciliation failed for user " +
                    $"{discordUserId}: {exception}");
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
            };

        private static string RoleLabel(
            IReadOnlyDictionary<ulong, string> roleLabels,
            ulong roleId) =>
            roleLabels != null && roleLabels.TryGetValue(roleId, out string label)
                ? label
                : "managed";
    }
}
