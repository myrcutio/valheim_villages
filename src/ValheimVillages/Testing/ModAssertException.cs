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