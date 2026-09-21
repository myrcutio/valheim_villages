using System;
using System.Text;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Emit a dev-command report without wedging a dedicated server.
    ///
    ///     <para>A headless server's stdout is a pipe, and a pipe holds 64 KiB. BepInEx logs
    ///     on the calling thread, so a single oversized write blocks in <c>anon_pipe_write</c>
    ///     until something drains it — on the MAIN thread, which means the server stops
    ///     answering anyone. Measured twice: a 70 KB ZDO audit took the server down, and a
    ///     <c>vv_rerank_dump</c> of two villages (250 lines of per-candidate scoring) left it
    ///     silent and unresponsive for nine hours until it was restarted.</para>
    ///
    ///     <para>So: reports are capped and written in small pieces. A command whose full
    ///     detail cannot fit should AGGREGATE rather than lean on the cap — a truncated report
    ///     answers nothing — and the cap is the backstop that keeps a mistake from costing a
    ///     server.</para>
    /// </summary>
    public static class ConsoleReport
    {
        /// <summary>Bytes per write. Well under the pipe buffer, so no single write can block.</summary>
        private const int ChunkChars = 3500;

        /// <summary>Hard ceiling for one report. Past this it is a data dump, not a report.</summary>
        private const int MaxChars = 16000;

        public static void Emit(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var truncated = false;
            if (text.Length > MaxChars)
            {
                // Cut on a line boundary so the last line printed is a whole one.
                var cut = text.LastIndexOf('\n', Math.Min(MaxChars, text.Length - 1));
                text = text.Substring(0, cut > 0 ? cut : MaxChars);
                truncated = true;
            }

            foreach (var chunk in Chunks(text))
            {
                global::Console.instance?.Print(chunk);
                Plugin.Log?.LogInfo(chunk);
            }

            if (!truncated) return;

            const string note = "[report truncated — narrow the query or aggregate the output]";
            global::Console.instance?.Print(note);
            Plugin.Log?.LogWarning(note);
        }

        private static System.Collections.Generic.IEnumerable<string> Chunks(string text)
        {
            var start = 0;
            while (start < text.Length)
            {
                var length = Math.Min(ChunkChars, text.Length - start);

                // Prefer to break at the last newline in the window, so lines stay intact.
                if (start + length < text.Length)
                {
                    var newline = text.LastIndexOf('\n', start + length - 1, length);
                    if (newline > start) length = newline - start + 1;
                }

                yield return text.Substring(start, length);
                start += length;
            }
        }

        /// <summary>Convenience for the common "title + body" shape.</summary>
        public static void Emit(string title, StringBuilder body)
        {
            Emit(title + "\n" + body);
        }
    }
}
