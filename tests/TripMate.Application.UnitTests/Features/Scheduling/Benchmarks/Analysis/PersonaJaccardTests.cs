using FluentAssertions;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public class PersonaJaccardTests
{
    [Fact]
    public void Calculate_IdenticalSets_ReturnsOne()
    {
        long[] setA = [1, 2, 3, 4, 5];
        long[] setB = [5, 4, 3, 2, 1];

        var jaccard = PersonaJaccard.Calculate(setA, setB);

        jaccard.Should().Be(1.0);
    }

    [Fact]
    public void Calculate_DisjointSets_ReturnsZero()
    {
        long[] setA = [1, 2, 3];
        long[] setB = [4, 5, 6];

        var jaccard = PersonaJaccard.Calculate(setA, setB);

        jaccard.Should().Be(0.0);
    }

    [Fact]
    public void Calculate_PartialOverlap_ReturnsExactRatio()
    {
        // A = {1, 2, 3}, B = {2, 3, 4, 5}
        // Intersection = {2, 3} (2), Union = {1, 2, 3, 4, 5} (5)
        // J = 2 / 5 = 0.40
        long[] setA = [1, 2, 3];
        long[] setB = [2, 3, 4, 5];

        var jaccard = PersonaJaccard.Calculate(setA, setB);

        jaccard.Should().Be(0.40);
    }

    [Fact]
    public void Calculate_DuplicateIdsInInput_CountsEachPoiOnce()
    {
        // A = [1, 1, 2, 2], B = [2, 3, 3]
        // Distinct A = {1, 2}, Distinct B = {2, 3}
        // Intersection = {2} (1), Union = {1, 2, 3} (3)
        // J = 1 / 3
        long[] setA = [1, 1, 2, 2];
        long[] setB = [2, 3, 3];

        var jaccard = PersonaJaccard.Calculate(setA, setB);

        jaccard.Should().BeApproximately(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Calculate_BothSetsEmpty_ReturnsOne()
    {
        long[] setA = [];
        long[] setB = [];

        var jaccard = PersonaJaccard.Calculate(setA, setB);

        jaccard.Should().Be(1.0, "authoritative rule defines J(empty, empty) = 1.0");
    }

    [Fact]
    public void Calculate_OneSetEmpty_ReturnsZero()
    {
        long[] setA = [1, 2];
        long[] setB = [];

        var jaccardAB = PersonaJaccard.Calculate(setA, setB);
        var jaccardBA = PersonaJaccard.Calculate(setB, setA);

        jaccardAB.Should().Be(0.0);
        jaccardBA.Should().Be(0.0);
    }

    [Fact]
    public void Calculate_IsSymmetric_ForAnyInputs()
    {
        long[] setA = [1, 5, 8, 12, 13];
        long[] setB = [5, 7, 1, 3, 2, 6];

        var jaccardAB = PersonaJaccard.Calculate(setA, setB);
        var jaccardBA = PersonaJaccard.Calculate(setB, setA);

        jaccardAB.Should().Be(jaccardBA);
    }

    [Fact]
    public void Counts_CalculatesIntersectionAndUnionCorrectly()
    {
        long[] setA = [1, 5, 8];
        long[] setB = [5, 8, 12, 13];

        var (intersection, union) = PersonaJaccard.Counts(setA, setB);

        intersection.Should().Be(2);
        union.Should().Be(5);
    }
}