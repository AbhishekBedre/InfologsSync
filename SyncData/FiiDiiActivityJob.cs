using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OptionChain;
using Quartz;
using System.Text.Json;

public class FiiDiiActivityJob : IJob
{
    private readonly ILogger<FiiDiiActivityJob> _logger;
    private readonly OptionDbContext _optionDbContext;
    private object counter = 0;

    public FiiDiiActivityJob(ILogger<FiiDiiActivityJob> log, OptionDbContext optionDbContext)
    {
        _logger = log;
        _optionDbContext = optionDbContext;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation($"{nameof(FiiDiiActivityJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));
        Utility.LogDetails($"{nameof(FiiDiiActivityJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));

        await GetFIIDIIActivity(context);

        Console.WriteLine($"{nameof(FiiDiiActivityJob)} completed successfully. Time: - " + context.FireTimeUtc.ToLocalTime());

        await Task.CompletedTask;
    }

    public async Task GetFIIDIIActivity(IJobExecutionContext context)
    {
    STEP:

        try
        {
            (bool status, object result, FiiDiiActivity? fiiDiiActivity) = await GetFIIDIIActivityData(counter, context);

            if (status == false && Convert.ToInt16(result) <= 3)
            {
                await Task.Delay(2000);
                counter = result;

                goto STEP;
            }

            if (Convert.ToInt32(counter) <= 3)
            {
                counter = 0;
                // Make a Db Call
                await StoreFIIDIIActivityDataInTable(fiiDiiActivity, context);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Multiple tried to get FII DII Activity but not succeed. counter: {counter}");
            counter = 0;

            Utility.LogDetails($"{nameof(GetFIIDIIActivity)} Exception: {ex.Message}");
        }
    }

    private async Task<(bool, object, List<FiiDiiActivity>?)> GetFIIDIIActivityData(object counter, IJobExecutionContext context)
    {
        Utility.LogDetails($"{nameof(GetFIIDIIActivityData)} -> Send quots reqest counter:" + counter + ", Time: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm"));

        bool status = true;
        List<FiiDiiActivity>? fiiDiiActivityData = null;
        
        _logger.LogInformation($"Exection time: {counter}");


        HttpClientHandler httpClientHandler = new HttpClientHandler();

        // Enable automatic decompression for gzip, deflate, and Brotli
        httpClientHandler.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                            System.Net.DecompressionMethods.Deflate |
                                            System.Net.DecompressionMethods.Brotli;

        using (HttpClient client = new HttpClient(httpClientHandler))
        {
            await Common.UpdateCookieAndHeaders(client, _optionDbContext, JobType.FIIDIIActivity);

            string url = "https://www.nseindia.com/api/fiidiiTradeReact";

            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string jsonContent = await response.Content.ReadAsStringAsync();

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    fiiDiiActivityData = JsonSerializer.Deserialize<List<FiiDiiActivity>>(jsonContent, options);

                    if (fiiDiiActivityData == null)
                    {
                        _logger.LogInformation("Failed to parse JSON content.");
                        Utility.LogDetails($"{nameof(GetFIIDIIActivityData)} -> Failed to parse JSON content.");
                        throw new Exception("Failed to parse JSON content.");
                    }
                }
                else
                {
                    Utility.LogDetails($"{nameof(GetFIIDIIActivityData)} -> HTTP Error: {response.StatusCode}.");
                    _logger.LogInformation($"HTTP Error: {response.StatusCode}");
                    throw new Exception($"Http Error: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Utility.LogDetails($"{nameof(GetFIIDIIActivityData)} -> Exception: {ex.Message}.");
                _logger.LogInformation($"Exception: {ex.Message}");
                counter = Convert.ToInt16(counter) + 1;
                status = false;
            }
        }

        return (status, counter, optionData);            
    }
    private async Task<bool> StoreFIIDIIActivityDataInTable(List<FiiDiiActivity>? fiiDiiActivity, IJobExecutionContext context)
    {
        try
        {
            _logger.LogInformation("Adding FII & DII data to table.");

            if(fiiDiiActivity != null && fiiDiiActivity.Any()) { 

                // Check if the activity for date is available or not, if not then insert otherwise skip

                await _optionDbContext.FiiDiiActivity.AddRangeAsync(fiiDiiActivity);
                await _optionDbContext.SaveChangesAsync();
                
                return true;
            }
            
            return false;
        }
        catch(Exception ex)
        {
            Utility.LogDetails($"{nameof(StoreStockDataInTable)} -> Exception: {ex.Message}.");            
            return false;
        }

        return false;
    }
}