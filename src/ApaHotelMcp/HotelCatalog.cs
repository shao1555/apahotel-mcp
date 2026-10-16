using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using Microsoft.Extensions.Logging;

namespace ApaHotelMcp;

/// <summary>
/// ホテルマスタ（/json/hotel.json）と econ_hotel_id の対応表をキャッシュする。
/// アパ直提携ホテル（partner / partner_plus）は対象外とし、直営（company）と FC（franchise）のみ扱う。
/// </summary>
public sealed class HotelCatalog(ApaClient client, ILogger<HotelCatalog> logger)
{
    private static readonly TimeSpan MasterTtl = TimeSpan.FromHours(24);
    private static readonly string[] TargetAttributes = ["company", "franchise"];

    private static readonly string CacheDir = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "apahotel-mcp");
    private static readonly string MasterCachePath = Path.Combine(CacheDir, "hotel.json");
    private static readonly string EconIdCachePath = Path.Combine(CacheDir, "econ-ids.json");

    private readonly SemaphoreSlim _loadLock = new(1);
    private IReadOnlyList<Hotel>? _hotels;
    private DateTimeOffset _loadedAt;
    private ConcurrentDictionary<int, int>? _econIds;

    public async Task<IReadOnlyList<Hotel>> GetHotelsAsync(CancellationToken ct)
    {
        if (_hotels is not null && DateTimeOffset.UtcNow - _loadedAt < MasterTtl) return _hotels;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_hotels is not null && DateTimeOffset.UtcNow - _loadedAt < MasterTtl) return _hotels;

            byte[] bytes;
            var cache = new FileInfo(MasterCachePath);
            if (cache.Exists && DateTime.UtcNow - cache.LastWriteTimeUtc < MasterTtl)
            {
                bytes = await File.ReadAllBytesAsync(MasterCachePath, ct);
            }
            else
            {
                logger.LogInformation("ホテルマスタを取得します");
                bytes = await client.GetHotelMasterJsonAsync(ct);
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(MasterCachePath, bytes, ct);
            }

            _hotels = ParseMaster(bytes);
            _loadedAt = DateTimeOffset.UtcNow;
            return _hotels;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static List<Hotel> ParseMaster(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var hotels = new List<Hotel>();
        foreach (var h in doc.RootElement.EnumerateArray())
        {
            string S(string name) => h.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

            if (!TargetAttributes.Contains(S("hotel-hotel_attribute"))) continue;
            if (S("hotel-publish_status") != "open") continue;
            if (!int.TryParse(S("hotel-hotel_id"), CultureInfo.InvariantCulture, out var id)) continue;

            hotels.Add(new Hotel(
                HotelId: id,
                HotelNo: int.TryParse(S("hotel-hotel_no"), CultureInfo.InvariantCulture, out var no) ? no : 0,
                Name: S("hotel-hotel_name"),
                Prefecture: S("hotel-pref_name"),
                AreaId: int.TryParse(S("hotelArea-area_id"), CultureInfo.InvariantCulture, out var a) ? a : 0,
                AreaName: S("hotelArea-area_name"),
                Address: S("hotel-pref_name") + S("hotel-address01") + S("hotel-address02") + S("hotel-address03"),
                CheckIn: S("hotel-ci"),
                CheckOut: S("hotel-co"),
                // リゾート等は独自ページ（external_url）側にのみ予約 ID が載っている
                Url: S("hotel-external_url") is { Length: > 0 } ext ? ext : S("hotel-url"),
                Attribute: S("hotel-hotel_attribute")));
        }
        return hotels;
    }

    public async Task<IReadOnlyList<Hotel>> SearchAsync(string? keyword, string? prefecture, string? area, CancellationToken ct)
    {
        IEnumerable<Hotel> q = await GetHotelsAsync(ct);
        if (!string.IsNullOrWhiteSpace(prefecture))
        {
            var p = Normalize(prefecture).TrimEnd('都', '道', '府', '県');
            q = q.Where(h => Normalize(h.Prefecture).StartsWith(p, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(area))
        {
            if (int.TryParse(area, CultureInfo.InvariantCulture, out var areaId))
                q = q.Where(h => h.AreaId == areaId);
            else
            {
                var a = Normalize(area);
                q = q.Where(h => Normalize(h.AreaName).Contains(a, StringComparison.Ordinal));
            }
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            // 空白区切りの AND 検索。名前・住所・エリア名・URL を対象にする
            var terms = keyword.Split((char[])[' ', '　'], StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
            q = q.Where(h =>
            {
                var hay = Normalize($"{h.Name} {h.Address} {h.AreaName} {h.Url}");
                return terms.All(t => hay.Contains(t, StringComparison.Ordinal));
            });
        }
        return q.ToList();
    }

    /// <summary>hotel_id（数値）またはホテル名の一部から 1 軒に特定する。</summary>
    public async Task<Hotel> ResolveAsync(string hotel, CancellationToken ct)
    {
        var all = await GetHotelsAsync(ct);
        if (int.TryParse(hotel.Trim(), CultureInfo.InvariantCulture, out var id))
            return all.FirstOrDefault(h => h.HotelId == id)
                ?? throw new McpException($"hotel_id={id} のホテルは見つかりません（アパ直提携ホテルは対象外です）。");

        var key = Normalize(hotel);
        var exact = all.Where(h => Normalize(h.Name) == key).ToList();
        if (exact.Count == 1) return exact[0];

        var matches = await SearchAsync(hotel, null, null, ct);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new McpException($"「{hotel}」に一致するホテルが見つかりません。search_hotels で検索してください。"),
            _ => throw new McpException(
                $"「{hotel}」に一致するホテルが {matches.Count} 件あります。hotel_id で指定してください:\n" +
                string.Join("\n", matches.Take(20).Select(h => $"- {h.HotelId}: {h.Name}"))),
        };
    }

    public async Task<int> GetEconHotelIdAsync(Hotel hotel, CancellationToken ct)
    {
        var ids = await LoadEconIdsAsync(ct);
        if (ids.TryGetValue(hotel.HotelId, out var econ)) return econ;

        econ = await client.FetchEconHotelIdAsync(hotel.Url, ct)
            ?? throw new McpException($"{hotel.Name} の予約 ID をホテルページから取得できませんでした: {hotel.Url}");
        ids[hotel.HotelId] = econ;
        await SaveEconIdsAsync(ids, ct);
        return econ;
    }

    private async Task<ConcurrentDictionary<int, int>> LoadEconIdsAsync(CancellationToken ct)
    {
        if (_econIds is not null) return _econIds;
        var dict = new ConcurrentDictionary<int, int>();
        if (File.Exists(EconIdCachePath))
        {
            try
            {
                await using var fs = File.OpenRead(EconIdCachePath);
                var saved = await JsonSerializer.DeserializeAsync<Dictionary<int, int>>(fs, cancellationToken: ct);
                foreach (var (k, v) in saved ?? []) dict[k] = v;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "econ_hotel_id キャッシュが壊れているため破棄します");
            }
        }
        return _econIds ??= dict;
    }

    private readonly SemaphoreSlim _saveLock = new(1);

    private async Task SaveEconIdsAsync(ConcurrentDictionary<int, int> ids, CancellationToken ct)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(CacheDir);
            var tmp = EconIdCachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(new SortedDictionary<int, int>(ids)), ct);
            File.Move(tmp, EconIdCachePath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>全角半角・大文字小文字・空白・括弧の揺れを吸収する。</summary>
    internal static string Normalize(string s)
    {
        var n = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
        {
            if (char.IsWhiteSpace(c) || c is '〈' or '〉' or '<' or '>' or '(' or ')' or '「' or '」' or '・') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
