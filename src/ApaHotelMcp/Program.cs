using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApaHotelMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdio transport では stdout が JSON-RPC 専用なので、ログはすべて stderr に出す
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services
    .AddHttpClient(ApaClient.HttpClientName, http =>
    {
        http.BaseAddress = new Uri("https://www.apahotel.com/");
        http.Timeout = TimeSpan.FromSeconds(30);
        // Akamai は素の HttpClient の既定ヘッダを弾くため、ブラウザ相当のヘッダを付ける
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ja,en;q=0.9");
        http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"141\", \"Not?A_Brand\";v=\"8\"");
        http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"macOS\"");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        CookieContainer = new CookieContainer(),
        UseCookies = true,
        AllowAutoRedirect = true,
    });

builder.Services.AddSingleton<ApaClient>();
builder.Services.AddSingleton<HotelCatalog>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        // 日本語を \uXXXX にエスケープするとトークンを無駄に消費するのでそのまま出す
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    });

await builder.Build().RunAsync();
