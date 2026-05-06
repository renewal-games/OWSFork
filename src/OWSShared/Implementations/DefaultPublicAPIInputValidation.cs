using OWSShared.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OWSShared.Implementations
{
    public class DefaultPublicAPIInputValidation : IPublicAPIInputValidation
    {
        public string ValidateCharacterName(string charName)
        {
            //Test for empty Character Names or Character Names that are shorter than the minimum Character name Length
            string normalizedCharacterName = charName?.Trim();
            if (String.IsNullOrEmpty(normalizedCharacterName) || normalizedCharacterName.Length < 4)
            {
                return "Please enter a valid Character Name that is at least 4 characters in length.";
            }

            // Test for character names that use characters other than letters, numbers, spaces, or underscores.
            Regex regex = new Regex(@"^[A-Za-z0-9_ ]+$");
            if (!regex.IsMatch(normalizedCharacterName))
            {
                return "Please enter a Character Name that only contains letters, numbers, spaces, and underscores.";
            }

            return "";
        }

        public string ValidateEmail(string email)
        {
            throw new NotImplementedException();
        }

        public string ValidateFirstName(string firstName)
        {
            throw new NotImplementedException();
        }

        public string ValidateLastName(string lastName)
        {
            throw new NotImplementedException();
        }

        public string ValidatePassword(string password)
        {
            throw new NotImplementedException();
        }
    }
}
