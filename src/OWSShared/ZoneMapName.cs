using System.Text.RegularExpressions;

namespace OWSShared
{
    /// <summary>
    /// The map name the launcher puts first in a zone server's command line ("{map}?listen -server ..."). One flat
    /// Arguments string, so a space would add UE switches and a '?' would add URL options. Letters, digits, '_' and '/'
    /// cover every Maps row and /Game/ path in use.
    /// </summary>
    public static class ZoneMapName
    {
        public const int MaxLength = 128;
        private static readonly Regex Allowed = new Regex("^[A-Za-z0-9_/]+$", RegexOptions.CultureInvariant);

        public static bool IsValid(string mapName)
        {
            return !string.IsNullOrEmpty(mapName) && mapName.Length <= MaxLength && Allowed.IsMatch(mapName);
        }
    }
}
