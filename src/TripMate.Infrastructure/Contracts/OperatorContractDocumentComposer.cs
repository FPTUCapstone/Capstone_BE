using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Infrastructure.Contracts.Templates;

namespace TripMate.Infrastructure.Contracts;

public sealed class OperatorContractDocumentComposer : IOperatorContractDocumentComposer
{
    public Task<Result<byte[]>> PrepareAsync(byte[] sourceDocx, string fileName, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) =>
        new DocxContractTemplateProcessor().PrepareContractDocxAsync(new ContractTemplateUpload(fileName,
            DocxContractTemplateProcessor.DocxContentType, sourceDocx), values, cancellationToken);
}