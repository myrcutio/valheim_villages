using System;
using System.Collections.Generic;

namespace ValheimVillages.Testing
{
    /// <summary>
    ///     A single assertion failure with expected/actual values.
    /// </summary>
    public class AssertionEntry
    {
        public string Actual;
        public string Expected;
        public string Message;
    }

    /// <summary>
    ///     Thrown by <see cref="ModAssert.Skip" /> when a test cannot meaningfully run
    ///     here: an unmet precondition, or a fixture recorded in a different world.
    ///
    ///     <para>A skip is NOT a pass. Every unmet precondition used to be a bare
    ///     <c>return</c>, which the runner counted as a PASS — so the HNA tests reported
    ///     green on a world with no region graph at all, and a green suite proved nothing.
    ///     A skip is its own outcome and carries its reason, so "passed" means the
    ///     assertions actually ran.</para>
    /// </summary>
    public class ModTestSkipException : Exception
    {
        public ModTestSkipException(string reason) : base(reason)
        {
        }
    }

    /// <summary>
    ///     Exception thrown by ModAssert when an assertion fails.
    ///     Captures all assertions (including soft-assert batches) for JSON output.
    /// </summary>
    public class ModAssertException : Exception
    {
        public ModAssertException(string message) : base(message)
        {
            Assertions = new List<AssertionEntry>
            {
                new() { Message = message },
            };
        }

        public ModAssertException(string message, string expected, string actual) : base(message)
        {
            Assertions = new List<AssertionEntry>
            {
                new() { Message = message, Expected = expected, Actual = actual },
            };
        }

        public ModAssertException(List<AssertionEntry> assertions)
            : base(assertions.Count > 0 ? assertions[0].Message : "Assertion failed")
        {
            Assertions = assertions;
        }

        public List<AssertionEntry> Assertions { get; }
    }
}