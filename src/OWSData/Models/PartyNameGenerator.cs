using System;
using System.Security.Cryptography;

namespace OWSData.Models
{
    internal static class PartyNameGenerator
    {
        public const int MaxPartyNameLength = 64;

        private static readonly string[] Adjectives =
        {
            "Amber", "Ancient", "Arcane", "Astral", "Bold", "Bright", "Crimson", "Dusky",
            "Emerald", "Fabled", "Golden", "Hidden", "Iron", "Ivory", "Lucky", "Lunar",
            "Mystic", "Noble", "Radiant", "Runic", "Silver", "Stalwart", "Storm", "Swift"
        };

        private static readonly string[] Nouns =
        {
            "Aegis", "Anvil", "Banner", "Beacon", "Blade", "Crown", "Ember", "Forge",
            "Harbor", "Lantern", "Moon", "Oath", "Path", "Relic", "Rune", "Saga",
            "Shield", "Sigil", "Spear", "Star", "Thorn", "Tower", "Vale", "Vanguard"
        };

        public static string Normalize(string partyName)
        {
            return string.IsNullOrWhiteSpace(partyName) ? string.Empty : partyName.Trim();
        }

        public static bool IsValid(string partyName)
        {
            string normalizedPartyName = Normalize(partyName);
            return normalizedPartyName.Length > 0 && normalizedPartyName.Length <= MaxPartyNameLength;
        }

        public static string CreateCandidate(int attempt)
        {
            string adjective = Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)];
            string noun = Nouns[RandomNumberGenerator.GetInt32(Nouns.Length)];
            string suffix = attempt < 8 ? string.Empty : RandomNumberGenerator.GetInt32(2, 1000).ToString();

            return $"{adjective}{noun}{suffix}";
        }

        public static string CreateFallback()
        {
            return $"Party{Guid.NewGuid():N}".Substring(0, 13);
        }
    }
}
