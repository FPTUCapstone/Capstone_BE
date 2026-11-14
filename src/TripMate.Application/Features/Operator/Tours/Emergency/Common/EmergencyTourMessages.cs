namespace TripMate.Application.Features.Operator.Tours.Emergency;

/// <summary>The SRS message-table wording UC-70 (SRS UC-66) shows, in the app's Vietnamese.</summary>
public static class EmergencyTourMessages
{
    /// <summary>SRS MSG123: a mandatory reason is empty.</summary>
    public const string ReasonRequired = "Vui lòng nhập lý do.";

    /// <summary>SRS MSG126: the account is not the owning, active, approved Tour Operator.</summary>
    public const string PermissionDenied = "Bạn không có quyền truy cập chức năng này.";

    /// <summary>SRS MSG133: the departure is already cancelled, stopped or completed.</summary>
    public const string StatusNotAllowed =
        "Thao tác này không khả dụng với trạng thái hiện tại của lịch khởi hành.";
}