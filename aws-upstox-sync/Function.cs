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
using OptionChain.Migrations.UpStoxDb;

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
    private readonly IDbContextFactory<UpStoxDbContext> _upStoxDbContext;
    private static readonly HttpClient _httpClient = new HttpClient();
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static List<DateTime> Holidays = new List<DateTime>
    {
        new DateTime(2025, 11, 05, 0, 0, 0, DateTimeKind.Local),
        new DateTime(2025, 12, 25, 0, 0, 0, DateTimeKind.Local),
    };

    // Constructor injection
    public Function(IDbContextFactory<UpStoxDbContext> upStoxDbContext)
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
        if (Holidays.Contains(DateTime.UtcNow.Date)) return "Today is holiday";

        if (input != null && input.MessageType == "access_token")
        {
            var db = _upStoxDbContext.CreateDbContext();

            var authDetails = db.AuthDetails.Where(x => x.Id == 1).FirstOrDefault();
            if (authDetails != null)
            {
                authDetails.AccessToken = input.AccessToken;
                authDetails.ModifiedDate = DateTime.Now;
                db.SaveChanges();
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
        else if (input != null && input.MessageType == "precomputed_data")
        {
            return PreComputedData();
        }
        else
        {
            string accessToken = GetAccessToken();

            var marketTask = Task.Run(() => GetMarketUpdate(accessToken));
            var optionTask = Task.Run(() => GetOptionExpiryData(accessToken));

            Task.WaitAll(marketTask, optionTask);

            var marketDataResult = marketTask.Result;
            var optionDataResult = optionTask.Result;

            string message = "";

            if (marketDataResult.Item1)
                message += "Market data updated successfully.";
            else
                message += marketDataResult.Item2;

            if (optionDataResult.Item1)
                message += " Option data updated successfully.";
            else
                message += optionDataResult.Item2;

            return message;
        }
    }

    public string PreComputedData()
    {
        try
        {
            var db = _upStoxDbContext.CreateDbContext();

            const int NO_OF_DAYS = 10;
            var preCompuerDataList = new List<PreComputedData>();
            var futurePrecomputedDatas = new List<FuturePreComputedData>();

            var allDatesInTable = db.OHLCs
                .AsNoTracking()
                .Where(x => x.StockMetaDataId == 1)
                .Select(x => x.CreatedDate)
                .Distinct()
                .OrderByDescending(x => x)
                .ToList();

            var top10Days = allDatesInTable.Take(NO_OF_DAYS).ToList();

            var equityStocks = db.MarketMetaDatas
                .AsNoTracking()
                .Select(x => new { x.Id, x.Name })
                .ToList();

            // Take the last date to filter the values
            var startDate = top10Days.Last();
            var previousDate = top10Days.ElementAt(0); // Once the End of day this function execute todays date becomes the previousDate

            // Delete the existing records and re-insert the newly calculated.
            db.FuturePreComputedDatas
                .Where(x => x.CreatedDate == DateTime.Today)
                .ExecuteDelete();

            db.PreComputedDatas
                .Where(x => x.CreatedDate == DateTime.Today)
                .ExecuteDelete();

            foreach (var stock in equityStocks)
            {
                decimal daysHigh = 0, daysLow = 0, daysAverageClose = 0, previousDayHigh = 0, previousDayLow = 0, previousDayClose = 0, pivotPoint = 0, bottomCP = 0, topCP = 0;
                long daysAverageVolume = 0, daysStdDevVolume = 0;

                // last 10 days data of a specific stock/index
                var ohlcData = db.OHLCs
                    .AsNoTracking()
                    .Where(x => x.StockMetaDataId == stock.Id && x.CreatedDate >= startDate)
                    .ToList();

                if (ohlcData.Count > 0)
                {
                    // calculate the DaysStdDevVolume
                    var daysVolumes = ohlcData
                        .Select(x => x.Volume)
                        .ToList();

                    if (daysVolumes.Count < 2)
                        daysStdDevVolume = 0;

                    daysStdDevVolume = CalculateDaysStdDevVolume(daysVolumes);

                    // calculate the ATR and Median ATR
                    var orderedOHLCData = ohlcData.OrderBy(x => x.Timestamp).ToList();
                    List<decimal> trueRanges = new();

                    decimal prevClose = orderedOHLCData[0].Close;

                    // Calculation of DaysStdDevVolume
                    long mean = 0;
                    long m2 = 0;
                    int n = 0;

                    n++;
                    mean += (orderedOHLCData[0].Volume - mean) / n;
                    m2 += (orderedOHLCData[0].Volume - mean) * (orderedOHLCData[0].Volume - mean);

                    foreach (var candle in orderedOHLCData.Skip(1))
                    {
                        decimal highLow = candle.High - candle.Low;
                        decimal highPrevClose = Math.Abs(candle.High - prevClose);
                        decimal lowPrevClose = Math.Abs(candle.Low - prevClose);

                        decimal trueRange = Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
                        trueRanges.Add(trueRange);

                        prevClose = candle.Close;

                        // Calculation of DaysStdDevVolume
                        n++;
                        var delta = candle.Volume - mean;
                        mean += delta / n;
                        m2 += delta * (candle.Volume - mean);
                    }

                    var variance = m2 / (n - 1);
                    daysStdDevVolume = (long)Math.Sqrt((long)variance);

                    decimal atr = trueRanges.Average();

                    // Median ATR
                    var ordered = trueRanges.OrderBy(x => x).ToList();
                    decimal medianAtr = ordered[ordered.Count / 2];

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
                        var prevOHLCData = previousOHLCData.OrderByDescending(x => x.Id).FirstOrDefault();

                        previousDayHigh = previousOHLCData.Max(x => x.High);
                        previousDayLow = previousOHLCData.Min(x => x.Low);
                        previousDayClose = prevOHLCData?.LastPrice != null ? prevOHLCData.LastPrice ?? 0 : prevOHLCData?.Close ?? 0;
                        pivotPoint = (previousDayHigh + previousDayLow + previousDayClose) / 3;
                        bottomCP = (previousDayHigh + previousDayLow) / 2;
                        topCP = (pivotPoint - bottomCP) + pivotPoint;

                        decimal perMovement = (((topCP - bottomCP) / topCP) * 100);

                        // if the perMovement is in the negetive in that case it also means there is strong Negetive movement in the stock which may contine next day.
                        if (perMovement < 0)
                            perMovement = perMovement * (-1);

                        var futPreCompData = new FuturePreComputedData
                        {
                            PivotPoint = pivotPoint,
                            BottomCP = bottomCP,
                            TopCP = topCP,
                            StockMetaDataId = stock.Id,
                            TR1 = perMovement < 0.05M ? true : false,
                            TR2 = (perMovement >= 0.05M && perMovement <= 0.10M) ? true : false,
                            WasTrendy = false,
                            CreatedDate = DateTime.Now.Date,
                            ForDate = GetNextBusinessDay(DateTime.Now, Holidays).Date
                        };

                        futurePrecomputedDatas.Add(futPreCompData);
                    }

                    var precomputedValue = new PreComputedData
                    {
                        CreatedDate = DateTime.Now.Date,
                        DaysHigh = daysHigh,
                        DaysLow = daysLow,
                        DaysAverageClose = daysAverageClose,
                        DaysAverageVolume = daysAverageVolume,
                        DaysAboveVWAPPercentage = 0,
                        DaysATR = atr,
                        DaysMedianATR = medianAtr,
                        DaysAverageBodySize = 0,
                        DaysGreenPercentage = 0,
                        DaysHighLowRangePercentage = 0,
                        DaysStdDevClose = 0,
                        DaysStdDevVolume = daysStdDevVolume,
                        DaysTrendScore = 0,
                        DaysVWAP = 0,
                        StockMetaDataId = stock.Id,
                        PreviousDayHigh = previousDayHigh,
                        PreviousDayClose = previousDayClose,
                        PreviousDayLow = previousDayLow
                    };

                    preCompuerDataList.Add(precomputedValue);
                }
            }

            // Delete the last day data from the OHLCs table
            //DeleteLastDayFromOHLC(allDatesInTable.Last().Value);

            db.FuturePreComputedDatas.AddRange(futurePrecomputedDatas);
            db.PreComputedDatas.AddRange(preCompuerDataList);
            db.SaveChanges();

            return "Precomputed data captured/updated successfully.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private long CalculateDaysStdDevVolume(List<long> dailyVolumes)
    {
        long mean = 0;
        long m2 = 0;
        int n = 0;

        foreach (var volume in dailyVolumes)
        {
            n++;
            var delta = volume - mean;
            mean += delta / n;
            m2 += delta * (volume - mean);
        }

        var variance = m2 / (n - 1);
        return (long)Math.Sqrt((long)variance);
    }

    public static DateTime GetNextBusinessDay(DateTime fromDate, List<DateTime> holidays)
    {
        DateTime nextDate = fromDate.AddDays(1);

        // Normalize holiday dates (ignore time)
        HashSet<DateTime> holidaySet = holidays
            .Select(h => h.Date)
            .ToHashSet();

        while (true)
        {
            // Skip Saturday & Sunday
            if (nextDate.DayOfWeek == DayOfWeek.Saturday ||
                nextDate.DayOfWeek == DayOfWeek.Sunday)
            {
                nextDate = nextDate.AddDays(1);
                continue;
            }

            // Skip Holidays
            if (holidaySet.Contains(nextDate.Date))
            {
                nextDate = nextDate.AddDays(1);
                continue;
            }

            // Valid business day found
            return nextDate;
        }
    }

    public bool DeleteLastDayFromOHLC(DateTime? lastDateInTable)
    {
        try
        {
            var db = _upStoxDbContext.CreateDbContext();

            // Fire and forget no need to await.
            if (lastDateInTable != null)
            {
                _ = db.OHLCs.Where(x => x.CreatedDate == lastDateInTable).ExecuteDeleteAsync();
                _ = db.FuturePreComputedDatas.Where(x => x.CreatedDate == lastDateInTable).ExecuteDeleteAsync();
                _ = db.PreComputedDatas.Where(x => x.CreatedDate == lastDateInTable).ExecuteDeleteAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            return false;
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
        var db = _upStoxDbContext.CreateDbContext();

        var authDetail = db.AuthDetails.AsNoTracking().Where(x => x.Id == 1).FirstOrDefault();

        return authDetail?.AccessToken ?? throw new Exception("Invalid access token");
    }

    public async Task<Tuple<bool, string>> GetMarketUpdate(string accessToken)
    {
        try
        {
            var db = _upStoxDbContext.CreateDbContext();

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var marketMetaData = await db.MarketMetaDatas.AsNoTracking().ToListAsync();
            var stockNameWithKey = marketMetaData.ToDictionary(x => x.Name, x => x.Id);

            var instrumentKey = string.Join(",", marketMetaData.Select(x => x.InstrumentToken));

            // API endpoint (you can dynamically change symbols if needed), NSE_EQ|INE040A01034,NSE_EQ|INE062A01020
            string url = "https://api.upstox.com/v3/market-quote/ohlc?instrument_key=" + instrumentKey + "&interval=I1";

            // Make GET request
            HttpResponseMessage response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Upstox API failed ({response.StatusCode}): {error}");
            }

            string jsonResponse = await response.Content.ReadAsStringAsync();

            ApiResponse apiResponse = JsonSerializer.Deserialize<ApiResponse>(jsonResponse, _jsonOptions) ?? new ApiResponse();

            var result = await AddMarketDataEFCore(apiResponse, stockNameWithKey);

            return new Tuple<bool, string>(result, "Success");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetMarketUpdate: {ex.Message}");
            return new Tuple<bool, string>(false, ex.Message);
        }
    }

    public async Task<bool> AddMarketDataEFCore(ApiResponse apiResponse, Dictionary<string, long> marketMetaDatas)
    {
        var prevOhlcList = new List<OHLC>();
        var db = _upStoxDbContext.CreateDbContext();

        if (apiResponse.Data == null && apiResponse.Status != "success")
            return false;

        // Find the latest
        var preComputedRecord = await db.PreComputedDatas
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();

        var allPrecomputedData = await db.PreComputedDatas
            .AsNoTracking()
            .Where(x => x.CreatedDate == preComputedRecord.CreatedDate)
            .ToListAsync();

        // Get Previous day lastprice or closing price
        var findLastDate = await db.OHLCs
            .AsNoTracking()
            .Where(x => x.Time == new TimeSpan(15, 29, 0))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();

        var previousCloseStockCollection = await db.OHLCs
            .AsNoTracking()
            .Where(x => x.CreatedDate != null
                && findLastDate != null
                && findLastDate.CreatedDate != null
                && x.CreatedDate.Value.Date == findLastDate.CreatedDate.Value.Date
                && x.Time == new TimeSpan(15, 29, 0))
            .ToListAsync(); // this will fetch 235 stocks/index details

        foreach (var item in apiResponse.Data)
        {
            var instrumentKey = item.Key;
            marketMetaDatas.TryGetValue(instrumentKey, out var stockMetaDataId);

            // calculate the pChange for each stock based on lastprice or closeprice
            var stockDetails = previousCloseStockCollection.Where(x => x.StockMetaDataId == stockMetaDataId).FirstOrDefault();

            var previousClose = stockDetails?.LastPrice > 0 ? stockDetails.LastPrice : stockDetails?.Close ?? 0;

            var stockPrecomputedData = allPrecomputedData.Where(x => x.StockMetaDataId == stockMetaDataId).FirstOrDefault();

            if (DateTime.Now.Hour == 15 && DateTime.Now.Minute == 30)
            {
                var pChange = previousClose == 0 ? 0 : ((((item.Value?.LastPrice ?? item.Value.LiveOhlc.Close) - previousClose) * 100) / previousClose);

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
                    PChange = pChange,
                    RFactor = (stockPrecomputedData != null && item.Value != null && item.Value?.LastPrice != 0) ? ((stockPrecomputedData.DaysHigh - stockPrecomputedData.DaysLow) / item.Value?.LastPrice ?? 1) * 100 : 0
                });
            }
            else
            {
                var pChange = previousClose == 0 ? 0 : ((((item.Value?.LastPrice ?? item.Value.PrevOhlc.Close) - previousClose) * 100) / previousClose);

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
                    PChange = pChange,
                    RFactor = (stockPrecomputedData != null && item.Value != null && item.Value?.LastPrice != 0) ? ((stockPrecomputedData.DaysHigh - stockPrecomputedData.DaysLow) / item.Value?.LastPrice ?? 1) * 100 : 0
                });
            }
        }

        if (prevOhlcList?.Count == 0)
            return false;

        db.OHLCs.AddRange(prevOhlcList);
        db.SaveChanges();

        return true;
    }

    public async Task<Tuple<bool, string>> GetOptionExpiryData(string accessToken)
    {
        try
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            string scriptName = "Nifty 50";
            string expiryDate = "2025-12-23"; // Need to change every Tuesday EOD

            // pass next expiry data
            string url = "https://api.upstox.com/v2/option/chain?instrument_key=NSE_INDEX|" + scriptName + "&expiry_date=" + expiryDate;

            // Make GET request
            HttpResponseMessage response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Upstox option API failed ({response.StatusCode}): {error}");
            }

            string jsonResponse = await response.Content.ReadAsStringAsync();

            ApiOptionResponse apiResponse = JsonSerializer.Deserialize<ApiOptionResponse>(jsonResponse, _jsonOptions) ?? new ApiOptionResponse();

            var result = await AddOptionExpiryDataToTable(apiResponse);

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetOptionExpiryData: {ex.Message}");
            return new Tuple<bool, string>(false, ex.Message);
        }
    }

    public async Task<Tuple<bool, string>> AddOptionExpiryDataToTable(ApiOptionResponse apiResponse)
    {
        try
        {
            var db = _upStoxDbContext.CreateDbContext();
            var optionDatas = new List<OptionExpiryData>();

            foreach (var item in apiResponse.Data)
            {
                var optionData = new OptionExpiryData
                {
                    CallLTP = item.CallOptions.MarketData.LTP,
                    CallOI = (long)item.CallOptions.MarketData.OI,
                    CallPrevOI = (long)item.CallOptions.MarketData.PrevOI,
                    CallVolume = item.CallOptions.MarketData.Volume,

                    CreatedDate = DateTime.Now.Date,
                    Expiry = item.Expiry,

                    PutLTP = item.PutOptions.MarketData.LTP,
                    PutOI = (long)item.PutOptions.MarketData.OI,
                    PutPrevOI = (long)item.PutOptions.MarketData.PrevOI,
                    PutVolume = item.PutOptions.MarketData.Volume,

                    SpotPrice = item.UnderlyingSpotPrice,
                    StrikePCR = item.PCR,
                    StrikePrice = item.StrikePrice,
                    StockMetaDataId = 89,
                    Time = new TimeSpan(DateTime.Now.Hour, DateTime.Now.Minute, 0),
                };

                optionDatas.Add(optionData);
            }

            var prevOptionEntry = await db.optionExpirySummaries
                .AsNoTracking()
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            var totOICE = optionDatas.Sum(x => x.CallOI);
            var totOIPE = optionDatas.Sum(x => x.PutOI);

            var totVolCE = optionDatas.Sum(x => x.CallVolume);
            var totVolPE = optionDatas.Sum(x => x.PutVolume);

            var cEPEOIDiff = totOICE - totOIPE;
            var cEPEVolDiff = totVolCE - totVolPE;

            var cEPEOIPrevDiff = cEPEOIDiff - (prevOptionEntry?.CEPEOIDiff ?? cEPEOIDiff);
            var cEPEVolPrevDiff = cEPEVolDiff - (prevOptionEntry?.CEPEVolDiff ?? cEPEVolDiff);

            var optionExpirySummary = new OptionExpirySummary
            {
                Time = new TimeSpan(DateTime.Now.Hour, DateTime.Now.Minute, 0),
                EntryDate = DateTime.Now.Date,

                TotOICE = totOICE,
                TotOIPE = totOIPE,

                TotVolCE = totVolCE,
                TotVolPE = totVolPE,

                CEPEOIDiff = cEPEOIDiff,    // (+) means market is createing support of call unwinding, (-) means market is createing reisitence or put unwinding
                CEPEVolDiff = cEPEVolDiff,

                CEPEOIPrevDiff = cEPEOIPrevDiff,
                CEPEVolPrevDiff = cEPEVolPrevDiff
            };

            await db.optionExpirySummaries.AddAsync(optionExpirySummary);

            await db.OptionExpiryDatas.AddRangeAsync(optionDatas);

            await db.SaveChangesAsync();

            return new Tuple<bool, string>(true, "Option expiry data saved successfully.");
        }
        catch (Exception ex)
        {
            return new Tuple<bool, string>(false, ex.Message);
        }
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
        services.AddDbContextFactory<UpStoxDbContext>(x =>
            x.UseSqlServer("Data Source=190.92.174.111;Initial Catalog=karmajew_optionchain;User Id=karmajew_sa;Password=Prokyonz@2023;TrustServerCertificate=True;MultipleActiveResultSets=True;"), ServiceLifetime.Transient);
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