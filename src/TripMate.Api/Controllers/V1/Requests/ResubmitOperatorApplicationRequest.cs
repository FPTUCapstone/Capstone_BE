using Microsoft.AspNetCore.Mvc;

namespace TripMate.Api.Controllers.V1.Requests;

public sealed class ResubmitOperatorApplicationRequest
{
    [FromForm(Name = "companyName")]
    public string? CompanyName { get; init; }

    [FromForm(Name = "businessLicenseNo")]
    public string? BusinessLicenseNo { get; init; }

    [FromForm(Name = "taxCode")]
    public string? TaxCode { get; init; }

    [FromForm(Name = "contactPerson")]
    public string? ContactPerson { get; init; }

    [FromForm(Name = "businessAddress")]
    public string? BusinessAddress { get; init; }

    [FromForm(Name = "contactPhone")]
    public string? ContactPhone { get; init; }

    [FromForm(Name = "businessLicenseDocument")]
    public IFormFile? BusinessLicenseDocument { get; init; }

    [FromForm(Name = "supportingDocuments")]
    public List<IFormFile>? SupportingDocuments { get; init; }
}

