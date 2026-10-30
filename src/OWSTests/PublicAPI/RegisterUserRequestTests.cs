using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OWSData.Models.Composites;
using OWSData.Models.StoredProcs;
using OWSData.Repositories.Interfaces;
using OWSPublicAPI.DTOs;
using OWSPublicAPI.Requests.Users;
using OWSPublicAPI.Services;
using OWSShared.Implementations;
using OWSShared.Interfaces;
using Xunit;

namespace OWSTests.PublicAPI
{
    /// <summary>RegisterUser validates its input and never hands a repository error to the client.</summary>
    public class RegisterUserRequestTests
    {
        private class CustomerGuid : IHeaderCustomerGUID
        {
            public Guid CustomerGUID { get; set; } = Guid.NewGuid();
        }

        /// <summary>Answers the few repository calls RegisterUser makes and records RegisterUser itself; anything else returns default.</summary>
        public class FakeRepository : DispatchProxy
        {
            public int RegisterUserCalls;
            public SuccessAndErrorMessage RegisterResult = new SuccessAndErrorMessage { Success = true, ErrorMessage = "" };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case nameof(IUsersRepository.RegisterUser):
                        RegisterUserCalls++;
                        return Task.FromResult(RegisterResult);
                    case nameof(IUsersRepository.GetUserFromEmail):
                        return Task.FromResult<GetUserSession>(null);
                    case nameof(IUsersRepository.LoginAndCreateSession):
                        return Task.FromResult(new PlayerLoginAndCreateSession { Authenticated = true, ErrorMessage = "" });
                    case nameof(IConsoleSettingsRepository.GetAll):
                        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
                    default:
                        return null;
                }
            }
        }

        private static (RegisterUserRequest Request, FakeRepository Users) Build(RegisterUserDTO dto, SuccessAndErrorMessage registerResult = null)
        {
            IUsersRepository users = DispatchProxy.Create<IUsersRepository, FakeRepository>();
            FakeRepository fakeUsers = (FakeRepository)(object)users;
            if (registerResult != null)
            {
                fakeUsers.RegisterResult = registerResult;
            }
            IConsoleSettingsRepository settings = DispatchProxy.Create<IConsoleSettingsRepository, FakeRepository>();
            ISanctionsRepository sanctions = DispatchProxy.Create<ISanctionsRepository, FakeRepository>();

            RegisterUserRequest request = new RegisterUserRequest(dto, users, null, new CustomerGuid(),
                new LoginGate(sanctions, settings, users), new DefaultPublicAPIInputValidation(), NullLogger.Instance);
            return (request, fakeUsers);
        }

        private static RegisterUserDTO Valid() => new RegisterUserDTO
        {
            Email = "player@example.com",
            Password = "correct horse",
            FirstName = "Test",
            LastName = "Player"
        };

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-an-email")]
        [InlineData("two@@example.com")]
        public async Task MalformedEmail_IsRejectedBeforeRegistering(string email)
        {
            RegisterUserDTO dto = Valid();
            dto.Email = email;
            var (request, users) = Build(dto);

            PlayerLoginAndCreateSession result = await request.Handle();

            Assert.False(String.IsNullOrEmpty(result.ErrorMessage));
            Assert.Equal(0, users.RegisterUserCalls);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task MissingPassword_IsRejectedBeforeRegistering(string password)
        {
            RegisterUserDTO dto = Valid();
            dto.Password = password;
            var (request, users) = Build(dto);

            PlayerLoginAndCreateSession result = await request.Handle();

            Assert.False(String.IsNullOrEmpty(result.ErrorMessage));
            Assert.Equal(0, users.RegisterUserCalls);
        }

        [Fact]
        public async Task OverlongPasswordAndNames_AreRejectedBeforeRegistering()
        {
            foreach (Action<RegisterUserDTO> breakIt in new Action<RegisterUserDTO>[]
            {
                d => d.Password = new string('é', 37),
                d => d.FirstName = new string('a', 51),
                d => d.LastName = new string('a', 51)
            })
            {
                RegisterUserDTO dto = Valid();
                breakIt(dto);
                var (request, users) = Build(dto);

                PlayerLoginAndCreateSession result = await request.Handle();

                Assert.False(String.IsNullOrEmpty(result.ErrorMessage));
                Assert.Equal(0, users.RegisterUserCalls);
            }
        }

        [Fact]
        public async Task RepositoryFailure_ReturnsGenericMessage()
        {
            var (request, users) = Build(Valid(), new SuccessAndErrorMessage
            {
                Success = false,
                ErrorMessage = "23505: duplicate key value violates unique constraint internal-db-detail"
            });

            PlayerLoginAndCreateSession result = await request.Handle();

            Assert.Equal(1, users.RegisterUserCalls);
            Assert.DoesNotContain("internal-db-detail", result.ErrorMessage);
            Assert.Equal(RegisterUserRequest.RegistrationFailedMessage, result.ErrorMessage);
        }

        [Fact]
        public async Task ValidInput_RegistersAndLogsIn()
        {
            RegisterUserDTO dto = Valid();
            dto.FirstName = "";
            dto.LastName = null;
            var (request, users) = Build(dto);

            PlayerLoginAndCreateSession result = await request.Handle();

            Assert.Equal(1, users.RegisterUserCalls);
            Assert.True(String.IsNullOrEmpty(result.ErrorMessage));
            Assert.True(result.Authenticated);
        }
    }
}
