using ModelContextProtocol;

namespace ApaHotelMcp;

/// <summary>hotel.json の 1 ホテル分（アパ直営・FC のみ保持）。</summary>
public sealed record Hotel(
    int HotelId,
    int HotelNo,
    string Name,
    string Prefecture,
    int AreaId,
    string AreaName,
    string Address,
    string CheckIn,
    string CheckOut,
    string Url,
    string Attribute);

/// <summary>get_planlist.jsp の 1 行（プラン × 部屋タイプ）。</summary>
public sealed record PlanRoomPrice(
    int PlanId,
    string PlanName,
    int RoomTypeId,
    string RoomTypeName,
    int TotalPrice,
    string ReserveUrl);

/// <summary>カレンダーの 1 日分。</summary>
public sealed record CalendarDay(
    string Date,
    bool Available,
    int? RoomsLeft,
    int? PricePerAdult,
    int? TotalForAdults,
    string? RoomTypeName);

public enum MealType
{
    RoomOnly,
    Breakfast,
    TwoMeals,
    Dinner,
    Any,
}

public static class MealTypeExtensions
{
    public static MealType Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "room_only" or "素泊まり" => MealType.RoomOnly,
        "breakfast" or "朝食付" or "朝食付き" => MealType.Breakfast,
        "two_meals" or "二食付" or "二食付き" => MealType.TwoMeals,
        "dinner" or "夕食付" or "夕食付き" => MealType.Dinner,
        "any" or "指定なし" => MealType.Any,
        _ => throw new McpException($"meal に不正な値です: {value}（room_only / breakfast / two_meals / dinner / any）"),
    };

    /// <summary>get_planlist.jsp の plan パラメータ値。指定なしはブラウザと同じく "NaN"。</summary>
    public static string ToPlanParam(this MealType meal) => meal switch
    {
        MealType.RoomOnly => "2",
        MealType.Breakfast => "3",
        MealType.TwoMeals => "4",
        MealType.Dinner => "5",
        _ => "NaN",
    };
}
