using System;
using System.Text.RegularExpressions;

namespace OWSShared
{
    /// <summary>
    /// Makes a client-supplied string safe to pass as a log argument. Line breaks and other control characters become
    /// '?', so the value can't start a forged entry in a plain-text sink, and long values are cut short. Use it on the
    /// log argument only and keep the original value for everything else.
    /// </summary>
    public static class LogSanitizer
    {
        public const int DefaultMaxLength = 64;

        // Cc covers CR, LF, tab, NEL (U+0085) and terminal escapes; Zl and Zp are the Unicode line and paragraph separators.
        private static readonly Regex Unsafe = new Regex(@"[\p{Cc}\p{Zl}\p{Zp}]", RegexOptions.CultureInvariant);

        public static string Sanitize(string value, int maxLength = DefaultMaxLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

            if (value == null)
            {
                return null;
            }

            string truncated = "";
            if (value.Length > maxLength)
            {
                // Don't leave half of a surrogate pair at the cut.
                int keep = maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
                truncated = $"...({value.Length} chars)";
                value = value.Substring(0, keep);
            }

            return Unsafe.Replace(value, "?") + truncated;
        }
    }
}
