using System;
using System.Collections.Generic;

namespace ValheimVillages.Testing
{
    /// <summary>
    /// A single assertion failure with expected/actual values.
    /// </summary>
    public class AssertionEntry
    {
        public string Message;
        public string Expected;
        public string Actual;
    }

    /// <summary>
    /// Exception thrown by ModAssert when an assertion fails.
    /// Captures all assertions (including soft-assert batches) for JSON output.
    /// </summary>
    public class ModAssertException : Exception
    {
        public List<AssertionEntry> Assertions { get; }

        public ModAssertException(string message) : base(message)
        {
            Assertions = new List<AssertionEntry>
            {
                new AssertionEntry { Message = message }
            };
        }

        public ModAssertException(string message, string expected, string actual) : base(message)
        {
            Assertions = new List<AssertionEntry>
            {
                new AssertionEntry { Message = message, Expected = expected, Actual = actual }
            };
        }

        public ModAssertException(List<AssertionEntry> assertions)
            : base(assertions.Count > 0 ? assertions[0].Message : "Assertion failed")
        {
            Assertions = assertions;
        }
    }
}
