using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>Reference data shared by the UC-35 tour authoring unit tests.</summary>
internal static class TourAuthoringTestData
{
    public const long DestinationId = 1;
    public const long SecondDestinationId = 2;
    public const long PolicyTemplateId = 1;

    public static Destination NewDestination(long id, string name)
    {
        var destination = (Destination)Activator.CreateInstance(typeof(Destination), nonPublic: true)!;
        destination.Id = id;
        typeof(Destination).GetProperty(nameof(Destination.Name))!.SetValue(destination, name);
        return destination;
    }

    public static CancellationPolicyTemplate NewPolicy(long id = PolicyTemplateId, bool active = true)
    {
        var policy = CancellationPolicyTemplate.Create(
            "Standard",
            "Standard cancellation terms",
            DateTimeOffset.UtcNow,
            [
                CancellationPolicyTemplateRule.Create(30, 0),
                CancellationPolicyTemplateRule.Create(15, 25),
                CancellationPolicyTemplateRule.Create(0, 100),
            ]);
        typeof(CancellationPolicyTemplate).GetProperty(nameof(CancellationPolicyTemplate.Id))!
            .SetValue(policy, id);
        typeof(CancellationPolicyTemplate).GetProperty(nameof(CancellationPolicyTemplate.IsActive))!
            .SetValue(policy, active);
        return policy;
    }

    /// <summary>Seeds two destinations (ids 1 and 2) and the active policy template with id 1.</summary>
    public static async Task SeedReferenceDataAsync(TestDbContext db)
    {
        if (await db.Destinations.AnyAsync()) return;

        db.Destinations.Add(NewDestination(DestinationId, "Da Nang"));
        db.Destinations.Add(NewDestination(SecondDestinationId, "Hoi An"));
        db.CancellationPolicyTemplates.Add(NewPolicy());
        await db.SaveChangesAsync();
    }
}