using Microsoft.Extensions.DependencyInjection;
using Amazon.Lambda.Core;
using OptionChain;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using OptionChain.Models;
using System.Text.Json;
using System.Data;
using System.Text.Json.Serialization;
using System.Text;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace aws_session_sync_net8;

public class LambdaInput
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; }

    [JsonPropertyName("user_id")]
    public string UserId { get; set; }

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; }

    [JsonPropertyName("expires_at")]
    public Int64 ExpiresAt { get; set; }

    [JsonPropertyName("issued_at")]
    public Int64 IssuedAt { get; set; }

    [JsonPropertyName("message_type")]
    public string MessageType { get; set; }
}

public class Function
{
    private readonly UpStoxDbContext _upStoxDbContext;
    private static readonly HttpClient _httpClient = new HttpClient();
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Constructor injection
    public Function(UpStoxDbContext upStoxDbContext)
    {
        _upStoxDbContext = upStoxDbContext;
    }

    /// <summary>
    /// A simple function that takes a string and does a ToUpper
    /// </summary>
    /// <param name="input">The event for the Lambda function handler to process.</param>
    /// <param name="context">The ILambdaContext that provides methods for logging and describing the Lambda environment.</param>
    /// <returns></returns>
    public string FunctionHandler(LambdaInput input, ILambdaContext context)
    {
        if (input != null && input.MessageType == "access_token")
        {
            var authDetails = _upStoxDbContext.AuthDetails.Where(x => x.Id == 1).FirstOrDefault();
            if (authDetails != null)
            {
                authDetails.AccessToken = input.AccessToken;
                authDetails.ModifiedDate = DateTime.Now;
                _upStoxDbContext.SaveChanges();
                return "Access token updated successfully.";
            }
            return "Invalid access token";
        }
        else if (input != null && input.MessageType == "request_access_token")
        {
            if (RequestForAccessToken())
            {
                return "Request sent successfully for access token. Please approve it fro what's app.";
            }
            else
                return "Failed to send the request for access token.";
        }
        else if(input != null && input.MessageType == "precomputed_data")
        {
            return PreComputedData();
        }
        else
        {
            string accessToken = GetAccessToken();
            bool success = GetMarketUpdate(accessToken);
            return success ? "Market data updated successfully" : "No data to update";
        }
    }

