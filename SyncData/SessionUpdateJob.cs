using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
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
            // Step 1: Use Playwright to extract cookies

            string finalCookie = "";
            string url = "https://www.nseindia.com/option-chain?symbol=NIFTY";

            var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
            
            try
            {
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36"
                });

                var page = await context.NewPageAsync();

                await Task.Delay(1500);

                await page.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
                {
                    { "Referer", "https://www.nseindia.com/" },
                    { "Accept-Language", "en-US,en;q=0.9" },
                    { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8" }
                });

                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle
                });

                await Task.Delay(10000);

                Console.WriteLine("Page loaded successfully!");

                var cookies = await context.CookiesAsync();
                string cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

                foreach (var cookie in cookieHeader.Split(";"))
                {
                    if (cookie.Trim().StartsWith("_abck="))
                    {
                        finalCookie += cookie.Trim() + ";";
                    }

                    if (cookie.Trim().StartsWith("ak_bmsc="))
                    {
                        finalCookie += cookie.Trim() + ";";
                    }

                    if (cookie.Trim().StartsWith("bm_sv="))
                    {
                        finalCookie += cookie.Trim() + ";";
                    }

                    if (cookie.Trim().StartsWith("bm_sz="))
                    {
                        finalCookie += cookie.Trim() + ";";
                    }

                    if (cookie.Trim().StartsWith("nseappid="))
                    {
                        finalCookie += cookie.Trim() + ";";
                    }

                    if (cookie.Trim().StartsWith("nsit="))
                    {
                        finalCookie += cookie.Trim() + ";";
                    }
                }
                finalCookie = finalCookie.Trim();

                Console.WriteLine(finalCookie);

                return finalCookie;
            }
            catch (Exception ex)
            {
                Utility.LogDetails($"{nameof(OpenPlayWrightBrowser)} Exception: {ex.Message}");
                return "";
            }
            finally
            {
                await browser.CloseAsync();
            }
        }
    }
}
