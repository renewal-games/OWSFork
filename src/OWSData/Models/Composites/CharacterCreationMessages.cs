namespace OWSData.Models.Composites
{
    /// <summary>Error messages from character creation that are safe to show to the player.</summary>
    public static class CharacterCreationMessages
    {
        public const string UnknownDefaultCharacterSet = "Unknown default character set";

        /// <summary>Another character of the same customer already has this name, ignoring case and surrounding spaces.</summary>
        public const string CharacterNameTaken = "Character name already exists!";

        public const string CharacterCreationFailed = "Character could not be created";
    }
}
