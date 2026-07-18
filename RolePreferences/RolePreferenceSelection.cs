namespace SmokyPluginV2.RolePreferences
{
    internal enum RolePreferenceCategory
    {
        None,
        Scp,
        Scientist,
        ClassD,
        FacilityGuard,
    }

    internal sealed class RolePreferenceSelection
    {
        public RolePreferenceCategory Category { get; set; }

    }

    internal sealed class RoleSlotForecast
    {
        public int ScpSlots { get; set; }

        public int ScientistSlots { get; set; }

        public int ClassDSlots { get; set; }

        public int FacilityGuardSlots { get; set; }

        public int GetSlots(RolePreferenceCategory category)
        {
            switch (category)
            {
                case RolePreferenceCategory.Scp: return ScpSlots;
                case RolePreferenceCategory.Scientist: return ScientistSlots;
                case RolePreferenceCategory.ClassD: return ClassDSlots;
                case RolePreferenceCategory.FacilityGuard: return FacilityGuardSlots;
                default: return 0;
            }
        }
    }
}
