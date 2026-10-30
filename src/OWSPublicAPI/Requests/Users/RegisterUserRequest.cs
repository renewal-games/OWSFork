using Microsoft.AspNetCore.Mvc;
using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using OWSExternalLoginProviders.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OWSData.Models.StoredProcs;
using OWSPublicAPI.DTOs;
using Microsoft.Extensions.Logging;

namespace OWSPublicAPI.Requests.Users
{
    /// <summary>
    /// Register a User
    /// </summary>
    /// <remarks>
    /// Register a user with the system.  You can control validation with a custom IIPublicAPIInputValidation implementation.  See DefaultPublicAPIInputValidation for an example.
    /// </remarks>
    public class RegisterUserRequest
    {
        /// <summary>
        /// Returned for any failure to create the account.  The real cause is logged, not sent to the client.
        /// </summary>
        public const string RegistrationFailedMessage = "Registration failed. Please try again later.";

        private readonly RegisterUserDTO _registerUserDTO;
        private readonly Guid _customerGUID;
        private readonly IUsersRepository _usersRepository;
        private readonly IExternalLoginProviderFactory _externalLoginProviderFactory;
        private readonly Services.LoginGate _loginGate;
        private readonly IPublicAPIInputValidation _publicAPIInputValidation;
        private readonly ILogger _logger;

        /// <summary>
        /// RegisterUserRequest Constructor
        /// </summary>
        /// <remarks>
        /// Initialize the RegisterUserRequest object with dependencies
        /// </remarks>
        public RegisterUserRequest(RegisterUserDTO registerUserDTO, IUsersRepository usersRepository, IExternalLoginProviderFactory externalLoginProviderFactory, IHeaderCustomerGUID customerGuid, Services.LoginGate loginGate,
            IPublicAPIInputValidation publicAPIInputValidation, ILogger logger)
        {
            _registerUserDTO = registerUserDTO;
            _customerGUID = customerGuid.CustomerGUID;
            _usersRepository = usersRepository;
            _externalLoginProviderFactory = externalLoginProviderFactory;
            _loginGate = loginGate;
            _publicAPIInputValidation = publicAPIInputValidation;
            _logger = logger;
        }

        /// <summary>
        /// RegisterUserRequest Request Handler
        /// </summary>
        /// <remarks>
        /// Handle the RegisterUserRequest request
        /// </remarks>
        public async Task<PlayerLoginAndCreateSession> Handle()
        {
            // A new account is a Player, and registering logs it straight in.
            if (await _loginGate.ClosedForMaintenance(OWSData.Models.UserRoles.Player))
            {
                return new PlayerLoginAndCreateSession { ErrorMessage = Services.LoginGate.MaintenanceMessage };
            }

            string validationError = Validate();
            if (!String.IsNullOrEmpty(validationError))
            {
                return new PlayerLoginAndCreateSession { ErrorMessage = validationError };
            }

            //Check for duplicate account before creating a new one:
            var foundUser = await _usersRepository.GetUserFromEmail(_customerGUID, _registerUserDTO.Email);

            //This user already exists
            if (foundUser != null)
            {
                PlayerLoginAndCreateSession errorOutput = new PlayerLoginAndCreateSession()
                {
                    ErrorMessage = "Duplicate Account!"
                };

                return errorOutput;
            }

            //Register the new account
            SuccessAndErrorMessage registerOutput = await _usersRepository.RegisterUser(_customerGUID, _registerUserDTO.Email, _registerUserDTO.Password, _registerUserDTO.FirstName ?? "", _registerUserDTO.LastName ?? "");

            //There was an error registering the new account
            if (!registerOutput.Success)
            {
                //The repository's message can be a raw database error, so it is logged and the client gets a generic one.
                //It can echo the submitted values, so line breaks are stripped to keep it to one log entry.
                string logSafeError = (registerOutput.ErrorMessage ?? "").Replace("\r", " ").Replace("\n", " ");
                _logger.LogWarning("RegisterUser failed for customer {CustomerGUID}: {Error}", _customerGUID, logSafeError);

                PlayerLoginAndCreateSession errorOutput = new PlayerLoginAndCreateSession()
                {
                    ErrorMessage = RegistrationFailedMessage
                };

                return errorOutput;
            }

            //Login to the new account to get a UserSession
            PlayerLoginAndCreateSession playerLoginAndCreateSession = await _usersRepository.LoginAndCreateSession(_customerGUID, _registerUserDTO.Email, _registerUserDTO.Password);

            /*
            if (externalLoginProviderFactory != null)
            {
                //This method will do nothing if AutoRegister isn't set to true
                //await externalLoginProvider.RegisterAsync(Email, Password, Email);
            }
            */

            return playerLoginAndCreateSession;
        }

        private string Validate()
        {
            if (_registerUserDTO == null)
            {
                return "Please enter a valid Email address.";
            }

            foreach (string error in new[]
            {
                _publicAPIInputValidation.ValidateEmail(_registerUserDTO.Email),
                _publicAPIInputValidation.ValidatePassword(_registerUserDTO.Password),
                _publicAPIInputValidation.ValidateFirstName(_registerUserDTO.FirstName),
                _publicAPIInputValidation.ValidateLastName(_registerUserDTO.LastName)
            })
            {
                if (!String.IsNullOrEmpty(error))
                {
                    return error;
                }
            }

            return "";
        }
    }
}
