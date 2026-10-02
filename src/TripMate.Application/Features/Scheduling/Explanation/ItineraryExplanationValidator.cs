using System.Collections.Frozen;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Explanation;

public static class ItineraryExplanationValidator
{
    public static bool TryValidate(
        ItineraryExplanationInput input,
        ItineraryExplanationResult? result,
        out IReadOnlyDictionary<int, string> validatedExplanations)
    {
        ArgumentNullException.ThrowIfNull(input);
        var accepted = new Dictionary<int, string>();

        if (result?.Items is null || result.Items.Count != input.Items.Count)
        {
            validatedExplanations = accepted.ToFrozenDictionary();
            return false;
        }

        var expectedBySequence = input.Items.ToDictionary(item => item.SequenceNo);
        foreach (ItineraryExplanationItemResult? item in result.Items)
        {
            if (item is null
                || !expectedBySequence.TryGetValue(item.SequenceNo, out var expected)
                || expected.PoiId != item.PoiId)
            {
                validatedExplanations = new Dictionary<int, string>().ToFrozenDictionary();
                return false;
            }

            string? explanation = item.FriendlyExplanation?.Trim();
            if (string.IsNullOrEmpty(explanation)
                || explanation.Length > ItineraryItem.FriendlyExplanationMaxLength
                || !accepted.TryAdd(item.SequenceNo, explanation))
            {
                validatedExplanations = new Dictionary<int, string>().ToFrozenDictionary();
                return false;
            }
        }

        if (accepted.Count != expectedBySequence.Count)
        {
            validatedExplanations = new Dictionary<int, string>().ToFrozenDictionary();
            return false;
        }

        validatedExplanations = accepted.ToFrozenDictionary();
        return true;
    }
}