    public string PreComputedData()
    {
        try
        {
            const int NO_OF_DAYS = 10;
            var preCompuerDataList = new List<PreComputedData>();

            var firstRow = _upStoxDbContext.OHLCs
                .AsNoTracking()
                .Where(x => x.StockMetaDataId == 1)
                .Select(x => x.CreatedDate)
                .Distinct()
                .OrderByDescending(x => x)
                .Take(NO_OF_DAYS)
                .ToList();

            var equityStocks = _upStoxDbContext.MarketMetaDatas
                .AsNoTracking()
                .Select(x => new { x.Id, x.Name })
                .ToList();

            // Take the last date to filter the values
            var startDate = firstRow.Last();
            var previousDate = firstRow.ElementAt(0);

            foreach (var stock in equityStocks)
            {
                decimal daysHigh = 0, daysLow = 0, daysAverageClose = 0, previousDayHigh = 0, previousDayLow = 0, previousDayClose = 0;
                long daysAverageVolume = 0;

                var ohlcData = _upStoxDbContext.OHLCs
                    .AsNoTracking()
                    .Where(x => x.StockMetaDataId == stock.Id && x.CreatedDate >= startDate)
                    .ToList();

                if (ohlcData.Count > 0)
                {
                    daysHigh = ohlcData.Max(x => x.High);
                    daysLow = ohlcData.Min(x => x.Low);
                    daysAverageClose = ohlcData.Average(x => x.Close);
                    daysAverageVolume = (long)ohlcData.Average(x => x.Volume);
                }

                var previousOHLCData = ohlcData.Where(x => x.CreatedDate == previousDate).ToList();

                if (previousOHLCData.Count > 0)
                {
                    previousDayHigh = previousOHLCData.Max(x => x.High);
                    previousDayLow = previousOHLCData.Min(x => x.Low);
                    previousDayClose = previousOHLCData.OrderByDescending(x => x.Time).FirstOrDefault()?.LastPrice ?? 0;
                }
                var precomputedValue = new PreComputedData
                {
                    CreatedDate = DateTime.Now.Date,
                    DaysHigh = daysHigh,
                    DaysLow = daysLow,
                    DaysAverageClose = daysAverageClose,
                    DaysAverageVolume = daysAverageVolume,
                    DaysAboveVWAPPercentage = 0,
                    DaysATR = 0,
                    DaysAverageBodySize = 0,
                    DaysGreenPercentage = 0,
                    DaysHighLowRangePercentage = 0,
                    DaysMedianATR = 0,
                    DaysStdDevClose = 0,
                    DaysStdDevVolume = 0,
                    DaysTrendScore = 0,
                    DaysVWAP = 0,
                    StockMetaDataId = stock.Id,
                    PreviousDayHigh = previousDayHigh,
                    PreviousDayClose = previousDayClose,
                    PreviousDayLow = previousDayLow
                };

                preCompuerDataList.Add(precomputedValue);
            }

            _upStoxDbContext.PreComputedDatas.AddRange(preCompuerDataList);
            _upStoxDbContext.SaveChanges();

            return "Precomputed data captured/updated successfully.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public bool RequestForAccessToken()
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("accept", "application/json");

        string url = "https://api.upstox.com/v3/login/auth/token/request/98671b41-bd9d-4fa7-beea-04ff53e17868";

        var data = new
        {
            client_secret = "xh5mi6vity"
        };

        var json = JsonSerializer.Serialize(data);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = _httpClient.PostAsync(url, content).GetAwaiter().GetResult();

        if (response.IsSuccessStatusCode)
            return true;
        else
            return false;
    }

    public string GetAccessToken()
    {
        var authDetail = _upStoxDbContext.AuthDetails.AsNoTracking().Where(x => x.Id == 1).FirstOrDefault();

        return authDetail?.AccessToken ?? throw new Exception("Invalid access token");
    }

    public bool GetMarketUpdate(string accessToken)
    {
        try
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var marketMetaData = _upStoxDbContext.MarketMetaDatas.AsNoTracking().ToList();
            var stockNameWithKey = marketMetaData.ToDictionary(x => x.Name, x => x.Id);

            var instrumentKey = string.Join(",", marketMetaData.Select(x => x.InstrumentToken));

            // API endpoint (you can dynamically change symbols if needed), NSE_EQ|INE040A01034,NSE_EQ|INE062A01020
            string url = "https://api.upstox.com/v3/market-quote/ohlc?instrument_key=" + instrumentKey + "&interval=I1";

            // Make GET request
            HttpResponseMessage response = _httpClient.GetAsync(url).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                string error = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new Exception($"Upstox API failed ({response.StatusCode}): {error}");
            }

            string jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            ApiResponse apiResponse = JsonSerializer.Deserialize<ApiResponse>(jsonResponse, _jsonOptions) ?? new ApiResponse();

            return AddMarketDataEFCore(apiResponse, stockNameWithKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetMarketUpdate: {ex.Message}");
            return false;
        }
    }

