namespace TripMate.Domain.Enums;

/// <summary>
/// Document types for Tour Operator applications. TaxCode replaces the legacy TaxCertificate label.
/// Member names are string-converted by EF Core.
/// </summary>
public enum OperatorDocumentType
{
    BusinessLicense = 1,
    TaxCode = 2,
    Other = 3,
}
