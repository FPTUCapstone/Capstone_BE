using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;

namespace TripMate.Application.Features.Operator.PaymentChannels.Submit;

public sealed record SubmitPaymentChannelCredentialsCommand(
    string ClientId,
    string ApiKey,
    string ChecksumKey,
    string BeneficiaryAccountNumber,
    string BeneficiaryAccountName,
    string BeneficiaryBankCode)
    : IRequest<Result<ChannelStatusDto>>;