namespace SmokyPluginV2.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Exiled.API.Features;

    using MEC;

    internal sealed class GeneralBroadcastHandler
    {
        private CoroutineHandle loop;
        private bool isRegistered;

        public void Register()
        {
            if (isRegistered)
                return;

            isRegistered = true;
            loop = Timing.RunCoroutine(BroadcastLoop());
        }

        public void Unregister()
        {
            if (!isRegistered)
                return;

            isRegistered = false;
            Timing.KillCoroutines(loop);
        }

        private IEnumerator<float> BroadcastLoop()
        {
            while (isRegistered)
            {
                GeneralBroadcastSettings settings = Plugin.Instance?.Config?.GeneralBroadcast;
                if (settings?.IsEnabled != true || string.IsNullOrWhiteSpace(settings.Text))
                {
                    yield return Timing.WaitForSeconds(1f);
                    continue;
                }

                float interval = settings.IntervalSeconds;
                if (float.IsNaN(interval) || float.IsInfinity(interval))
                    interval = 300f;
                interval = Math.Max(1f, interval);
                yield return Timing.WaitForSeconds(interval);

                settings = Plugin.Instance?.Config?.GeneralBroadcast;
                if (!isRegistered || settings?.IsEnabled != true || string.IsNullOrWhiteSpace(settings.Text))
                    continue;

                ushort duration = settings.DurationSeconds == 0 ? (ushort)1 : settings.DurationSeconds;
                string text = settings.Text;
                foreach (Player player in Player.List.Where(player => player != null && player.IsConnected && !player.IsHost).ToList())
                {
                    try
                    {
                        player.Broadcast(duration, text, shouldClearPrevious: false);
                    }
                    catch (Exception exception)
                    {
                        Log.Debug($"[General Broadcast] Could not show the broadcast to {player.Nickname}: {exception.Message}");
                    }
                }
            }
        }
    }
}