    public bool AddMarketDataEFCore(ApiResponse apiResponse, Dictionary<string, long> marketMetaDatas)
    {
        var prevOhlcList = new List<OHLC>();

        if (apiResponse.Data == null && apiResponse.Status != "success")
            return false;

        // Get Previous day lastprice or closing price
        var findLastDate = _upStoxDbContext.OHLCs
            .AsNoTracking()
            .Where(x => x.Time == new TimeSpan(15, 29, 0))
            .OrderByDescending(x=>x.Id)
            .FirstOrDefault();

        var previousCloseStockCollection = _upStoxDbContext.OHLCs
            .AsNoTracking()
            .Where(x => x.CreatedDate != null
                && findLastDate != null
                && findLastDate.CreatedDate != null
                && x.CreatedDate.Value.Date == findLastDate.CreatedDate.Value.Date
                && x.Time == new TimeSpan(15, 29, 0))
            .ToList(); // this will fetch 235 stocks/index details

        foreach (var item in apiResponse.Data)
        {
            var instrumentKey = item.Key;
            marketMetaDatas.TryGetValue(instrumentKey, out var stockMetaDataId);

            // calculate the pChange for each stock based on lastprice or closeprice
            var stockDetails = previousCloseStockCollection.Where(x => x.StockMetaDataId == stockMetaDataId).FirstOrDefault();

            var previousClose = stockDetails?.LastPrice ?? stockDetails?.Close ?? 0;

            if(DateTime.Now.Hour == 15 && DateTime.Now.Minute == 30)
            {
                var pChange = ((((item.Value?.LastPrice ?? item.Value.LiveOhlc.Close) - previousClose) * 100) / previousClose);

                prevOhlcList.Add(new OHLC
                {
                    StockMetaDataId = stockMetaDataId,
                    Open = item.Value?.LiveOhlc.Open ?? 0,
                    High = item.Value?.LiveOhlc.High ?? 0,
                    Low = item.Value?.LiveOhlc.Low ?? 0,
                    Close = item.Value?.LiveOhlc.Close ?? 0,
                    Volume = item.Value?.LiveOhlc.Volume ?? 0,
                    Timestamp = item.Value?.LiveOhlc.Timestamp ?? 0,
                    LastPrice = item.Value?.LastPrice ?? 0,
                    CreatedDate = DateTime.Now.Date,
                    Time = new TimeSpan(DateTime.Now.Hour, DateTime.Now.Minute - 1, 0),
                    PChange = pChange
                });
            } else
            {
                var pChange = ((((item.Value?.LastPrice ?? item.Value.PrevOhlc.Close) - previousClose) * 100) / previousClose);

                prevOhlcList.Add(new OHLC
                {
                    StockMetaDataId = stockMetaDataId,
                    Open = item.Value?.PrevOhlc.Open ?? 0,
                    High = item.Value?.PrevOhlc.High ?? 0,
                    Low = item.Value?.PrevOhlc.Low ?? 0,
                    Close = item.Value?.PrevOhlc.Close ?? 0,
                    Volume = item.Value?.PrevOhlc.Volume ?? 0,
                    Timestamp = item.Value?.PrevOhlc.Timestamp ?? 0,
                    LastPrice = item.Value?.LastPrice ?? 0,
                    CreatedDate = DateTime.Now.Date,
                    Time = new TimeSpan(DateTime.Now.Hour, DateTime.Now.Minute - 1, 0),
                    PChange = pChange
                });
            }
        }

        if (prevOhlcList?.Count == 0)
            return false;

        _upStoxDbContext.OHLCs.AddRange(prevOhlcList);
        _upStoxDbContext.SaveChanges();

        return true;
    }
}

public class LambdaEntryPoint
{
    private static IServiceProvider _serviceProvider;

    static LambdaEntryPoint()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddDbContext<UpStoxDbContext>(x => x.UseSqlServer("Data Source=190.92.174.111;Initial Catalog=karmajew_optionchain;User Id=karmajew_sa;Password=Prokyonz@2023;TrustServerCertificate=True"));
        //services.AddDbContext<UpStoxDbContext>(x => x.UseSqlServer("Data Source=DESKTOP-PKUGHDC\\SQLEXPRESS;Initial Catalog=smarttrader;User Id=sa;Password=Janver@1234;TrustServerCertificate=True;Connect Timeout=200;"));
        services.AddTransient<Function>();
        services.AddMemoryCache();
    }

    public static string Handler(LambdaInput input, ILambdaContext context)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var function = scope.ServiceProvider.GetRequiredService<Function>();
            return function.FunctionHandler(input, context);
        }
    }
}