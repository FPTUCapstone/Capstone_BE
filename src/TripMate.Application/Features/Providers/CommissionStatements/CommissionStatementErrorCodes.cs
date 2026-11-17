namespace TripMate.Application.Features.Providers.CommissionStatements;

public static class CommissionStatementErrorCodes
{
    public const string Forbidden = "CommissionStatement.Forbidden";
    public const string NotFound = "CommissionStatement.NotFound";
    public const string InvalidInput = "CommissionStatement.InvalidInput";
    public const string InvalidState = "CommissionStatement.InvalidState";
    public const string MissingBankReference = "CommissionStatement.MissingBankReference";
    public const string AlreadyPaid = "CommissionStatement.AlreadyPaid";
    public const string SystemError = "CommissionStatement.SystemError";
}
