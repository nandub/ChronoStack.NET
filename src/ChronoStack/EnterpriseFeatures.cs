using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ChronoStack
{
    /// <summary>
    /// A structured redaction policy allowing multiple custom rules to mask sensitive data.
    /// </summary>
    public sealed class RedactionPolicy
    {
        private readonly List<(Regex Pattern, string Replacement)> _rules = new List<(Regex, string)>();

        /// <summary>
        /// Adds a Regex pattern and its safe placeholder replacement.
        /// </summary>
        public RedactionPolicy AddRule(string regexPattern, string replacementText)
        {
            // Compiled for high performance on the hot path
            _rules.Add((new Regex(regexPattern, RegexOptions.Compiled), replacementText));
            return this;
        }

        /// <summary>
        /// Executes all configured rules against the input string.
        /// </summary>
        public string Redact(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;

            var safeStr = input;
            foreach (var rule in _rules)
            {
                safeStr = rule.Pattern.Replace(safeStr, rule.Replacement);
            }
            return safeStr;
        }

        /// <summary>
        /// Gets a pre-configured policy containing standard rules for SSNs, Credit Cards, and Emails.
        /// </summary>
        public static RedactionPolicy DefaultPiiPolicy()
        {
            return new RedactionPolicy()
                .AddRule(@"\b\d{3}[-]?\d{2}[-]?\d{4}\b", "***-**-****") // SSN
                .AddRule(@"\b(?:\d[ -]*?){13,16}\b", "****-****-****-****") // Credit Card
                .AddRule(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", "*@*.***"); // Email
        }
    }
}
