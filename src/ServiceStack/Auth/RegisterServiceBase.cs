using System;
using System.Globalization;
using System.Threading.Tasks;
using ServiceStack.FluentValidation;
using ServiceStack.Text;
using ServiceStack.Web;

namespace ServiceStack.Auth
{
    public class FullRegistrationValidator : RegistrationValidator
    {
        public FullRegistrationValidator() { RuleSet(ApplyTo.Post, () => RuleFor(x => x.DisplayName).NotEmpty()); }
    }

    public class RegistrationValidator : AbstractValidator<Register>
    {
        public RegistrationValidator()
        {
            RuleSet(
                ApplyTo.Post,
                () =>
                {
                    RuleFor(x => x.Password).NotEmpty();
                    RuleFor(x => x.ConfirmPassword)
                        .Equal(x => x.Password)
                        .When(x => x.ConfirmPassword != null)
                        .WithErrorCode(nameof(ErrorMessages.PasswordsShouldMatch))
                        .WithMessage(ErrorMessages.PasswordsShouldMatch.Localize(base.Request));
                    RuleFor(x => x.UserName).NotEmpty().When(x => x.Email.IsNullOrEmpty());
                    RuleFor(x => x.Email).NotEmpty().EmailAddress().When(x => x.UserName.IsNullOrEmpty());
                    RuleFor(x => x.UserName)
                        .MustAsync(async (x,token) =>
                        {
                            var authRepo = HostContext.AppHost.GetAuthRepositoryAsync(base.Request);
#if NET472 || NETSTANDARD2_0
                            await using (authRepo as IAsyncDisposable)
#else
                            using (authRepo as IDisposable)
#endif
                            {
                                return await authRepo.GetUserAuthByUserNameAsync(x).ConfigAwait() == null;
                            }
                        })
                        .WithErrorCode("AlreadyExists")
                        .WithMessage(ErrorMessages.UsernameAlreadyExists.Localize(base.Request))
                        .When(x => !x.UserName.IsNullOrEmpty());
                    RuleFor(x => x.Email)
                        .MustAsync(async (x,token) =>
                        {
                            var authRepo = HostContext.AppHost.GetAuthRepositoryAsync(base.Request);
#if NET472 || NETSTANDARD2_0
                            await using (authRepo as IAsyncDisposable)
#else
                            using (authRepo as IDisposable)
#endif
                            {
                                return x.IsNullOrEmpty() || await authRepo.GetUserAuthByUserNameAsync(x).ConfigAwait() == null;
                            }
                        })
                        .WithErrorCode("AlreadyExists")
                        .WithMessage(ErrorMessages.EmailAlreadyExists.Localize(base.Request))
                        .When(x => !x.Email.IsNullOrEmpty());
                });
            RuleSet(
                ApplyTo.Put,
                () =>
                {
                    RuleFor(x => x.UserName).NotEmpty().When(x => x.Email.IsNullOrEmpty());
                    RuleFor(x => x.Email).NotEmpty().EmailAddress().When(x => x.UserName.IsNullOrEmpty());
                });
        }
    }
    
    public abstract class RegisterServiceBase : Service
    {
        public IValidator<Register> RegistrationValidator { get; set; }

        protected IUserAuth ToUserAuth(Register request)
        {
            var to = AuthRepositoryAsync is ICustomUserAuth customUserAuth
                ? customUserAuth.CreateUserAuth()
                : new UserAuth();

            to.PopulateWithNonDefaultValues(request);
            to.PrimaryEmail = request.Email;
            return to;
        }

        protected async Task<bool> UserExistsAsync(IAuthSession session) => 
            session.IsAuthenticated && await AuthRepositoryAsync.GetUserAuthAsync(session, null).ConfigAwait() != null;

        protected async Task ValidateAndThrowAsync(Register request)
        {
            var validator = RegistrationValidator ?? new RegistrationValidator();
            await validator.ValidateAndThrowAsync(request, ApplyTo.Post).ConfigAwait();
        }

        protected async Task RegisterNewUserAsync(IAuthSession session, IUserAuth user)
        {
            var authEvents = TryResolve<IAuthEvents>();
            session.UserAuthId = user.Id.ToString(CultureInfo.InvariantCulture);
            session.PopulateSession(user);
            session.OnRegistered(Request, session, this);
            if (session is IAuthSessionExtended sessionExt)
                await sessionExt.OnRegisteredAsync(Request, session, this).ConfigAwait();
            authEvents?.OnRegistered(this.Request, session, this);
            if (authEvents is IAuthEventsAsync asyncEvents)
                await asyncEvents.OnRegisteredAsync(this.Request, session, this).ConfigAwait();
        }
        
        protected async Task<object> CreateRegisterResponse(IAuthSession session, string userName, string password, bool? autoLogin=null)
        {
            var returnUrl = Request.GetReturnUrl();
            if (!string.IsNullOrEmpty(returnUrl))
                GetPlugin<AuthFeature>()?.ValidateRedirectLinks(Request, returnUrl);

            RegisterResponse response = null;
            if (autoLogin.GetValueOrDefault())
            {
#if NETSTANDARD2_0 || NET472
                await using var authService = base.ResolveService<AuthenticateService>();
#else
                using var authService = base.ResolveService<AuthenticateService>();
#endif
                var authResponse = await authService.PostAsync(
                    new Authenticate {
                        provider = CredentialsAuthProvider.Name,
                        UserName = userName,
                        Password = password,
                    });

                if (authResponse is IHttpError)
                    throw (Exception) authResponse;

                if (authResponse is AuthenticateResponse typedResponse)
                {
                    response = new RegisterResponse {
                        SessionId = typedResponse.SessionId,
                        UserName = typedResponse.UserName,
                        ReferrerUrl = typedResponse.ReferrerUrl,
                        UserId = session.UserAuthId,
                        BearerToken = typedResponse.BearerToken,
                        RefreshToken = typedResponse.RefreshToken,
                    };
                }
            }

            if (response == null)
            {
                response = new RegisterResponse {
                    UserId = session.UserAuthId,
                    ReferrerUrl = Request.GetReturnUrl(),
                    UserName = session.UserName,
                };
            }

            var isHtml = Request.ResponseContentType.MatchesContentType(MimeTypes.Html);
            if (isHtml)
            {
                if (string.IsNullOrEmpty(returnUrl))
                    return response;

                return new HttpResult(response) {
                    Location = returnUrl
                };
            }

            return response;
        }
       
    }
}