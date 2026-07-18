namespace SmokyPluginV2.Handlers
{
    using Exiled.API.Features;
    using Exiled.Events.EventArgs.Server;

    using ServerEvents = Exiled.Events.Handlers.Server;

    internal sealed class EndRoundFriendlyFireHandler
    {
        private bool isRegistered;

        public void Register()
        {
            if (isRegistered)
                return;

            ServerEvents.RoundEnded += OnRoundEnded;
            ServerEvents.RestartingRound += OnRestartingRound;
            ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
            ServerEvents.RoundStarted += OnRoundStarted;
            isRegistered = true;

            SetFriendlyFire(false, "plugin enabled");
        }

        public void Unregister()
        {
            if (!isRegistered)
                return;

            ServerEvents.RoundEnded -= OnRoundEnded;
            ServerEvents.RestartingRound -= OnRestartingRound;
            ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            ServerEvents.RoundStarted -= OnRoundStarted;
            isRegistered = false;

            SetFriendlyFire(false, "handler disabled");
        }

        private void OnRoundEnded(RoundEndedEventArgs _) => SetFriendlyFire(true, "round ended");

        private void OnRestartingRound() => SetFriendlyFire(false, "round restarting");

        private void OnWaitingForPlayers() => SetFriendlyFire(false, "waiting for players");

        private void OnRoundStarted() => SetFriendlyFire(false, "round started");

        private static void SetFriendlyFire(bool enabled, string reason)
        {
            if (Server.FriendlyFire == enabled)
                return;

            Server.FriendlyFire = enabled;
            Log.Info($"[End Round Friendly Fire] Friendly fire {(enabled ? "enabled" : "disabled")} ({reason}).");
        }
    }
}
