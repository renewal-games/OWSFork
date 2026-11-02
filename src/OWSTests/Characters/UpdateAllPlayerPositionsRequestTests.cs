using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using OWSCharacterPersistence.Requests.Characters;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Implementations;
using Xunit;

namespace OWSTests.Characters
{
    /// <summary>
    /// A non-finite or out-of-world coordinate never reaches the repository, and the rest of the batch still saves.
    /// The database side (ownership, Z) is covered by OWSTests.Database.PositionSaveTests.
    /// </summary>
    public class UpdateAllPlayerPositionsRequestTests
    {
        [Theory]
        [InlineData("NaN:0:0:0:0:0")]
        [InlineData("0:Infinity:0:0:0:0")]
        [InlineData("0:0:-Infinity:0:0:0")]
        [InlineData("1e13:0:0:0:0:0")]
        [InlineData("0:0:0:NaN:0:0")]
        [InlineData("0:0:0:0:0:Infinity")]
        public async Task BadEntryIsSkippedAndTheValidPlayerSaves(string badValues)
        {
            var saved = new List<string>();
            var request = new UpdateAllPlayerPositionsRequest
            {
                SerializedPlayerLocationData = $"Bad:{badValues}|Good:10.5:-20:30:0:90:0",
                MapName = ""
            };
            request.SetData(RecordingCharactersRepository.Create(saved), new HeaderCustomerGUID { CustomerGUID = Guid.NewGuid() });

            SuccessAndErrorMessage result = await request.Handle();

            Assert.True(result.Success);
            Assert.Equal(new[] { "Good:10.5:-20:30" }, saved);
            Assert.Contains("Bad", result.ErrorMessage);
        }

        [Fact]
        public async Task RefusedWriteIsReportedAsSkipped()
        {
            var request = new UpdateAllPlayerPositionsRequest { SerializedPlayerLocationData = "Stale:1:2:3:0:0:0", MapName = "" };
            request.SetData(RecordingCharactersRepository.Create(new List<string>(), written: false), new HeaderCustomerGUID());

            SuccessAndErrorMessage result = await request.Handle();

            Assert.Contains("Stale", result.ErrorMessage);
        }

        // Implements only UpdatePosition; anything else the request calls fails the test.
        public class RecordingCharactersRepository : DispatchProxy
        {
            private List<string> _saved;
            private bool _written;

            public static ICharactersRepository Create(List<string> saved, bool written = true)
            {
                ICharactersRepository proxy = Create<ICharactersRepository, RecordingCharactersRepository>();
                var recorder = (RecordingCharactersRepository)(object)proxy;
                recorder._saved = saved;
                recorder._written = written;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                Assert.Equal(nameof(ICharactersRepository.UpdatePosition), targetMethod.Name);
                _saved.Add(string.Create(CultureInfo.InvariantCulture, $"{args[1]}:{args[3]}:{args[4]}:{args[5]}"));
                return Task.FromResult(_written);
            }
        }
    }
}
