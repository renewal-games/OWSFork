using System;
using OWSShared;
using Xunit;

namespace OWSTests.Shared
{
    public class LogSanitizerTests
    {
        [Theory]
        [InlineData("Alice", "Alice")]
        [InlineData("Alice Smith_2", "Alice Smith_2")]
        [InlineData("Alice\r\nforged", "Alice??forged")]
        [InlineData("a\tb\u000Bc\u000Cd", "a?b?c?d")]
        [InlineData("\u001B[31mred", "?[31mred")]
        [InlineData("", "")]
        public void ReplacesLineBreaksAndControlCharacters(string value, string expected)
        {
            Assert.Equal(expected, LogSanitizer.Sanitize(value));
        }

        [Fact]
        public void ReplacesUnicodeLineBreaks()
        {
            // NEL, LINE SEPARATOR and PARAGRAPH SEPARATOR, built from their code points.
            string value = "a" + (char)0x85 + "b" + (char)0x2028 + "c" + (char)0x2029 + "d";

            Assert.Equal("a?b?c?d", LogSanitizer.Sanitize(value));
        }

        [Fact]
        public void NullStaysNull()
        {
            Assert.Null(LogSanitizer.Sanitize(null));
        }

        [Fact]
        public void KeepsValuesUpToTheLimit()
        {
            string value = new string('a', LogSanitizer.DefaultMaxLength);

            Assert.Equal(value, LogSanitizer.Sanitize(value));
        }

        [Fact]
        public void CutsLongValuesAndSaysHowLongTheyWere()
        {
            string value = new string('a', LogSanitizer.DefaultMaxLength) + "\r\nforged";

            Assert.Equal(new string('a', LogSanitizer.DefaultMaxLength) + $"...({value.Length} chars)", LogSanitizer.Sanitize(value));
        }

        [Fact]
        public void DoesNotSplitASurrogatePairAtTheCut()
        {
            string value = "abc\U0001F600def";

            Assert.Equal($"abc...({value.Length} chars)", LogSanitizer.Sanitize(value, maxLength: 4));
        }

        [Fact]
        public void RejectsANegativeLimit()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LogSanitizer.Sanitize("Alice", -1));
        }
    }
}
