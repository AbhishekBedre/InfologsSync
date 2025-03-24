using Infologs.SessionReader.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OptionChain;
using OptionChain.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Infologs.SessionReader
{
    public interface ITasks
    {
        public Task<string> ReadSessionAsync();
        public Task<bool> ReadFIIDIIActvityAsync();
    }
    public class DataReader : ITasks
    {
        private readonly OptionDbContext _dbContext;
        private readonly ICacheHelper _cacheHelper;
        HttpClientHandler httpClientHandler;
        public DataReader(OptionDbContext dbContext, ICacheHelper cacheHelper)
        {
            _dbContext = dbContext;
            _cacheHelper = cacheHelper;

            httpClientHandler = new HttpClientHandler();

            httpClientHandler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli;
        }
        public async Task<string> ReadSessionAsync()
        {
            StringBuilder finalCookie = new StringBuilder();
            string url = "https://www.nseindia.com/";

            using (HttpClient client = new HttpClient(httpClientHandler))
            {
                client.DefaultRequestHeaders.Add("User-Agent", "PostmanRuntime/7.43.0");
                client.DefaultRequestHeaders.Add("Accept", "*/*");
                client.DefaultRequestHeaders.Add("Connection", "keep-alive");
                client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
                client.DefaultRequestHeaders.Add("cookie", "AKA_A2=A;");

                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        string localCookie = response.Headers.NonValidated
                            .FirstOrDefault(x => x.Key == "Set-Cookie").Value.ToString() ?? "";

                        foreach (var cookie in localCookie.Split(";"))
                        {
                            if (cookie.Trim().Contains("_abck="))
                                finalCookie.Append(cookie.Substring(cookie.IndexOf("_abck=")).Trim() + ";");
                            if (cookie.Trim().Contains("ak_bmsc="))
                                finalCookie.Append(cookie.Substring(cookie.IndexOf("ak_bmsc=")).Trim() + ";");
                            if (cookie.Trim().Contains("bm_sv="))
                                finalCookie.Append(cookie.Substring(cookie.IndexOf("bm_sv=")).Trim() + ";");
                            if (cookie.Trim().Contains("bm_sz="))
                                finalCookie.Append(cookie.Substring(cookie.IndexOf("bm_sz=")).Trim() + ";");
                            if (cookie.Trim().Contains("nseappid="))
                                finalCookie.Append(cookie.Substring(cookie.IndexOf("nseappid=")).Trim() + ";");
                            if (cookie.Trim().Contains("nsit="))
                                finalCookie.Append(cookie.Substring(cookie.IndexOf("nsit=")).Trim() + ";");
                            if (cookie.Trim().Contains("AKA_A2="))
                                finalCookie.Append(cookie.Substring(cookie.IndexOf("AKA_A2=")).Trim() + ";");
                        }

                        TimeZoneInfo istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                        DateTime istTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);

                        var sessionRecord = await _dbContext.Sessions.FirstOrDefaultAsync();

                        if (sessionRecord == null)
                        {
                            await _dbContext.Sessions.AddAsync(new Sessions
                            {
                                Cookie = finalCookie.ToString(),
                                UpdatedDate = istTime
                            });
                        }
                        else
                        {
                            sessionRecord.Cookie = finalCookie.ToString();
                            sessionRecord.UpdatedDate = istTime;

                            string cacheKey = "sessionCookie";

                            // Use common function to get or create cache
                            _cacheHelper.GetOrCreate(cacheKey, () => finalCookie.ToString(), TimeSpan.FromHours(5));
                        }

                        await _dbContext.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            }

            return finalCookie.ToString();
        }

        public async Task<bool> ReadFIIDIIActvityAsync()
        {
            List<FiiDiiActivity>? fiiDiiActivityData = new List<FiiDiiActivity>();
            List<FIIDIIActivityResponse>? fiiDiiActivityresponse = null;

            using (HttpClient client = new HttpClient(httpClientHandler))
            {
                await Common.UpdateCookieAndHeaders(client, _dbContext, JobType.FIIDIIActivity, _cacheHelper);

                string url = "https://www.nseindia.com/api/fiidiiTradeReact";

                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonContent = await response.Content.ReadAsStringAsync();

                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        fiiDiiActivityresponse = JsonSerializer.Deserialize<List<FIIDIIActivityResponse>>(jsonContent, options);
                    }
                }
                catch
                {
                    return false;
                }
            }

            if (fiiDiiActivityresponse != null)
            {
                foreach (var item in fiiDiiActivityresponse)
                {
                    fiiDiiActivityData.Add(new FiiDiiActivity
                    {
                        BuyValue = Convert.ToDecimal(item.BuyValue),
                        Category = item.Category ?? "",
                        Date = DateTime.ParseExact(item.Date, "dd-MMM-yyyy", null),
                        NetValue = Convert.ToDecimal(item.NetValue),
                        SellValue = Convert.ToDecimal(item.SellValue)
                    });
                }

                var currReco = fiiDiiActivityData.First();

                var existingRecord = await _dbContext.FiiDiiActivitys
                    .Where(x => x.Date.HasValue
                        && currReco.Date.HasValue
                        && x.Date.Value.Date == currReco.Date.Value.Date)
                    .FirstOrDefaultAsync();

                if (existingRecord == null)
                {
                    await _dbContext.FiiDiiActivitys.AddRangeAsync(fiiDiiActivityData);
                    await _dbContext.SaveChangesAsync();
                }

                return true;
            }

            return false;
        }
    }

    public enum JobType
    {
        SessionUpdate,
        NiftyUpdate,
        BroderMarketUpdate,
        StockUpdate,
        BankNiftyUpdate,
        FIIDIIActivity
    }

    public static class Common
    {
        public static async Task UpdateCookieAndHeaders(HttpClient httpClient, OptionDbContext optionDbContext, JobType jobType, ICacheHelper cacheHelper)
        {
            var sessionCookie = "";
            StringBuilder cookies = new StringBuilder();

            var Cookie = cacheHelper.Get<string>("sessionCookie");

            if (string.IsNullOrWhiteSpace(Cookie))
            {
                var sessionInfo = await optionDbContext.Sessions.FirstAsync();

                if (sessionInfo != null)
                {
                    sessionCookie = sessionInfo.Cookie ?? "";
                }
            }
            else
            {
                sessionCookie = Cookie;
            }

            foreach (var cookie in sessionCookie.Split(";"))
            {
                if (cookie.Trim().StartsWith("_abck="))
                    cookies.Append(cookie.Trim() + ";");

                if (cookie.Trim().StartsWith("ak_bmsc="))
                    cookies.Append(cookie.Trim() + ";");

                if (cookie.Trim().StartsWith("bm_sv="))
                    cookies.Append(cookie.Trim() + ";");

                if (cookie.Trim().StartsWith("bm_sz="))
                    cookies.Append(cookie.Trim() + ";");

                if (cookie.Trim().StartsWith("nseappid="))
                    cookies.Append(cookie.Trim() + ";");

                if (cookie.Trim().StartsWith("nsit="))
                    cookies.Append(cookie.Trim() + ";");
            }
            cookies.Append("AKA_A2=A;");

            if (jobType == JobType.BroderMarketUpdate)
            {
                string pattern = @"(nsit=[^;]*;|nseappid=[^;]*;)";
                cookies = new StringBuilder(Regex.Replace(cookies.ToString(), pattern, string.Empty));
            }

            httpClient.DefaultRequestHeaders.Add("User-Agent", "PostmanRuntime/7.43.0");
            httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
            httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            httpClient.DefaultRequestHeaders.Add("cookie", cookies.ToString());
        }
    }
}