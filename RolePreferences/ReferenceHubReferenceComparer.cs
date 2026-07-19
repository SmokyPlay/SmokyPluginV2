namespace SmokyPluginV2.RolePreferences
{
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;

    internal sealed class ReferenceHubReferenceComparer : IEqualityComparer<ReferenceHub>
    {
        public static readonly ReferenceHubReferenceComparer Instance = new ReferenceHubReferenceComparer();

        private ReferenceHubReferenceComparer()
        {
        }

        public bool Equals(ReferenceHub x, ReferenceHub y) => ReferenceEquals(x, y);

        public int GetHashCode(ReferenceHub obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
