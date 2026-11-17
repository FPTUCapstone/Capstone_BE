using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.AccountProvisioning;

public sealed class CreateStaffAccountValidator : AbstractValidator<CreateStaffAccountCommand>
{
    public CreateStaffAccountValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().WithErrorCode(ProvisioningErrors.Required)
            .MaximumLength(150).WithErrorCode(ProvisioningErrors.Invalid);
        RuleFor(x => x.Email).Cascade(CascadeMode.Stop).NotEmpty().WithErrorCode(ProvisioningErrors.Required)
            .EmailAddress().WithErrorCode(ProvisioningErrors.Invalid)
            .MaximumLength(256).WithErrorCode(ProvisioningErrors.Invalid);
        RuleFor(x => x.PhoneNumber).NotEmpty().WithErrorCode(ProvisioningErrors.Required)
            .MaximumLength(20).WithErrorCode(ProvisioningErrors.Invalid);
    }
}

public sealed class CreateServiceProviderAccountValidator : AbstractValidator<CreateServiceProviderAccountCommand>
{
    public CreateServiceProviderAccountValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().WithErrorCode(ProvisioningErrors.Required)
            .MaximumLength(150).WithErrorCode(ProvisioningErrors.Invalid);
        RuleFor(x => x.Email).Cascade(CascadeMode.Stop).NotEmpty().WithErrorCode(ProvisioningErrors.Required)
            .EmailAddress().WithErrorCode(ProvisioningErrors.Invalid)
            .MaximumLength(ServiceProvider.ContactEmailMaxLength).WithErrorCode(ProvisioningErrors.Invalid);
        RuleFor(x => x.PhoneNumber).NotEmpty().WithErrorCode(ProvisioningErrors.Required)
            .MaximumLength(ServiceProvider.ContactPhoneMaxLength).WithErrorCode(ProvisioningErrors.Invalid);
        RuleFor(x => x.BusinessName).NotEmpty().WithErrorCode(ProvisioningErrors.Required)
            .MaximumLength(ServiceProvider.NameMaxLength).WithErrorCode(ProvisioningErrors.Invalid);
        RuleFor(x => x.Category).Cascade(CascadeMode.Stop).NotEmpty().WithErrorCode(ProvisioningErrors.Required)
            .Must(value => AccountProvisioningHandler.NormalizeCategory(value) is not null).WithErrorCode(ProvisioningErrors.Invalid);
        RuleFor(x => x.LinkedPoiIds).Cascade(CascadeMode.Stop).NotEmpty().WithErrorCode(ProvisioningErrors.InvalidPoi)
            .Must(ids => ids.All(id => id > 0)).WithErrorCode(ProvisioningErrors.InvalidPoi);
    }
}