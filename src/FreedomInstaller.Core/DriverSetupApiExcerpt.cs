// FreedomInstaller.Core.DriverSetupApiExcerpt
// Reads the tail of setupapi.dev.log and extracts a whitelisted excerpt of the
// most recent Driver Install section for a given INF path.
//
// No third-party dependencies. No state machine: chunking is done with a single
// Regex.Split on section headers, and each header block is inspected with
// anchored multiline regexes. Only whitelisted tokens are ever emitted; the raw
// log text, file paths, and user identifiers are never copied to the output.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FreedomInstaller.Core
{
    /// <summary>
    /// Safe, whitelisted excerpt of setupapi.dev.log for a single INF install.
    /// </summary>
    public static class DriverSetupApiExcerpt
    {
        private const int TailBytes = 1024 * 1024;
        private const string TsFormat = "yyyy/MM/dd HH:mm:ss.fff";
        private const string HeaderPrefix = ">>>  [Driver Install (DrvSetupInstallDriver) - ";

        // Chunk boundary: start of any ">>>  [" header line.
        private static readonly Regex s_chunkSplit =
            new Regex(@"(?m)(?=^>>>  \[)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex s_sectionStart = new Regex(
            @"^>>>  Section start (?<t>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex s_sectionEnd = new Regex(
            @"^<<<  Section end (?<t>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex s_exitStatus = new Regex(
            @"^<<<  \[Exit status:\s+(?<s>SUCCESS|FAILURE(?:\(0x[0-9A-Fa-f]{8}\))?|FAILURE)\s*\]\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex s_driverInf = new Regex(
            @"dvs:\s+Driver INF\s+-\s+(?<name>[^\\/\r\n]+?)\s+\((?<oem>oem\d+\.inf)\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex s_outcome = new Regex(
            @"dvs:\s+Outcome\s+-\s+(?<o>Already Imported|Imported)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex s_errorCode = new Regex(
            @"^\s*(?:!{1,3}\s*sig:\s*Error\s+0x(?<h>[0-9A-Fa-f]{8})\b|!{3,}\s*dvi:\s*Error\s+0x(?<h2>[0-9A-Fa-f]{8})\b)",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex s_noMatch = new Regex(
            @"Unable to find any matching devices",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads the tail of the standard setupapi.dev.log and returns a whitelisted
        /// excerpt for <paramref name="infPath"/>, or null if unavailable.
        /// </summary>
        public static string? ReadForInstall(string infPath, DateTimeOffset started, DateTimeOffset finished)
        {
            if (string.IsNullOrEmpty(infPath)) return null;
            if (!OperatingSystem.IsWindows()) return null;

            string root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(root)) return null;

            string logPath = Path.Combine(root, "INF", "setupapi.dev.log");
            string text;
            try
            {
                using FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                text = ReadTail(fs);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            using var sr = new StringReader(text);
            return Extract(sr, infPath, started, finished);
        }

        /// <summary>
        /// Extracts a whitelisted excerpt for <paramref name="infPath"/> from the
        /// supplied log text. Returns null when no in-window section matches.
        /// </summary>
        public static string? Extract(TextReader reader, string infPath, DateTimeOffset started, DateTimeOffset finished)
        {
            if (reader is null || string.IsNullOrEmpty(infPath)) return null;
            if (finished < started) return null;

            string body = reader.ReadToEnd();
            string target = HeaderPrefix + infPath + "]";

            string? sectionStart = null, sectionEnd = null, exitStatus = null;
            string? driverInf = null, outcome = null, noMatch = null;
            var codes = new List<string>(capacity: 4);

            // Iterate chunks from last to first, so the most recent matching
            // section wins even when several sections are present.
            string[] chunks = s_chunkSplit.Split(body);
            bool found = false;
            for (int ci = chunks.Length - 1; ci >= 0; ci--)
            {
                string chunk = chunks[ci];
                if (chunk.Length == 0) continue;

                int nl = chunk.IndexOf('\n');
                string firstLine = (nl < 0 ? chunk : chunk.Substring(0, nl)).TrimEnd('\r');
                if (!string.Equals(firstLine, target, StringComparison.OrdinalIgnoreCase)) continue;

                Match ms = s_sectionStart.Match(chunk);
                Match me = s_sectionEnd.Match(chunk);
                if (!ms.Success || !me.Success) continue;
                if (!TryParseTs(ms.Groups["t"].Value, out DateTime tsStart)) continue;
                if (!TryParseTs(me.Groups["t"].Value, out DateTime tsEnd)) continue;
                if (tsEnd < tsStart) continue;
                if (!InWindow(tsStart, started, finished)) continue;
                if (!InWindow(tsEnd, started, finished)) continue;

                // Exit-status line must appear after the Section end line.
                int endIdx = me.Index + me.Length;
                Match mex = s_exitStatus.Match(chunk, endIdx);
                if (!mex.Success) continue;

                sectionStart = ms.Groups["t"].Value;
                sectionEnd = me.Groups["t"].Value;
                exitStatus = mex.Groups["s"].Value;

                Match md = s_driverInf.Match(chunk);
                if (md.Success)
                {
                    string name = md.Groups["name"].Value;
                    driverInf = SanitizeBasename(name) + " (" + md.Groups["oem"].Value + ")";
                }

                Match mo = s_outcome.Match(chunk);
                if (mo.Success) outcome = mo.Groups["o"].Value;

                codes.Clear();
                foreach (Match mc in s_errorCode.Matches(chunk))
                {
                    if (codes.Count >= 4) break;
                    string hex = mc.Groups["h"].Success
                        ? mc.Groups["h"].Value
                        : mc.Groups["h2"].Value;
                    codes.Add("0x" + hex.ToUpperInvariant());
                }

                if (s_noMatch.IsMatch(chunk)) noMatch = "Unable to find any matching devices";
                found = true;
                break;
            }

            if (!found) return null;

            if (sectionStart is null) return null;

            var sb = new StringBuilder(256);
            sb.Append(">>>  [Driver Install (DrvSetupInstallDriver)]").Append('\n');
            sb.Append(">>>  Section start ").Append(sectionStart).Append('\n');

            if (driverInf is not null) sb.Append("dvs: Driver INF - ").Append(driverInf).Append('\n');
            if (outcome is not null) sb.Append("dvs: Outcome - ").Append(outcome).Append('\n');
            if (noMatch is not null) sb.Append(noMatch).Append('\n');

            foreach (string c in codes)
            {
                sb.Append("Error code: ").Append(c).Append('\n');
            }

            sb.Append("<<<  Section end ").Append(sectionEnd).Append('\n');
            sb.Append("<<<  [Exit status: ").Append(exitStatus).Append(']').Append('\n');
            return sb.ToString();
        }

        private static bool TryParseTs(string s, out DateTime value) =>
            DateTime.TryParseExact(s, TsFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value);

        private static bool InWindow(DateTime t, DateTimeOffset a, DateTimeOffset b)
        {
            DateTime lo = a.DateTime;
            DateTime hi = b.DateTime;
            // allow up to 1ms rounding slack on both ends
            return t >= lo.AddMilliseconds(-1) && t <= hi.AddMilliseconds(1);
        }

        private static string SanitizeBasename(string s)
        {
            int cut = s.IndexOfAny(new[] { '\\', '/', '\r', '\n', '\t' });
            if (cut >= 0) s = s.Substring(0, cut);
            return s.Length > 128 ? s.Substring(0, 128) : s;
        }

        private static string ReadTail(FileStream fs)
        {
            long length = fs.Length;
            long start = length > TailBytes ? length - TailBytes : 0;

            // Sniff BOM at the beginning of the file (not the tail) to pick an encoding.
            Encoding enc = SniffEncoding(fs);
            byte[] preamble = enc.GetPreamble();
            bool utf16 = enc is UnicodeEncoding;

            if (start > 0 && utf16)
            {
                start += start & 1; // align to code unit boundary
                if (start >= length) start = Math.Max(0, length - 2);
            }

            int toRead = checked((int)(length - start));
            if (toRead <= 0) return string.Empty;

            byte[] buffer = new byte[toRead];
            fs.Seek(start, SeekOrigin.Begin);
            int read = 0;
            while (read < toRead)
            {
                int n = fs.Read(buffer, read, toRead - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return string.Empty;

            // Skip a leading BOM that may have landed at the tail window.
            int off = (start == 0 && read >= preamble.Length && PreambleMatches(buffer, preamble))
                ? preamble.Length : 0;
            return enc.GetString(buffer, off, read - off);
        }

        private static bool PreambleMatches(byte[] buf, byte[] preamble)
        {
            for (int i = 0; i < preamble.Length; i++)
                if (buf[i] != preamble[i]) return false;
            return true;
        }

        private static Encoding SniffEncoding(FileStream fs)
        {
            byte[] head = new byte[4];
            long pos = fs.Position;
            try
            {
                fs.Seek(0, SeekOrigin.Begin);
                int n = fs.Read(head, 0, 4);
                if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                    return Encoding.UTF8;
                if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE)
                    return Encoding.Unicode;
                if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF)
                    return Encoding.BigEndianUnicode;
            }
            finally { fs.Seek(pos, SeekOrigin.Begin); }

            try
            {
                Encoding? ansi = CodePagesEncodingProvider.Instance.GetEncoding(0);
                if (ansi is not null) return ansi;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }

            return Encoding.UTF8;
        }
    }
}
