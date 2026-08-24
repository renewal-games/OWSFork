namespace OWSManagement.DTOs
{
    public class SetCharacterAdminFlagsDTO
    {
        public int CharacterID { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsModerator { get; set; }
    }
}
