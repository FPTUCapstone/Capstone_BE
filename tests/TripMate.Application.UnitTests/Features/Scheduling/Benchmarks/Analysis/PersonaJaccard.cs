namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public static class PersonaJaccard
{
    public static double Calculate(IReadOnlyCollection<long>? setA, IReadOnlyCollection<long>? setB)
    {
        var (intersection, union) = Counts(setA, setB);
        return union == 0 ? 1.0 : (double)intersection / union;
    }

    public static (int IntersectionCount, int UnionCount) Counts(IReadOnlyCollection<long>? setA, IReadOnlyCollection<long>? setB)
    {
        var distinctA = setA != null ? new HashSet<long>(setA) : [];
        var distinctB = setB != null ? new HashSet<long>(setB) : [];

        var intersectionCount = distinctA.Count(distinctB.Contains);
        var unionCount = distinctA.Union(distinctB).Count();

        return (intersectionCount, unionCount);
    }
}