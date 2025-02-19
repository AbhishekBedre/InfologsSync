using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OptionChain;
using Quartz;

namespace SyncData
{
    public class SessionUpdateJob : IJob
    {
        private readonly ILogger<FetchAndProcessJob> _logger;
        private readonly OptionDbContext _optionDbContext;
        private object counter = 0;

        public SessionUpdateJob(ILogger<FetchAndProcessJob> log, OptionDbContext optionDbContext)
        {
            _logger = log;
            _optionDbContext = optionDbContext;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation($"{nameof(SessionUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));
            Utility.LogDetails($"{nameof(SessionUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));

            await ExecuteSessionUpdate(context);

            Console.WriteLine($"{nameof(SessionUpdateJob)} completed successfully. Time: - " + context.FireTimeUtc.ToLocalTime());

            await Task.CompletedTask;
        }

        public async Task ExecuteSessionUpdate(IJobExecutionContext context)
        {

        STEP:

            try
            {
                var sessionResult = await GetSessionUpdate(counter, context);

                if (sessionResult.Status == false && Convert.ToInt16(sessionResult.Counter) <= 3)
                {
                    await Task.Delay(2000);
                    counter = sessionResult.Counter;

                    goto STEP;
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Multiple tried for stock data but not succeed. counter: {counter}");
                counter = 0;

                Utility.LogDetails($"{nameof(ExecuteSessionUpdate)} Exception: {ex.Message}");
            }
        }

        public async Task<(bool Status, object Counter, string Cookie)> GetSessionUpdate(object counter,
            IJobExecutionContext context)
        {
            bool status = true;
            string cookie = "";

            try
            {
                cookie = await OpenPlayWrightBrowser();

                if (string.IsNullOrWhiteSpace(cookie))
                {
                    status = false;
                }
                else
                {
                    var sessionRecord = await _optionDbContext.Sessions.Where(x => x.Id > 0).FirstOrDefaultAsync();

                    if(sessionRecord == null)
                    {
                        await _optionDbContext.Sessions.AddAsync(new Sessions
                        {
                            Cookie = cookie,
                            UpdatedDate = DateTime.Now
                        });
                    } else
                    {
                        sessionRecord.Cookie = cookie;
                        sessionRecord.UpdatedDate = DateTime.Now;
                    }

                    await _optionDbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                Utility.LogDetails($"{nameof(GetSessionUpdate)} -> Exception: {ex.Message}.");
                _logger.LogInformation($"Exception: {ex.Message}");
                counter = Convert.ToInt16(counter) + 1;
                status = false;
            }

            return (status, counter, cookie);
        }

        public async Task<string> OpenPlayWrightBrowser()
        {
            string finalCookie = "";
            string url = "https://www.nseindia.com/";

            HttpClientHandler httpClientHandler = new HttpClientHandler();

            // Enable automatic decompression for gzip, deflate, and Brotli
            httpClientHandler.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                             System.Net.DecompressionMethods.Deflate |
                                             System.Net.DecompressionMethods.Brotli;

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
                        string localCookie = response.Headers.NonValidated.ToList().Where(x => x.Key == "Set-Cookie").FirstOrDefault().Value.ToString();

                        foreach (var cookie in localCookie.Split(";"))
                        {
                            if (cookie.Trim().Contains("_abck="))
                                finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("_abck=")).Trim() + ";";

                            if (cookie.Trim().Contains("ak_bmsc"))
                                finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("ak_bmsc=")).Trim() + ";";

                            if (cookie.Trim().Contains("bm_sv="))
                                finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("bm_sv=")).Trim() + ";";

                            if (cookie.Trim().Contains("bm_sz="))
                                finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("bm_sz=")).Trim() + ";";

                            if (cookie.Trim().Contains("nseappid="))
                                finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("nseappid=")).Trim() + ";";

                            if (cookie.Trim().Contains("nsit="))
                                finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("nsit=")).Trim() + ";";
                            
                            if (cookie.Trim().Contains("AKA_A2="))                            
                                finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("AKA_A2=")).Trim() + ";";                            
                        }

                        finalCookie = finalCookie.Trim();
                    }
                }
                catch (Exception ex)
                {
                    Utility.LogDetails($"{nameof(OpenPlayWrightBrowser)} Exception: {ex.Message}");
                    return "";
                }
            }

            return finalCookie;
        }
    }
}
