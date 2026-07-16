namespace SmokyPluginV2.Handlers
{
    using Exiled.API.Features;
    using Exiled.Events.EventArgs.Player;

    /// <summary>
    /// Restarts an active round after the last player leaves the server.
    /// </summary>
    internal sealed class EmptyRoundHandler
    {
        /// <summary>
        /// Handles a player leaving the server.
        /// </summary>
        /// <param name="ev">The event arguments.</param>
        public void OnLeft(LeftEventArgs ev)
        {
            Config config = Plugin.Instance?.Config;

            if (config is null || !config.RestartEmptyRound || !Round.IsStarted)
                return;

            // EXILED raises Left before it removes the departing player from Player.List.
            if (Server.PlayerCount > 1)
                return;

            Log.Info($"The last player ({ev.Player.Nickname}) left the active round. Restarting immediately.");
            Round.Restart();
        }
    }
}
