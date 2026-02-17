using System.Collections.Generic;

namespace ValheimVillages.Core.Testing
{
    /// <summary>
    /// Assert helpers for in-game integration tests.
    /// Supports both immediate (throw-on-fail) and soft-assert (collect) modes.
    /// </summary>
    public static class ModAssert
    {
        [System.ThreadStatic]
        private static List<AssertionEntry> s_collected;

        /// <summary>Assert that a condition is true.</summary>
        public static void True(bool condition, string message)
        {
            if (condition) return;

            var entry = new AssertionEntry
            {
                Message = message,
                Expected = "true",
                Actual = "false"
            };

            if (s_collected != null)
            {
                s_collected.Add(entry);
                return;
            }

            throw new ModAssertException(message, "true", "false");
        }

        /// <summary>Assert that two values are equal.</summary>
        public static void Equal<T>(T expected, T actual, string message)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) return;

            string exp = expected?.ToString() ?? "null";
            string act = actual?.ToString() ?? "null";

            var entry = new AssertionEntry
            {
                Message = message,
                Expected = exp,
                Actual = act
            };

            if (s_collected != null)
            {
                s_collected.Add(entry);
                return;
            }

            throw new ModAssertException(message, exp, act);
        }

        /// <summary>Assert that an object is not null.</summary>
        public static void NotNull(object obj, string message)
        {
            if (obj != null) return;

            var entry = new AssertionEntry
            {
                Message = message,
                Expected = "not null",
                Actual = "null"
            };

            if (s_collected != null)
            {
                s_collected.Add(entry);
                return;
            }

            throw new ModAssertException(message, "not null", "null");
        }

        /// <summary>
        /// Enter soft-assert mode. Multiple assertions are collected
        /// and thrown as a batch when the returned scope is disposed.
        /// Usage: using (ModAssert.Collect()) { ... }
        /// </summary>
        public static CollectScope Collect()
        {
            return new CollectScope();
        }

        public class CollectScope : System.IDisposable
        {
            private readonly List<AssertionEntry> _previous;

            public CollectScope()
            {
                _previous = s_collected;
                s_collected = new List<AssertionEntry>();
            }

            public void Dispose()
            {
                var collected = s_collected;
                s_collected = _previous;

                if (collected != null && collected.Count > 0)
                {
                    throw new ModAssertException(collected);
                }
            }
        }
    }
}
