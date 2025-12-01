using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FileMaster.Utils
{
    public class Normalizer
    {
        // Nomalize text: Ignore quote, dash, space, ...
        public static string NormalizeText(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            return input
                .Replace("‘", "'")
                .Replace("’", "'")
                .Replace("“", "\"")
                .Replace("”", "\"")
                .Replace("–", "-")
                .Replace("—", "-")
                .Replace("−", "-")
                .Replace("…", "...")
                .Replace("\u00A0", " ") // non-breaking space
                .Replace("\u2000", " ")
                .Replace("\u2001", " ")
                .Replace("\u2002", " ")
                .Replace("\u2003", " ")
                .Replace("\u2004", " ")
                .Replace("\u2005", " ")
                .Replace("\u2006", " ")
                .Replace("\u2007", " ")
                .Replace("\u2008", " ")
                .Replace("\u2009", " ")
                .Replace("\u200A", " ")
                .Trim();
        }

        // Normalize JSON string from MongoDB shell
        public static string NormalizeMongoShellJson(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return raw;

            string result = raw;

            // ISODate('...') → "..."
            result = Regex.Replace(result, @"ISODate\((['""])(.*?)\1\)", "\"$2\"");

            // ObjectId('...') → "..."
            result = Regex.Replace(result, @"ObjectId\((['""])(.*?)\1\)", "\"$2\"");

            // NumberInt(123) → 123
            result = Regex.Replace(result, @"NumberInt\((\d+)\)", "$1");

            // NumberLong(1234567890123) → 1234567890123
            result = Regex.Replace(result, @"NumberLong\((\d+)\)", "$1");

            // BinData(...) → "BinData(...)"
            result = Regex.Replace(result, @"BinData\(([^)]*)\)", "\"BinData($1)\"");

            // Replace '' with ""
            result = Regex.Replace(result, @"([{\[,]\s*)'([^']*?)'\s*(:)", "$1\"$2\"$3"); // keys
            result = Regex.Replace(result, @"(:\s*)'([^']*?)'(\s*[,}])", "$1\"$2\"$3");   // string values

            return result;
        }

        // Normalize cell string
        public static string NormalizeCellDataString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            // remove " and ' at the beginning and end of the string
            input = input.Trim('\"', '\'');
            return input;
        }
    }
}
