using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace ApaHotelMcp;

[McpServerToolType]
public sealed class ApaHotelTools(HotelCatalog catalog, ApaClient client, ILogger<ApaHotelTools> logger)
{
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    [McpServerTool(Name = "list_areas", ReadOnly = true, Idempotent = true)]
    [Description("アパホテル（直営・FC）の都道府県とエリアの一覧を、ホテル数つきで返す。search_vacancies の area 指定に使う。")]
    public async Task<object> ListAreas(
        [Description("都道府県で絞り込む（例: 東京都, 大阪）。省略時は全国")] string? prefecture = null,
        CancellationToken ct = default)
    {
        var hotels = await catalog.SearchAsync(null, prefecture, null, ct);
        return hotels
            .GroupBy(h => h.Prefecture)
            .Select(p => new
            {
                prefecture = p.Key,
                hotel_count = p.Count(),
                areas = p.GroupBy(h => (h.AreaId, h.AreaName))
                    .OrderBy(a => a.Key.AreaId)
                    .Select(a => new { area_id = a.Key.AreaId, area_name = a.Key.AreaName, hotel_count = a.Count() }),
            });
    }

    [McpServerTool(Name = "search_hotels", ReadOnly = true, Idempotent = true)]
    [Description("アパホテル（直営・FC。アパ直提携ホテルは除く）をホテル名・住所・エリアで検索する。")]
    public async Task<object> SearchHotels(
        [Description("キーワード（空白区切りで AND）。ホテル名・住所・エリア名に部分一致。例: 秋葉原, 新宿 歌舞伎町")] string? keyword = null,
        [Description("都道府県（例: 東京都）")] string? prefecture = null,
        [Description("エリア ID またはエリア名の一部（list_areas 参照）")] string? area = null,
        [Description("最大件数")] int limit = 50,
        CancellationToken ct = default)
    {
        var hotels = await catalog.SearchAsync(keyword, prefecture, area, ct);
        return new
        {
            total = hotels.Count,
            hotels = hotels.Take(Math.Clamp(limit, 1, 300)).Select(ToSummary),
        };
    }

    [McpServerTool(Name = "get_hotel_prices", ReadOnly = true, Idempotent = true)]
    [Description("""
        1 軒のアパホテルについて、指定日程の空室と料金（税込・全泊・全室の合計）を公式サイトから取得する。
        既定では素泊まりプランのうち最安の部屋 1 件を返す。満室の部屋タイプは返らない。
        結果を利用者に示すときは reserve_url（日程・泊数・人数・部屋を指定済みの予約画面）と
        calendar_url（公式サイトの料金カレンダー）をリンクとして添えること。
        """)]
    public async Task<object> GetHotelPrices(
        [Description("hotel_id（search_hotels の結果）またはホテル名の一部")] string hotel,
        [Description("チェックイン日 (YYYY-MM-DD)")] string checkin,
        [Description("泊数 (1-14, 30)")] int nights = 1,
        [Description("1 室あたりの大人の人数 (1-6)")] int adults = 1,
        [Description("部屋数 (1-10)")] int rooms = 1,
        [Description("食事条件: room_only（素泊まり・既定） / breakfast / two_meals / dinner / any")] string meal = "room_only",
        [Description("true なら最安 1 件ではなく、予約可能な全プラン × 部屋タイプを安い順に返す")] bool all_plans = false,
        CancellationToken ct = default)
    {
        var stay = ValidateStay(checkin, nights, adults, rooms);
        var mealType = MealTypeExtensions.Parse(meal);
        var h = await catalog.ResolveAsync(hotel, ct);
        var prices = await FetchPricesAsync(h, stay, mealType, ct);

        return new
        {
            hotel = ToSummary(h),
            stay = StayInfo(stay, mealType),
            available = prices.Count > 0,
            cheapest = prices.Count > 0 ? ToPrice(h, prices[0], stay) : null,
            options = all_plans ? prices.Select(p => ToPrice(h, p, stay)) : null,
            note = prices.Count == 0 ? "指定条件で予約可能な部屋がありません（満室または販売期間外）。" : null,
        };
    }

