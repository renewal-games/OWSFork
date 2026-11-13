using OWSCharacterPersistence.Requests.Characters;
using OWSData.Repositories.Interfaces;
using OWSShared.Implementations;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace OWSTests.CharacterPersistence
{
    public class BatchPositionOwnerTokenTests
    {
        [Fact]
        public async Task BatchSavesClaimedCharacterOnlyWhenEntryCarriesItsOwnerToken()
        {
            Guid ownerToken = Guid.NewGuid();
            ICharacterOwnersRepository owners = DispatchProxy.Create<ICharacterOwnersRepository, OwnersStub>();
            ((OwnersStub)(object)owners).CurrentToken = ownerToken;
            ICharactersRepository characters = DispatchProxy.Create<ICharactersRepository, CharactersStub>();
            var saved = ((CharactersStub)(object)characters).SavedNames;

            var request = new UpdateAllPlayerPositionsRequest
            {
                SerializedPlayerLocationData = $"Claimed:1:2:3:4:5:6:{ownerToken}|MissingToken:7:8:9:10:11:12",
                MapName = "Zone"
            };
            request.SetData(characters, owners, new HeaderCustomerGUID { CustomerGUID = Guid.NewGuid() });

            var result = await request.Handle();

            Assert.True(result.Success);
            Assert.Equal(new[] { "Claimed" }, saved);
            Assert.Contains("MissingToken", result.ErrorMessage);
        }

        public class OwnersStub : DispatchProxy
        {
            public Guid CurrentToken;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(ICharacterOwnersRepository.IsStaleOwnerWrite))
                {
                    return Task.FromResult((Guid?)args[2] != CurrentToken);
                }
                throw new NotSupportedException(targetMethod.Name);
            }
        }

        public class CharactersStub : DispatchProxy
        {
            public List<string> SavedNames = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(ICharactersRepository.UpdatePosition))
                {
                    SavedNames.Add((string)args[1]);
                    return Task.CompletedTask;
                }
                throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
