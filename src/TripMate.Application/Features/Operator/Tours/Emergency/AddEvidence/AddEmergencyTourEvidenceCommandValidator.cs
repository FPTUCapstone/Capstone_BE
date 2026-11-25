using FluentValidation;

using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.Emergency.AddEvidence;

public sealed class AddEmergencyTourEvidenceCommandValidator
    : AbstractValidator<AddEmergencyTourEvidenceCommand>
{
    public AddEmergencyTourEvidenceCommandValidator()
    {
        RuleFor(command => command.TourId).GreaterThan(0).WithMessage("Mã tour không hợp lệ.");
        RuleFor(command => command.ScheduleId).GreaterThan(0).WithMessage("Mã lịch khởi hành không hợp lệ.");
        RuleFor(command => command)
            .Must(command => command.WithdrawEvidence
                || !string.IsNullOrWhiteSpace(command.EvidenceUrl)
                || !string.IsNullOrWhiteSpace(command.AuthorityReference)
                || !string.IsNullOrWhiteSpace(command.EvidenceReference))
            .WithMessage("Vui lòng nhập ít nhất một thông tin bằng chứng.");
        RuleFor(command => command)
            .Must(command => !command.WithdrawEvidence
                || (string.IsNullOrWhiteSpace(command.EvidenceUrl)
                    && string.IsNullOrWhiteSpace(command.AuthorityReference)
                    && string.IsNullOrWhiteSpace(command.EvidenceReference)))
            .WithMessage("Yêu cầu rút lại bằng chứng không được kèm nội dung bằng chứng.");
        RuleFor(command => command.EvidenceUrl)
            .Must(EmergencyEvidenceValidation.IsValidHttpsUrl)
            .WithMessage("Liên kết bằng chứng phải là HTTPS đầy đủ và không chứa thông tin đăng nhập.");
        RuleFor(command => command.AuthorityReference)
            .MaximumLength(EmergencyTourCancellationOperation.AuthorityReferenceMaxLength)
            .WithMessage("Cơ quan ban hành tối đa 200 ký tự.");
        RuleFor(command => command.EvidenceReference)
            .MaximumLength(EmergencyTourCancellationOperation.EvidenceMaxLength)
            .WithMessage("Số văn bản tối đa 500 ký tự.");
    }
}