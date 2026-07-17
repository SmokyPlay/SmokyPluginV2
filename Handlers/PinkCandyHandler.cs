namespace SmokyPluginV2.Handlers
{
    using System;

    using Exiled.API.Features;
    using Exiled.Events.EventArgs.Scp330;

    using InventorySystem.Items.Usables.Scp330;

    using Scp330Events = Exiled.Events.Handlers.Scp330;

    /// <summary>
    /// Restores the pink candy as a configurable SCP-330 bowl outcome.
    /// </summary>
    internal sealed class PinkCandyHandler
    {
        private readonly Random random = new Random();
        private bool isRegistered;

        public void Register()
        {
            if (isRegistered)
                return;

            Scp330Events.InteractingScp330 += OnInteractingScp330;
            isRegistered = true;
        }

        public void Unregister()
        {
            if (!isRegistered)
                return;

            Scp330Events.InteractingScp330 -= OnInteractingScp330;
            isRegistered = false;
        }

        private void OnInteractingScp330(InteractingScp330EventArgs ev)
        {
            PinkCandySettings settings = Plugin.Instance?.Config?.PinkCandy;
            if (settings is null || !settings.IsEnabled || ev is null || !ev.IsAllowed)
                return;

            double chance = settings.ChancePercent;
            if (double.IsNaN(chance) || chance <= 0)
                return;

            chance = Math.Min(100, chance);
            if ((random.NextDouble() * 100) >= chance)
                return;

            ev.Candy = CandyKindID.Pink;
            Log.Debug($"[Pink Candy] {ev.Player?.Nickname ?? "Unknown player"} received a pink candy from SCP-330.");
        }
    }
}
