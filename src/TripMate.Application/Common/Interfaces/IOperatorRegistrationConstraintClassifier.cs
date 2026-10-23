using Microsoft.EntityFrameworkCore;

namespace TripMate.Application.Common.Interfaces;

public enum OperatorRegistrationConstraint
{
    None,
    Email,
    TaxCodeOrBusinessLicense,
}

/// <summary>Classifies only known database unique-index races for operator registration.</summary>
public interface IOperatorRegistrationConstraintClassifier
{
    OperatorRegistrationConstraint Classify(DbUpdateException exception);
}