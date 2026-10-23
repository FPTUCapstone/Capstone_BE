using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Common;

public static class CostKindPolicy
{
    public static CostKind FromCategory(string? categoryName) =>
        PoiCategoryNames.IsFoodAndDrink(categoryName) ? CostKind.AverageSpend : CostKind.AdmissionTicket;
}