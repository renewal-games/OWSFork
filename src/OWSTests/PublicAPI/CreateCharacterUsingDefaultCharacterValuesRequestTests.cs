using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSPublicAPI.DTOs;
using OWSPublicAPI.Requests.Users;
using OWSShared.Interfaces;
using Xunit;

namespace OWSTests.PublicAPI
{
    /// <summary>The handler reports what the repository did, without leaking raw exception text.</summary>
    public class CreateCharacterUsingDefaultCharacterValuesRequestTests
    {
        [Fact]
        public async Task RepositorySuccess_ReturnsSuccess()
        {
            SuccessAndErrorMessage result = await Handle(new SuccessAndErrorMessage { Success = true, ErrorMessage = "" });

            Assert.True(result.Success);
        }

        [Fact]
        public async Task UnknownDefaultSet_ReturnsFailure()
        {
            SuccessAndErrorMessage result = await Handle(new SuccessAndErrorMessage
            {
                Success = false,
                ErrorMessage = CharacterCreationMessages.UnknownDefaultCharacterSet
            });

            Assert.False(result.Success);
            Assert.Equal(CharacterCreationMessages.UnknownDefaultCharacterSet, result.ErrorMessage);
        }

        [Fact]
        public async Task RepositoryException_ReturnsGenericFailure()
        {
            SuccessAndErrorMessage result = await Handle(new SuccessAndErrorMessage
            {
                Success = false,
                ErrorMessage = "23505: duplicate key value violates unique constraint \"pk_characters\""
            });

            Assert.False(result.Success);
            Assert.Equal(CharacterCreationMessages.CharacterCreationFailed, result.ErrorMessage);
        }

        private static Task<SuccessAndErrorMessage> Handle(SuccessAndErrorMessage repositoryResult)
        {
            IUsersRepository users = Stub<IUsersRepository>.Create(new()
            {
                [nameof(IUsersRepository.GetUserSession)] = _ => Task.FromResult(new GetUserSession { UserGuid = Guid.NewGuid() }),
                [nameof(IUsersRepository.CreateCharacterUsingDefaultCharacterValues)] = _ => Task.FromResult(repositoryResult)
            });
            ICharactersRepository characters = Stub<ICharactersRepository>.Create(new()
            {
                [nameof(ICharactersRepository.GetCharByCharName)] = _ => Task.FromResult<GetCharByCharName>(null)
            });
            IPublicAPIInputValidation validation = Stub<IPublicAPIInputValidation>.Create(new()
            {
                [nameof(IPublicAPIInputValidation.ValidateCharacterName)] = _ => ""
            });
            IHeaderCustomerGUID customer = Stub<IHeaderCustomerGUID>.Create(new()
            {
                ["get_CustomerGUID"] = _ => Guid.NewGuid()
            });

            var request = new CreateCharacterUsingDefaultCharacterValuesRequest(
                new CreateCharacterUsingDefaultCharacterValuesDTO { UserSessionGUID = Guid.NewGuid(), CharacterName = "Hero", DefaultSetName = "doesnotexist" },
                users, characters, validation, customer);

            return request.Handle();
        }

        /// <summary>Interface stub answering only the named members; anything else throws.</summary>
        public class Stub<T> : DispatchProxy
        {
            private Dictionary<string, Func<object[], object>> _members;

            public static T Create(Dictionary<string, Func<object[], object>> members)
            {
                T proxy = Create<T, Stub<T>>();
                ((Stub<T>)(object)proxy)._members = members;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args) =>
                _members.TryGetValue(targetMethod.Name, out var member)
                    ? member(args)
                    : throw new NotSupportedException($"{typeof(T).Name}.{targetMethod.Name} is not stubbed.");
        }
    }
}
