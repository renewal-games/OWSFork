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
    /// The request reports what the repository did, and refuses a name that is already taken whoever holds it.
    /// </summary>
    public class CreateCharacterUsingDefaultCharacterValuesRequestTests
    {
        private static readonly Guid CustomerGUID = Guid.NewGuid();
        private static readonly Guid UserGUID = Guid.NewGuid();

        [Fact]
        public async Task Handle_ReturnsRepositoryFailure()
        {
            var users = Stub<IUsersRepository>.Create(new()
            {
                [nameof(IUsersRepository.GetUserSession)] = _ => Task.FromResult(new GetUserSession { UserGuid = UserGUID }),
                [nameof(IUsersRepository.CreateCharacterUsingDefaultCharacterValues)] = _ =>
                    Task.FromResult(new SuccessAndErrorMessage { Success = false, ErrorMessage = "boom" })
            });

            SuccessAndErrorMessage result = await Request(users, NoCharacters()).Handle();

            Assert.False(result.Success);
            Assert.Equal("boom", result.ErrorMessage);
        }

        [Fact]
        public async Task Handle_ReturnsSuccessWhenRepositorySucceeds()
        {
            var users = Stub<IUsersRepository>.Create(new()
            {
                [nameof(IUsersRepository.GetUserSession)] = _ => Task.FromResult(new GetUserSession { UserGuid = UserGUID }),
                [nameof(IUsersRepository.CreateCharacterUsingDefaultCharacterValues)] = _ =>
                    Task.FromResult(new SuccessAndErrorMessage { Success = true, ErrorMessage = "" })
            });

            SuccessAndErrorMessage result = await Request(users, NoCharacters()).Handle();

            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public async Task Handle_RejectsNameHeldByCharacterWithEmptyUserGuid()
        {
            var characters = Stub<ICharactersRepository>.Create(new()
            {
                [nameof(ICharactersRepository.GetCharByCharName)] = _ => Task.FromResult(new GetCharByCharName { UserGuid = Guid.Empty })
            });
            // No CreateCharacterUsingDefaultCharacterValues entry: reaching the insert throws.
            var users = Stub<IUsersRepository>.Create(new()
            {
                [nameof(IUsersRepository.GetUserSession)] = _ => Task.FromResult(new GetUserSession { UserGuid = UserGUID })
            });

            SuccessAndErrorMessage result = await Request(users, characters).Handle();

            Assert.False(result.Success);
            Assert.Equal("Character name already exists!", result.ErrorMessage);
        }

        private static ICharactersRepository NoCharacters() => Stub<ICharactersRepository>.Create(new()
        {
            [nameof(ICharactersRepository.GetCharByCharName)] = _ => Task.FromResult<GetCharByCharName>(null)
        });

        private static CreateCharacterUsingDefaultCharacterValuesRequest Request(IUsersRepository users, ICharactersRepository characters) =>
            new(new CreateCharacterUsingDefaultCharacterValuesDTO { UserSessionGUID = Guid.NewGuid(), CharacterName = "Hero", DefaultSetName = "Default" },
                users, characters, Stub<IPublicAPIInputValidation>.Create(new()
                {
                    [nameof(IPublicAPIInputValidation.ValidateCharacterName)] = _ => ""
                }),
                new HeaderCustomerGUID { CustomerGUID = CustomerGUID });

        private sealed class HeaderCustomerGUID : IHeaderCustomerGUID
        {
            public Guid CustomerGUID { get; set; }
        }

        /// <summary>An interface implementation that answers only the methods it is given and throws for the rest.</summary>
        public class Stub<T> : DispatchProxy where T : class
        {
            private Dictionary<string, Func<object[], object>> _methods;

            public static T Create(Dictionary<string, Func<object[], object>> methods)
            {
                T proxy = Create<T, Stub<T>>();
                ((Stub<T>)(object)proxy)._methods = methods;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args) =>
                _methods.TryGetValue(targetMethod.Name, out var method)
                    ? method(args)
                    : throw new NotImplementedException($"{typeof(T).Name}.{targetMethod.Name} was not expected.");
        }
    }
}
