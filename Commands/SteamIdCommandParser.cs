namespace SmokyPluginV2.Commands
{
    using System;
    using System.Linq;
    using System.Text;

    using SmokyPluginV2.Database;

    internal static class SteamIdCommandParser
    {
        private const int SteamId64Length = 17;

        public static bool TryParse(string value, out string userId)
        {
            userId = null;
            string candidate = Clean(value);
            if (candidate.Length < SteamId64Length)
                return false;

            string steamId = candidate.Substring(0, SteamId64Length);
            if (!steamId.All(character => character >= '0' && character <= '9'))
                return false;

            string suffix = candidate.Substring(SteamId64Length);
            if (suffix.Length != 0 && !IsSteamProvider(suffix) && !IsDiscordMention(suffix))
                return false;

            userId = PostgreSqlService.ToExiledUserId(steamId);
            return true;
        }

        private static string Clean(string value)
        {
            string trimmed = (value ?? string.Empty).Trim().Trim('.', '`', '"', '\'');
            StringBuilder result = new StringBuilder(trimmed.Length);
            foreach (char character in trimmed)
            {
                if (character == '\\' || character == '\u200B' || character == '\u200C' ||
                    character == '\u200D' || character == '\u2060' || character == '\uFEFF')
                {
                    continue;
                }

                result.Append(character);
            }

            return result.ToString();
        }

        private static bool IsSteamProvider(string suffix) =>
            suffix.Equals("@steam", StringComparison.OrdinalIgnoreCase);

        private static bool IsDiscordMention(string suffix)
        {
            if (suffix.Length < 4 || suffix[0] != '<' || suffix[1] != '@' || suffix[suffix.Length - 1] != '>')
                return false;

            int index = 2;
            if (index < suffix.Length - 1 && (suffix[index] == '!' || suffix[index] == '&'))
                index++;

            if (index >= suffix.Length - 1)
                return false;

            for (; index < suffix.Length - 1; index++)
            {
                if (suffix[index] < '0' || suffix[index] > '9')
                    return false;
            }

            return true;
        }
    }
}
