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
    /// <summary>
    /// The handler reports what the repository did, without leaking raw exception text, and refuses a name that is
    /// already taken whoever holds it.
    /// </summary>
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
        public async Task NameTakenByConcurrentCreate_ReturnsNameAlreadyExists()
        {
            // The repository maps the unique index's violation (23505) to this message.
            SuccessAndErrorMessage result = await Handle(new SuccessAndErrorMessage
            {
                Success = false,
                ErrorMessage = CharacterCreationMessages.CharacterNameAlreadyExists
            });

            Assert.False(result.Success);
            Assert.Equal(CharacterCreationMessages.CharacterNameAlreadyExists, result.ErrorMessage);
        }

        [Fact]
        public async Task NameHeldByCharacterWithEmptyUserGuid_IsRejected()
        {
            ICharactersRepository characters = Stub<ICharactersRepository>.Create(new()
            {
                [nameof(ICharactersRepository.GetCharByCharName)] = _ => Task.FromResult(new GetCharByCharName { UserGuid = Guid.Empty })
            });

            // The repository result is never used: the users stub has no CreateCharacterUsingDefaultCharacterValues, so reaching the insert throws.
            SuccessAndErrorMessage result = await Handle(null, characters);

            Assert.False(result.Success);
            Assert.Equal(CharacterCreationMessages.CharacterNameAlreadyExists, result.ErrorMessage);
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

        private static Task<SuccessAndErrorMessage> Handle(SuccessAndErrorMessage repositoryResult, ICharactersRepository characters = null)
        {
            var userMembers = new Dictionary<string, Func<object[], object>>
            {
                [nameof(IUsersRepository.GetUserSession)] = _ => Task.FromResult(new GetUserSession { UserGuid = Guid.NewGuid() })
            };
            if (repositoryResult != null)
            {
                userMembers[nameof(IUsersRepository.CreateCharacterUsingDefaultCharacterValues)] = _ => Task.FromResult(repositoryResult);
            }
            IUsersRepository users = Stub<IUsersRepository>.Create(userMembers);
            characters ??= Stub<ICharactersRepository>.Create(new()
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
