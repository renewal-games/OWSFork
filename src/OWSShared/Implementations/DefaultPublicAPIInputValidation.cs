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

        // Matches the Users table columns (setup.sql) so a bad value gets a clear message instead of a database error.
        private const int MaxEmailLength = 255;
        private const int MaxNameLength = 50;
        // bcrypt reads only the first 72 bytes of a password; Postgres AddUser refuses anything longer.
        private const int MaxPasswordBytes = 72;

        public string ValidateEmail(string email)
        {
            string normalizedEmail = email?.Trim();
            if (String.IsNullOrEmpty(normalizedEmail) || normalizedEmail.Length > MaxEmailLength)
            {
                return "Please enter a valid Email address.";
            }

            // One @, something before it, and a dotted domain after it, with no spaces.
            Regex regex = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
            if (!regex.IsMatch(normalizedEmail))
            {
                return "Please enter a valid Email address.";
            }

            return "";
        }

        public string ValidateFirstName(string firstName)
        {
            if (firstName != null && firstName.Length > MaxNameLength)
            {
                return $"Please enter a First Name that is {MaxNameLength} characters or fewer.";
            }

            return "";
        }

        public string ValidateLastName(string lastName)
        {
            if (lastName != null && lastName.Length > MaxNameLength)
            {
                return $"Please enter a Last Name that is {MaxNameLength} characters or fewer.";
            }

            return "";
        }

        public string ValidatePassword(string password)
        {
            if (String.IsNullOrEmpty(password))
            {
                return "Please enter a Password.";
            }

            if (Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes)
            {
                return $"Password must be {MaxPasswordBytes} bytes or fewer.";
            }

            return "";
        }
    }
}
