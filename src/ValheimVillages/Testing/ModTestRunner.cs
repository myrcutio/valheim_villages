using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using ValheimVillages.Attributes;

namespace ValheimVillages.Testing
{
    /// <summary>
    ///     In-game integration test runner. Discovers [ModTest] methods,
    ///     executes them, and writes JSON results to vv_test_results.json.
    /// </summary>
    public static class ModTestRunner
    {
        /// <summary>Config toggle for auto-running tests on startup.</summary>
        public static bool AutoRunEnabled { get; set; } = true;

        private static string ResultsPath => Path.Combine(
            Paths.ConfigPath, "vv_test_results.json");

        /// <summary>
        ///     Discover and run all [ModTest] methods. Writes JSON results.
        /// </summary>
        public static void RunAll()
        {
            var assembly = typeof(Plugin).Assembly;
            var methods = DiscoverTests(assembly);

            Plugin.Log?.LogInfo($"[ModTestRunner] Running {methods.Count} integration tests...");

            int passed = 0, failed = 0, errored = 0, skipped = 0;
            var failures = new List<TestResultEntry>();
            var errors = new List<TestResultEntry>();

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<ModTestAttribute>();
                var testName = attr?.Name ?? $"{method.DeclaringType?.Name}.{method.Name}";

                try
                {
                    method.Invoke(null, null);
                    passed++;
                    Plugin.Log?.LogDebug($"[ModTestRunner] PASS: {testName}");
                }
                catch (TargetInvocationException tie) when (tie.InnerException is ModAssertException mae)
                {
                    failed++;
                    var entry = BuildResult(method, mae, testName, "FAIL");
                    failures.Add(entry);
                    Plugin.Log?.LogWarning($"[ModTestRunner] FAIL: {testName} — {mae.Message}");
                }
                catch (Exception ex)
                {
                    var actual = ex is TargetInvocationException tie2 ? tie2.InnerException ?? ex : ex;
                    errored++;
                    var entry = BuildResult(method, actual, testName, "ERROR");
                    errors.Add(entry);
                    Plugin.Log?.LogError($"[ModTestRunner] ERROR: {testName} — {actual.Message}");
                }
            }

            var result = failed == 0 && errored == 0 ? "PASS" : "FAIL";

            Plugin.Log?.LogInfo(
                $"[ModTestRunner] Results: {passed} passed, {failed} failed, " +
                $"{errored} errors, {skipped} skipped — {result}");

            WriteJsonResults(result, passed, failed, errored, skipped,
                failures, errors, methods.Count);
        }

        /// <summary>
        ///     Discover all [ModTest] methods sorted by Order, then alphabetically.
        /// </summary>
        private static List<MethodInfo> DiscoverTests(Assembly assembly)
        {
            var methods = new List<(MethodInfo method, int order, string name)>();

            foreach (var type in assembly.GetTypes())
            foreach (var method in type.GetMethods(
                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attr = method.GetCustomAttribute<ModTestAttribute>();
                if (attr == null) continue;

                if (method.GetParameters().Length > 0)
                {
                    Plugin.Log?.LogWarning(
                        $"[ModTestRunner] Skipping {type.Name}.{method.Name}: " +
                        "must be parameterless static");
                    continue;
                }

                var name = attr.Name ?? $"{type.Name}.{method.Name}";
                methods.Add((method, attr.Order, name));
            }

            return methods
                .OrderBy(m => m.order)
                .ThenBy(m => m.name)
                .Select(m => m.method)
                .ToList();
        }

        /// <summary>
        ///     Build a TestResultEntry from an exception.
        /// </summary>
        private static TestResultEntry BuildResult(
            MethodInfo method, Exception ex, string testName, string state)
        {
            var entry = new TestResultEntry
            {
                Test = testName,
                State = state,
                Message = ex.Message,
                StackTrace = ex.StackTrace ?? "",
            };

            // Extract source location from stack trace
            if (ex.StackTrace != null)
            {
                var match = Regex.Match(ex.StackTrace, @"in (.+):line (\d+)");
                if (match.Success)
                {
                    entry.File = match.Groups[1].Value;
                    int.TryParse(match.Groups[2].Value, out entry.Line);
                }
            }

            entry.ClassName = method.DeclaringType?.FullName ?? "";

            // Capture assertions if available
            if (ex is ModAssertException mae)
                foreach (var a in mae.Assertions)
                    entry.Assertions.Add(new AssertionOutput
                    {
                        Message = a.Message,
                        Expected = a.Expected ?? "",
                        Actual = a.Actual ?? "",
                    });

            return entry;
        }

        /// <summary>
        ///     Write test results to JSON using StringBuilder (no library dependency).
        /// </summary>
        private static void WriteJsonResults(
            string result, int passed, int failed, int errored, int skipped,
            List<TestResultEntry> failures, List<TestResultEntry> errors,
            int totalTests)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"v\": 1,");
            sb.AppendLine($"  \"ts\": \"{DateTime.UtcNow:O}\",");
            sb.AppendLine($"  \"result\": \"{result}\",");
            sb.AppendLine("  \"summary\": {");
            sb.AppendLine($"    \"total\": {totalTests},");
            sb.AppendLine($"    \"passed\": {passed},");
            sb.AppendLine($"    \"failed\": {failed},");
            sb.AppendLine($"    \"errors\": {errored},");
            sb.AppendLine($"    \"skipped\": {skipped}");
            sb.AppendLine("  },");

