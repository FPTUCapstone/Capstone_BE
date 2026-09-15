using FluentValidation;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.Login;

namespace TripMate.Application.Features.Authentication.WebSignIn;

public sealed record WebPasswordSignInCommand(string? Email, string? Password, bool AdministratorOnly)
    : IRequest<Result<AuthResponseDto>>;

public sealed class WebPasswordSignInCommandHandler(ISender sender)
    : IRequestHandler<WebPasswordSignInCommand, Result<AuthResponseDto>>
{
    public async Task<Result<AuthResponseDto>> Handle(WebPasswordSignInCommand request, CancellationToken cancellationToken)
    {
        var normalized = request with { Email = request.Email?.Trim().ToLowerInvariant() };
        var validation = await new InputValidator().ValidateAsync(normalized, cancellationToken);
        if (!validation.IsValid)
        {
            var fields = validation.Errors.GroupBy(e => e.PropertyName.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorCode).ToArray());
            return Result.Failure<AuthResponseDto>(AuthErrorCodes.RequestInvalid, "Invalid sign-in request.",
                new Dictionary<string, object?> { ["errors"] = fields });
        }
        return await sender.Send(new LoginCommand(normalized.Email!, normalized.Password!)
        { AdministratorOnly = normalized.AdministratorOnly }, cancellationToken);
    }

    private sealed class InputValidator : AbstractValidator<WebPasswordSignInCommand>
    {
        public InputValidator()
        {
            RuleFor(x => x.Email).Cascade(CascadeMode.Stop).NotEmpty().WithErrorCode(AuthErrorCodes.Msg01)
                .EmailAddress().WithErrorCode(AuthErrorCodes.Msg02);
            RuleFor(x => x.Password).NotEmpty().WithErrorCode(AuthErrorCodes.Msg01);
        }
    }
}