using FluentValidation;

using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency.EmergencyStopActive;

public sealed class EmergencyStopActiveCommandValidator
    : AbstractValidator<EmergencyStopActiveCommand>
{
    public EmergencyStopActiveCommandValidator()
    {
        RuleFor(command => command.TourId).GreaterThan(0).WithMessage("Mã tour không hợp lệ.");
        RuleFor(command => command.ScheduleId).GreaterThan(0).WithMessage("Mã lịch khởi hành không hợp lệ.");
        RuleFor(command => command.CommandId).NotEqual(Guid.Empty).WithMessage("Thiếu Idempotency-Key hợp lệ.");
        RuleFor(command => command.ImpactVersion)
            .NotEmpty().WithMessage("Phiên bản tác động không hợp lệ.")
            .Length(EmergencyTourCancellationOperation.HashMaxLength).WithMessage("Phiên bản tác động không hợp lệ.")
            .Matches("^[0-9a-fA-F]+$").WithMessage("Phiên bản tác động không hợp lệ.");
        RuleFor(command => command.CurrentPassword)
            .NotEmpty().WithMessage("Vui lòng nhập mật khẩu hiện tại.")
            .MaximumLength(200).WithMessage("Mật khẩu tối đa 200 ký tự.");
        RuleFor(command => command.Reason).IsInEnum().WithMessage("Lý do không hợp lệ.");
        RuleFor(command => command.ReasonDetails)
            .NotEmpty().WithMessage("Vui lòng nhập lý do hiển thị cho du khách.")
            .MaximumLength(EmergencyTourCancellationOperation.ReasonDetailsMaxLength)
            .WithMessage("Lý do hiển thị cho du khách tối đa 1000 ký tự.");
        RuleFor(command => command.ImmediateInstructions)
            .NotEmpty().WithMessage("Vui lòng nhập chỉ dẫn khẩn cấp.")
            .MaximumLength(EmergencyTourCancellationOperation.InstructionsMaxLength)
            .WithMessage("Chỉ dẫn khẩn cấp tối đa 1000 ký tự.");
        RuleFor(command => command.EmergencyContact)
            .NotEmpty().WithMessage("Vui lòng nhập đầu mối liên hệ khẩn cấp.")
            .MaximumLength(EmergencyTourCancellationOperation.ContactMaxLength)
            .WithMessage("Đầu mối liên hệ tối đa 100 ký tự.");
        RuleFor(command => command.EvidenceReference)
            .MaximumLength(EmergencyTourCancellationOperation.EvidenceMaxLength)
            .WithMessage("Số văn bản tối đa 500 ký tự.");
        RuleFor(command => command.EvidenceUrl)
            .Must(EmergencyEvidenceValidation.IsValidHttpsUrl)
            .WithMessage("Liên kết bằng chứng phải là HTTPS đầy đủ và không chứa thông tin đăng nhập.");
        RuleFor(command => command.AuthorityReference)
            .MaximumLength(EmergencyTourCancellationOperation.AuthorityReferenceMaxLength)
            .WithMessage("Cơ quan ban hành tối đa 200 ký tự.");
        RuleFor(command => command)
            .Must(HasAuthorityEvidenceOrDeferral)
            .WithMessage("Chỉ thị của cơ quan chức năng cần có cơ quan ban hành, số văn bản hoặc đánh dấu bổ sung sau.");
    }

    private static bool HasAuthorityEvidenceOrDeferral(EmergencyStopActiveCommand command) =>
        command.Reason != EmergencyTourReason.AuthorityDirective
        || !string.IsNullOrWhiteSpace(command.AuthorityReference)
        || !string.IsNullOrWhiteSpace(command.EvidenceReference)
        || command.AddEvidenceLater;
}