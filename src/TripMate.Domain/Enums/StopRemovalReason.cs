namespace TripMate.Domain.Enums;

/// <summary>Why a traveler removed a stop while adjusting an itinerary (UC-10 traveler feedback, D10).</summary>
public enum StopRemovalReason
{
    /// <summary>"Không thích": a dislike, the only reason that starts the removed-stop cooldown.</summary>
    NotInterested,

    /// <summary>"Không đủ thời gian": says nothing about taste.</summary>
    NoTime,

    /// <summary>"Đã đi rồi": says nothing about taste.</summary>
    AlreadyVisited,
}