            // Failures
            sb.AppendLine("  \"failures\": [");
            WriteEntries(sb, failures);
            sb.AppendLine("  ],");

            // Errors
            sb.AppendLine("  \"errors\": [");
            WriteEntries(sb, errors);
            sb.AppendLine("  ],");

            // Skipped (always empty for now)
            sb.AppendLine("  \"skipped\": []");
            sb.AppendLine("}");

            try
            {
                File.WriteAllText(ResultsPath, sb.ToString());
                Plugin.Log?.LogInfo($"[ModTestRunner] Results written to {ResultsPath}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[ModTestRunner] Failed to write results: {ex.Message}");
            }
        }

        private static void WriteEntries(StringBuilder sb, List<TestResultEntry> entries)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"test\": {JsonEscape(e.Test)},");
                sb.AppendLine($"      \"state\": {JsonEscape(e.State)},");
                sb.AppendLine("      \"location\": {");
                sb.AppendLine($"        \"file\": {JsonEscape(e.File ?? "")},");
                sb.AppendLine($"        \"line\": {e.Line},");
                sb.AppendLine($"        \"class\": {JsonEscape(e.ClassName ?? "")}");
                sb.AppendLine("      },");
                sb.AppendLine($"      \"message\": {JsonEscape(e.Message ?? "")},");

                // Assertions
                sb.AppendLine("      \"assertions\": [");
                for (var j = 0; j < e.Assertions.Count; j++)
                {
                    var a = e.Assertions[j];
                    sb.Append("        { ");
                    sb.Append($"\"message\": {JsonEscape(a.Message)}, ");
                    sb.Append($"\"expected\": {JsonEscape(a.Expected)}, ");
                    sb.Append($"\"actual\": {JsonEscape(a.Actual)}");
                    sb.Append(" }");
                    if (j < e.Assertions.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }

                sb.AppendLine("      ],");

                sb.AppendLine($"      \"stackTrace\": {JsonEscape(e.StackTrace ?? "")}");
                sb.Append("    }");
                if (i < entries.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }

        /// <summary>Console command to manually run all tests.</summary>
        [DevCommand("Run all [ModTest] integration tests", Name = "vv_test_run")]
        public static void RunTestsCommand()
        {
            RunAll();
        }

        private class TestResultEntry
        {
            public readonly List<AssertionOutput> Assertions = new();
            public string ClassName = "";
            public string File;
            public int Line;
            public string Message = "";
            public string StackTrace = "";
            public string State = "";
            public string Test = "";
        }

        private class AssertionOutput
        {
            public string Actual = "";
            public string Expected = "";
            public string Message = "";
        }
    }
}