using System;
using System.Net;
using System.Text.RegularExpressions;

namespace RDPManager
{
    public static class RemoteErrorFormatter
    {
        public static string Format(RemoteCommandResult result)
        {
            if (result == null)
                return "远程命令失败";
            return Format(result.Error, result.Output);
        }

        public static string Format(string error, string output)
        {
            string externalError = ExtractExternalText(error);
            if (!string.IsNullOrWhiteSpace(externalError))
                return Clean(externalError);

            string extracted = ExtractClixml(error);
            if (!string.IsNullOrWhiteSpace(extracted))
                return Clean(extracted);

            if (!IsOnlyClixml(error))
            {
                string cleanError = Clean(error);
                if (!string.IsNullOrWhiteSpace(cleanError))
                    return cleanError;
            }

            string cleanOutput = Clean(output);
            if (!string.IsNullOrWhiteSpace(cleanOutput))
                return cleanOutput;

            return "远程命令失败";
        }

        private static string ExtractClixml(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf("CLIXML", StringComparison.OrdinalIgnoreCase) < 0)
                return "";

            MatchCollection matches = Regex.Matches(
                value,
                "<S\\s+S=\"Error\">(?<text>.*?)</S>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count == 0)
                return "";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            foreach (Match match in matches)
            {
                string text = WebUtility.HtmlDecode(match.Groups["text"].Value);
                text = text.Replace("_x000D_", "").Replace("_x000A_", " ");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (builder.Length > 0)
                        builder.Append(" ");
                    builder.Append(text);
                }
            }
            return builder.ToString();
        }

        private static string ExtractExternalText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            int marker = value.IndexOf("#< CLIXML", StringComparison.OrdinalIgnoreCase);
            if (marker <= 0)
                return "";
            return value.Substring(0, marker).Trim();
        }

        private static bool IsOnlyClixml(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value.IndexOf("CLIXML", StringComparison.OrdinalIgnoreCase) >= 0 &&
                ExtractClixml(value).Length == 0;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            string result = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (result.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase))
                return "";
            return result;
        }
    }
}
