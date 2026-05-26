using System;
using System.Collections.Generic;

namespace ValheimVillages
{
    internal static partial class DebugLog
    {
        private static readonly Dictionary<string, ThrottleState> _throttle = new();

        private static readonly TimeSpan DefaultThrottleWindow = TimeSpan.FromSeconds(10);

        /// <summary>
        ///     Like Event() but deduplicates by `key`. First call within a window
        ///     emits immediately; subsequent calls with the same key are suppressed.
        ///     The next emit after the window includes `suppressed=N` so no signal
        ///     is lost — only the volume is.
        /// </summary>
        public static void Throttled(string key, string component, string eventName,
            params (string key, object val)[] kv)
        {
            ThrottledWindow(key, DefaultThrottleWindow, component, eventName, kv);
        }

        public static void ThrottledWindow(string key, TimeSpan window,
            string component, string eventName,
            params (string key, object val)[] kv)
        {
            if (string.IsNullOrEmpty(key))
            {
                Event(component, eventName, kv);
                return;
            }

            ThrottleState state;
            lock (_throttle)
            {
                if (!_throttle.TryGetValue(key, out state))
                {
                    state = new ThrottleState();
                    _throttle[key] = state;
                }
            }

            lock (state)
            {
                var now = Elapsed();
                var firstEver = !state.EverEmitted;
                var windowElapsed = now - state.LastEmitted >= window;

                if (firstEver || windowElapsed)
                {
                    if (state.Suppressed > 0)
                    {
                        var withSuppress = new (string, object)[kv.Length + 2];
                        Array.Copy(kv, withSuppress, kv.Length);
                        withSuppress[kv.Length] = ("suppressed", state.Suppressed);
                        withSuppress[kv.Length + 1] = ("window_s", window.TotalSeconds);
                        Event(component, eventName, withSuppress);
                    }
                    else
                    {
                        Event(component, eventName, kv);
                    }

                    state.LastEmitted = now;
                    state.EverEmitted = true;
                    state.Suppressed = 0;
                }
                else
                {
                    state.Suppressed++;
                }
            }
        }

        private sealed class ThrottleState
        {
            public bool EverEmitted;
            public TimeSpan LastEmitted;
            public int Suppressed;
        }
    }
}