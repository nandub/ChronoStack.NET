using System;
using System.Text.RegularExpressions;

namespace ChronoStack
{
    /// <summary>
    /// Provides automated redaction of sensitive PII/PHI data from exception messages.
    /// </summary>
    public static class PiiRedactor
    {
        // Compiled Regex for high performance during exception unwinding
        private static readonly Regex SsnRegex = new Regex(@"\b\d{3}[-]?\d{2}[-]?\d{4}\b", RegexOptions.Compiled);
        private static readonly Regex CardRegex = new Regex(@"\b(?:\d[ -]*?){13,16}\b", RegexOptions.Compiled);

        /// <summary>
        /// Masks common PII patterns with safe placeholder text.
        /// </summary>
        public static string Redact(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;
            
            var safeStr = SsnRegex.Replace(input, "***-**-****");
            safeStr = CardRegex.Replace(safeStr, "****-****-****-****");
            
            return safeStr;
        }
    }
}
