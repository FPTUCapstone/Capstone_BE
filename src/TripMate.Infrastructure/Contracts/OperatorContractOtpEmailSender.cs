using System.Globalization;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Contracts.Common;

namespace TripMate.Infrastructure.Contracts;

/// <summary>Composes a transient signing code and reuses the configured classified email transport.</summary>
public sealed class OperatorContractOtpEmailSender(Func<IUserAdministrationNotificationEmailSender> transport) : IOperatorContractOtpEmailSender
{
    public async Task<EmailDeliveryResult> SendAsync(string destinationEmail, string otp, long contractId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(destinationEmail) || destinationEmail.Contains('\r') || destinationEmail.Contains('\n')
            || contractId <= 0 || otp is not { Length: 6 } || otp.Any(character => character is < '0' or > '9'))
            return EmailDeliveryResult.DefiniteFailure;

        var number = string.Create(CultureInfo.InvariantCulture, $"TM-OP-{contractId:D8}");
        var minutes = OperatorContractSigningPolicy.OtpTimeToLive.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture);
        var subject = "TripMate — Mã xác nhận ký hợp đồng đối tác";
        var body = $"Bạn đã yêu cầu xác nhận ký hợp đồng đối tác {number}.\n\n"
            + $"Mã xác nhận của bạn là: {otp}\n\n"
            + $"Mã có hiệu lực trong {minutes} phút và chỉ dùng cho hợp đồng đang hiển thị trong phiên ký của bạn.\n"
            + "Chỉ nhập mã trên màn hình ký hợp đồng TripMate do bạn chủ động mở. Không chia sẻ mã cho người khác.\n"
            + "Nếu bạn không yêu cầu ký hợp đồng, hãy bỏ qua email này.";

        try
        {
            return await transport().SendAsync(destinationEmail, subject, body, cancellationToken);
        }
        catch
        {
            // Once delegated to transport, cancellation or failure can have an ambiguous delivery outcome.
            // No exception content, recipient, or message payload is logged or returned.
            return EmailDeliveryResult.Unknown;
        }
    }
}