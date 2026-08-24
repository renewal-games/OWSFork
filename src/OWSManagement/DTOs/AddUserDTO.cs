namespace OWSManagement.DTOs
{
    public class AddUserDTO
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string Password { get; set; }
        /// <summary>
        /// Player, Moderator, GameMaster or Admin. Defaults to Player when omitted.
        /// </summary>
        public string Role { get; set; }
    }
}