    [McpServerTool(Name = "search_vacancies", ReadOnly = true, Idempotent = true)]
    [Description("""
        エリア・都道府県・キーワードで絞り込んだ複数のアパホテルについて、指定日程の空室と最安料金を調べ、安い順に返す。
        既定では素泊まりプランの最安部屋で比較する。1 軒ごとに公式サイトへ問い合わせるため、対象ホテル数は max_hotels までに絞ること。
        結果を利用者に示すときは各ホテルの reserve_url（予約画面）と calendar_url（料金カレンダー）をリンクとして添えること。
        """)]
    public async Task<object> SearchVacancies(
        [Description("チェックイン日 (YYYY-MM-DD)")] string checkin,
        [Description("泊数 (1-14, 30)")] int nights = 1,
        [Description("1 室あたりの大人の人数 (1-6)")] int adults = 1,
        [Description("部屋数 (1-10)")] int rooms = 1,
        [Description("都道府県（例: 東京都）")] string? prefecture = null,
        [Description("エリア ID またはエリア名の一部（例: 21, 秋葉原）")] string? area = null,
        [Description("キーワード（ホテル名・住所に部分一致、空白区切りで AND）")] string? keyword = null,
        [Description("食事条件: room_only（素泊まり・既定） / breakfast / two_meals / dinner / any")] string meal = "room_only",
        [Description("問い合わせるホテル数の上限。超える場合はエラーになるので条件を絞る")] int max_hotels = 30,
        CancellationToken ct = default)
    {
        var stay = ValidateStay(checkin, nights, adults, rooms);
        var mealType = MealTypeExtensions.Parse(meal);
        if (string.IsNullOrWhiteSpace(prefecture) && string.IsNullOrWhiteSpace(area) && string.IsNullOrWhiteSpace(keyword))
            throw new McpException("prefecture / area / keyword のいずれかを指定してください。");

        var hotels = await catalog.SearchAsync(keyword, prefecture, area, ct);
        if (hotels.Count == 0) throw new McpException("条件に一致するホテルがありません。");
        if (hotels.Count > max_hotels)
            throw new McpException($"対象ホテルが {hotels.Count} 軒あり max_hotels={max_hotels} を超えています。area や keyword で絞り込むか max_hotels を増やしてください。");

        var results = await Task.WhenAll(hotels.Select(async h =>
        {
            try
            {
                var prices = await FetchPricesAsync(h, stay, mealType, ct);
                return (Hotel: h, Cheapest: prices.FirstOrDefault(), Error: (string?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "{Hotel} の取得に失敗", h.Name);
                return (Hotel: h, Cheapest: (PlanRoomPrice?)null, Error: ex.Message);
            }
        }));

        return new
        {
            stay = StayInfo(stay, mealType),
            checked_hotels = results.Length,
            available = results
                .Where(r => r.Cheapest is not null)
                .OrderBy(r => r.Cheapest!.TotalPrice)
                .Select(r => new
                {
                    hotel_id = r.Hotel.HotelId,
                    name = r.Hotel.Name,
                    address = r.Hotel.Address,
                    cheapest = ToPrice(r.Hotel, r.Cheapest!, stay),
                }),
            sold_out = results
                .Where(r => r.Cheapest is null && r.Error is null)
                .Select(r => new { hotel_id = r.Hotel.HotelId, name = r.Hotel.Name }),
            errors = results
                .Where(r => r.Error is not null)
                .Select(r => new { hotel_id = r.Hotel.HotelId, name = r.Hotel.Name, error = r.Error }),
        };
    }

    [McpServerTool(Name = "get_price_calendar", ReadOnly = true, Idempotent = true)]
    [Description("""
        1 軒のアパホテルの 1 か月分の日別空室状況と 1 泊料金（税込）を返す。安い日を探すのに使う。
        既定では素泊まりプランを使い、日ごとに空きのある最安の部屋タイプの料金を返す。
        結果を利用者に示すときは calendar_url（公式サイトの同じプランの料金カレンダー）をリンクとして添えること。
        """)]
    public async Task<object> GetPriceCalendar(
        [Description("hotel_id またはホテル名の一部")] string hotel,
        [Description("対象月 (YYYY-MM)")] string month,
        [Description("1 室あたりの大人の人数 (1-6)")] int adults = 1,
        [Description("プラン ID。省略時は素泊まりプラン")] int? plan_id = null,
        CancellationToken ct = default)
    {
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            throw new McpException("month は YYYY-MM 形式で指定してください。");
        if (adults is < 1 or > 6) throw new McpException("adults は 1〜6 で指定してください。");

        var h = await catalog.ResolveAsync(hotel, ct);
        var econ = await catalog.GetEconHotelIdAsync(h, ct);
        var plans = await client.GetPlanMasterAsync(econ, adults, ct);
        if (plans.Count == 0) throw new McpException($"{h.Name} に {adults} 名で予約できるプランがありません。");

        // 既定は「素泊まりプラン」、無ければ名前に素泊まりを含む中で最も短い（=装飾の少ない基本的な）プラン
        var candidates = plan_id is { } pid
            ? plans.Where(p => p.PlanId == pid)
            : plans.Where(p => p.PlanName == "素泊まりプラン")
                .Concat(plans.Where(p => p.PlanName.Contains("素泊まり", StringComparison.Ordinal)).OrderBy(p => p.PlanName.Length))
                .Append(plans[0]);
        if (!candidates.Any())
            throw new McpException($"plan_id={plan_id} は存在しません。選択可能: " +
                string.Join(", ", plans.Select(p => $"{p.PlanId}={p.PlanName}")));
        var plan = candidates.First();

        var perRoom = await Task.WhenAll(plan.RoomTypes.Select(async rt =>
            (RoomType: rt, Days: await client.GetMonthCalendarAsync(econ, plan.PlanId, rt.TypeId, first.Year, first.Month, adults, ct))));

        var today = Today();
        var days = Enumerable.Range(0, DateTime.DaysInMonth(first.Year, first.Month))
            .Select(first.AddDays)
            .Where(d => d >= today)
            .Select(d =>
            {
                var best = perRoom
                    .Select(r => (r.RoomType, Info: r.Days.TryGetValue(d, out var i) ? i : (RoomsLeft: 0, PricePerAdult: 0)))
                    .Where(x => x.Info.RoomsLeft > 0 && x.Info.PricePerAdult > 0)
                    .OrderBy(x => x.Info.PricePerAdult)
                    .FirstOrDefault();
                return best.RoomType.TypeName is null
                    ? new CalendarDay(d.ToString("yyyy-MM-dd ddd", JaCulture), false, null, null, null, null)
                    : new CalendarDay(d.ToString("yyyy-MM-dd ddd", JaCulture), true, best.Info.RoomsLeft,
                        best.Info.PricePerAdult, best.Info.PricePerAdult * adults, best.RoomType.TypeName);
            })
            .ToList();

        return new
        {
            hotel = ToSummary(h),
            plan = new { plan.PlanId, plan.PlanName },
            adults,
            calendar_url = CalendarUrl(h, plan.PlanId),
            note = "料金は 1 室 1 泊あたり（税込、会員割引なし）。rooms_left は 10 以上だと実数より丸められている場合がある。連泊時の合計は get_hotel_prices で確認すること。",
            other_plans = plans.Where(p => p.PlanId != plan.PlanId).Select(p => new { p.PlanId, p.PlanName }),
            days,
        };
    }

    private sealed record Stay(DateOnly CheckIn, int Nights, int Adults, int Rooms);

    private async Task<List<PlanRoomPrice>> FetchPricesAsync(Hotel h, Stay stay, MealType meal, CancellationToken ct)
    {
        var econ = await catalog.GetEconHotelIdAsync(h, ct);
        var prices = await client.GetPlanListAsync(h, econ, stay.CheckIn, stay.Nights, stay.Adults, stay.Rooms, meal, ct);
        // 同額ならサイトの表示順（基本プランが先頭）を優先する
        return prices.Select((p, i) => (p, i)).OrderBy(x => x.p.TotalPrice).ThenBy(x => x.i).Select(x => x.p).ToList();
    }

    private static Stay ValidateStay(string checkin, int nights, int adults, int rooms)
    {
        if (!DateOnly.TryParseExact(checkin, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw new McpException("checkin は YYYY-MM-DD 形式で指定してください。");
        if (d < Today()) throw new McpException($"checkin が過去の日付です（本日: {Today():yyyy-MM-dd}）。");
        if (nights is not (>= 1 and <= 14 or 30)) throw new McpException("nights は 1〜14 または 30 で指定してください。");
        if (adults is < 1 or > 6) throw new McpException("adults は 1〜6 で指定してください。");
        if (rooms is < 1 or > 10) throw new McpException("rooms は 1〜10 で指定してください。");
        return new Stay(d, nights, adults, rooms);
    }

    /// <summary>サイトと同じく、午前 5 時までは前日扱い。</summary>
    private static DateOnly Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Jst).AddHours(-5).DateTime);

    private static readonly CultureInfo JaCulture = CultureInfo.GetCultureInfo("ja-JP");

    private static object ToSummary(Hotel h) => new
    {
        hotel_id = h.HotelId,
        name = h.Name,
        prefecture = h.Prefecture,
        area = h.AreaName,
        address = h.Address,
        check_in = h.CheckIn,
        check_out = h.CheckOut,
        url = h.Url,
    };

    private static object StayInfo(Stay s, MealType meal) => new
    {
        checkin = s.CheckIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        checkout = s.CheckIn.AddDays(s.Nights).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        nights = s.Nights,
        adults_per_room = s.Adults,
        rooms = s.Rooms,
        meal = meal switch
        {
            MealType.RoomOnly => "room_only",
            MealType.Breakfast => "breakfast",
            MealType.TwoMeals => "two_meals",
            MealType.Dinner => "dinner",
            _ => "any",
        },
    };

    /// <summary>
    /// 公式サイトの料金カレンダー。URL で指定できるのはプランだけで、人数は 1 名・表示月は当月・泊数は 1 から始まる。
    /// target は値によらず「1 名で開く」フラグとして扱われる。
    /// </summary>
    private static string CalendarUrl(Hotel h, int planId) =>
        $"{h.Url.TrimEnd('/')}/calendar/?target=1&planid={planId}";

    private static object ToPrice(Hotel h, PlanRoomPrice p, Stay s) => new
    {
        plan_id = p.PlanId,
        plan_name = p.PlanName,
        room_type_id = p.RoomTypeId,
        room_type = p.RoomTypeName,
        total_price = p.TotalPrice,
        price_per_night = (int)Math.Round((double)p.TotalPrice / s.Nights),
        currency = "JPY",
        reserve_url = p.ReserveUrl,
        calendar_url = CalendarUrl(h, p.PlanId),
        calendar_note = "料金カレンダーは 1 名・当月・1 泊の表示で開く。泊数・人数・月はページ上で変更する。",
    };
}
