using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ChzzkDownloader
{
    internal static class TimeCode
    {
        private static readonly Regex Pattern = new Regex(
            @"^(?<hours>\d+):(?<minutes>[0-5]\d):(?<seconds>[0-5]\d)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static bool TryParse(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            Match match = Pattern.Match((value ?? "").Trim());
            if (!match.Success) return false;

            long hours;
            int minutes, seconds;
            if (!long.TryParse(match.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
                !int.TryParse(match.Groups["minutes"].Value, out minutes) ||
                !int.TryParse(match.Groups["seconds"].Value, out seconds) ||
                hours > 2400000)
                return false;

            time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }

        internal static string Format(TimeSpan value)
        {
            long hours = (long)Math.Floor(value.TotalHours);
            return hours.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   value.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   value.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
