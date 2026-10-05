using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Persistence;

internal sealed class OperatorRegistrationConstraintClassifier : IOperatorRegistrationConstraintClassifier
{
    public OperatorRegistrationConstraint Classify(DbUpdateException exception)
    {
        if (exception.InnerException is not SqlException sql ||
            sql.Number is not (2601 or 2627))
        {
            return OperatorRegistrationConstraint.None;
        }

        if (sql.Message.Contains("UX_Users_Email", StringComparison.OrdinalIgnoreCase))
        {
            return OperatorRegistrationConstraint.Email;
        }

        if (sql.Message.Contains("UQ_OperatorProfiles_TaxCode", StringComparison.OrdinalIgnoreCase) ||
            sql.Message.Contains("UX_OperatorProfiles_BusinessLicenseNo", StringComparison.OrdinalIgnoreCase))
        {
            return OperatorRegistrationConstraint.TaxCodeOrBusinessLicense;
        }

        return OperatorRegistrationConstraint.None;
    }
}