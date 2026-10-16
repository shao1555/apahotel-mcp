using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ModelContextProtocol;
using Microsoft.Extensions.Logging;

namespace ApaHotelMcp;

/// <summary>
/// アパホテル公式サイトのホテル詳細ページが内部で呼んでいるエンドポイントのクライアント。
/// サイト側の負荷を避けるため同時リクエスト数を絞っている。
/// </summary>
public sealed partial class ApaClient(IHttpClientFactory httpClientFactory, ILogger<ApaClient> logger)
{
    public const string HttpClientName = "apahotel";

    private static readonly SemaphoreSlim Throttle = new(4);
    private static readonly HtmlParser Parser = new();

    private HttpClient Http => httpClientFactory.CreateClient(HttpClientName);

    public async Task<byte[]> GetHotelMasterJsonAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "json/hotel.json");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        return await SendBytesAsync(req, ct);
    }

    /// <summary>ホテル詳細ページの HTML から予約システム側のホテル ID（econ_hotel_id）を取り出す。</summary>
    public async Task<int?> FetchEconHotelIdAsync(string hotelPageUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, hotelPageUrl);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        var html = await SendStringAsync(req, ct);
        var m = EconIdRegex().Match(html);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// 指定日程で予約可能なプラン × 部屋タイプと合計料金（税込）を取得する。
    /// 満室の部屋タイプは結果に含まれない。
    /// </summary>
    public async Task<IReadOnlyList<PlanRoomPrice>> GetPlanListAsync(
        Hotel hotel, int econHotelId, DateOnly checkIn, int nights, int adults, int rooms, MealType meal, CancellationToken ct)
    {
        var checkOut = checkIn.AddDays(nights);
        // ブラウザの JS は未選択項目を parseInt("") = NaN のまま送っており、サーバーもそれを「指定なし」と解釈する
        var form = new Dictionary<string, string>
        {
            // 予約リンクの生成にのみ使われる。空室検索自体は eHotelId だけで決まる
            ["hotelId"] = hotel.HotelId.ToString(CultureInfo.InvariantCulture),
            ["hotelNo"] = hotel.HotelNo.ToString(CultureInfo.InvariantCulture),
            ["eHotelId"] = econHotelId.ToString(CultureInfo.InvariantCulture),
            ["checkin"] = checkIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["checkout"] = checkOut.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["stayCount"] = nights.ToString(CultureInfo.InvariantCulture),
            ["roomNumber"] = rooms.ToString(CultureInfo.InvariantCulture),
            ["peopleCount"] = adults.ToString(CultureInfo.InvariantCulture),
            ["children1"] = "", ["children2"] = "", ["children3"] = "",
            ["children4"] = "", ["children5"] = "", ["children6"] = "",
            ["budget"] = "NaN",
            ["plan"] = meal.ToPlanParam(),
            ["smoking"] = "0",
            ["roomType"] = "NaN",
            ["langCode"] = "ja_jp",
        };
        var html = await PostFormStringAsync("apacustom/get_planlist.jsp", form, ct);
        return ParsePlanList(html);
    }

    internal static IReadOnlyList<PlanRoomPrice> ParsePlanList(string html)
    {
        var doc = Parser.ParseDocument(html);
        var results = new List<PlanRoomPrice>();
        foreach (var plan in doc.QuerySelectorAll(".p-hotelWrap__planList__item"))
        {
            var planName = Clean(plan.QuerySelector(".p-hotelWrap__planList__item__title")?.TextContent);
            foreach (var row in plan.QuerySelectorAll(".p-hotelWrap__planList__item__planMenu__main__recordWrap"))
            {
                var roomName = Clean(row.QuerySelector("[class*='record--roomType']")?.TextContent);
                var priceText = row.QuerySelector("[class*='record--price']")?.TextContent ?? "";
                var digits = new string(priceText.Where(char.IsAsciiDigit).ToArray());
                var form = row.QuerySelector("form[name='reservation_form']");
                if (digits.Length == 0 || form is null) continue;

                var inputs = form.QuerySelectorAll("input[type='hidden']")
                    .Select(i => (Name: i.GetAttribute("name") ?? "", Value: i.GetAttribute("value") ?? ""))
                    .Where(i => i.Name.Length > 0)
                    .ToList();
                string Field(string name) => inputs.FirstOrDefault(i => i.Name == name).Value ?? "";

                var action = form.GetAttribute("action") ?? "/apacustom/apa_hotel_reserve_login_check.jsp";
                var query = string.Join("&", inputs.Select(i => $"{Uri.EscapeDataString(i.Name)}={Uri.EscapeDataString(i.Value)}"));

                results.Add(new PlanRoomPrice(
                    PlanId: int.TryParse(Field("plan_id"), out var pid) ? pid : 0,
                    PlanName: planName,
                    RoomTypeId: int.TryParse(Field("room_type"), out var rid) ? rid : 0,
                    RoomTypeName: roomName,
                    TotalPrice: int.Parse(digits, CultureInfo.InvariantCulture),
                    ReserveUrl: new Uri(new Uri("https://www.apahotel.com/"), action) + "?" + query));
            }
        }
        return results;
    }

    /// <summary>ホテルの販売中プランと、プランごとの部屋タイプ一覧。</summary>
    public async Task<IReadOnlyList<(int PlanId, string PlanName, IReadOnlyList<(int TypeId, string TypeName)> RoomTypes)>>
        GetPlanMasterAsync(int econHotelId, int adults, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["method"] = "post",
            ["econ_hotel_id"] = econHotelId.ToString(CultureInfo.InvariantCulture),
            ["num_adults"] = adults.ToString(CultureInfo.InvariantCulture),
            ["usecorp"] = "0",
        };
        using var json = JsonDocument.Parse(await PostFormStringAsync("apacustom/apa_hotel_calendar_master_json.jsp", form, ct));
        if (!json.RootElement.TryGetProperty("PLANS", out var plans) || plans.ValueKind != JsonValueKind.Array) return [];

        return plans.EnumerateArray().Select(p => (
            PlanId: int.Parse(p.GetProperty("PLN_ID").GetString()!, CultureInfo.InvariantCulture),
            PlanName: Clean(p.GetProperty("PLN_NAME").GetString()),
            RoomTypes: (IReadOnlyList<(int, string)>)p.GetProperty("ROOM_INFO").EnumerateArray()
                .Select(r => (int.Parse(r.GetProperty("TYPE_ID").GetString()!, CultureInfo.InvariantCulture),
                              Clean(r.GetProperty("TYPE_NAME").GetString())))
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// 1 か月分の日別空室数と大人 1 名あたり 1 泊料金（税込）。
    /// 料金が 0 の日は販売期間外、空室数が 0 の日は満室。
    /// </summary>
    public async Task<IReadOnlyDictionary<DateOnly, (int RoomsLeft, int PricePerAdult)>> GetMonthCalendarAsync(
        int econHotelId, int planId, int roomTypeId, int year, int month, int adults, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["method"] = "post",
            ["econ_hotel_id"] = econHotelId.ToString(CultureInfo.InvariantCulture),
            ["room_type"] = roomTypeId.ToString(CultureInfo.InvariantCulture),
            ["year"] = year.ToString(CultureInfo.InvariantCulture),
            ["month"] = month.ToString(CultureInfo.InvariantCulture),
            ["people_count"] = adults.ToString(CultureInfo.InvariantCulture),
            ["plan_id"] = planId.ToString(CultureInfo.InvariantCulture),
            ["stay_count"] = "1",
            ["children1"] = "", ["children2"] = "", ["children3"] = "",
            ["children4"] = "", ["children5"] = "", ["children6"] = "",
            ["usecorp"] = "0",
        };
        using var json = JsonDocument.Parse(await PostFormStringAsync("apacustom/apa_hotel_calendar_json.jsp", form, ct));
        var root = json.RootElement;
        if (root.TryGetProperty("ERR_CODE", out var err) && err.GetString() is { Length: > 0 } code)
        {
            logger.LogDebug("calendar error {Code} hotel={Hotel} plan={Plan} room={Room}", code, econHotelId, planId, roomTypeId);
            return new Dictionary<DateOnly, (int, int)>();
        }

        var roomInfo = root.GetProperty("PLAN")[0].GetProperty("ROOM_INFO")[0];
        if (!roomInfo.TryGetProperty("ROOM_COUNT", out var counts) || counts.ValueKind != JsonValueKind.Object)
            return new Dictionary<DateOnly, (int, int)>();
        var charges = roomInfo.GetProperty("ROOM_CHARGE_LIST");

        var result = new Dictionary<DateOnly, (int, int)>();
        foreach (var day in counts.EnumerateObject())
        {
            var date = DateOnly.ParseExact(day.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var left = ReadInt(day.Value);
            var price = charges.TryGetProperty(day.Name, out var c) && c.TryGetProperty("PSN_CHG_ADULT", out var p) ? ReadInt(p) : 0;
            result[date] = (left, price);
        }
        return result;
    }

    private static int ReadInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetInt32(),
        JsonValueKind.String when int.TryParse(e.GetString(), CultureInfo.InvariantCulture, out var v) => v,
        _ => 0,
    };

    private async Task<string> PostFormStringAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.apahotel.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.apahotel.com/");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        return await SendStringAsync(req, ct);
    }

    private async Task<string> SendStringAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var bytes = await SendBytesAsync(req, ct);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> SendBytesAsync(HttpRequestMessage req, CancellationToken ct)
    {
        await Throttle.WaitAsync(ct);
        try
        {
            using var res = await Http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Forbidden)
                throw new McpException("アパホテルのサイトにアクセスを拒否されました (403)。時間をおいて再試行してください。");
            if (!res.IsSuccessStatusCode)
                throw new McpException($"アパホテルのサイトがエラーを返しました: {(int)res.StatusCode} {req.RequestUri}");
            return await res.Content.ReadAsByteArrayAsync(ct);
        }
        finally
        {
            Throttle.Release();
        }
    }

    private static string Clean(string? s) => WhitespaceRegex().Replace(s ?? "", " ").Trim();

    [GeneratedRegex("""(?:ehotelid="|req_econ_hotel_id" value=")(\d+)""")]
    private static partial Regex EconIdRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
