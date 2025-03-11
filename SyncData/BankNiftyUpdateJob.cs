using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OptionChain;
using Quartz;
using System.Text.Json;

namespace SyncData
{
    public class BankNiftyUpdateJob : IJob
    {
        private readonly ILogger<BankNiftyUpdateJob> _logger;
        private readonly OptionDbContext _optionDbContext;
        private object counter = 0;
        private object stockCounter = 0;
        private double? previousCPEOIDiffValue = null; // To store the previous X value
        private double? previousCPEColDiffValue = null; // To store the previous X value

        public BankNiftyUpdateJob(ILogger<BankNiftyUpdateJob> log,
            OptionDbContext optionDbContext)
        {
            _logger = log;
            _optionDbContext = optionDbContext;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation($"{nameof(BankNiftyUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));
            Utility.LogDetails($"{nameof(BankNiftyUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));

            await GetBankNiftyOptions(context);

            Console.WriteLine($"{nameof(BankNiftyUpdateJob)} completed successfully. Time: - " + context.FireTimeUtc.ToLocalTime());

            await Task.CompletedTask;
        }

        public async Task GetBankNiftyOptions(IJobExecutionContext context)
        {
        STEP:

            try
            {
                (bool status, object result, BankRoot? optionData) = await GetBankNiftyOptionData(counter, context);

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
                    await StoreBankOptionDataInTable(optionData, context);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Multiple tried bank nifty options but not succeed. counter: {counter}");
                counter = 0;

                Utility.LogDetails($"{nameof(GetBankNiftyOptions)} Exception: {ex.Message}");
            }
        }

        private async Task<(bool, object, BankRoot?)> GetBankNiftyOptionData(object counter, IJobExecutionContext context)
        {
            Utility.LogDetails($"{nameof(GetBankNiftyOptionData)} -> Send quots reqest counter:" + counter + ", Time: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm"));

            bool status = true;
            BankRoot? optionData = null;
            
            _logger.LogInformation($"Exection time: {counter}");


            HttpClientHandler httpClientHandler = new HttpClientHandler();

            // Enable automatic decompression for gzip, deflate, and Brotli
            httpClientHandler.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                             System.Net.DecompressionMethods.Deflate |
                                             System.Net.DecompressionMethods.Brotli;

            using (HttpClient client = new HttpClient(httpClientHandler))
            {
                await Common.UpdateCookieAndHeaders(client, _optionDbContext, JobType.BankNiftyUpdate);

                string url = "https://www.nseindia.com/api/option-chain-indices?symbol=BANKNIFTY";

                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonContent = await response.Content.ReadAsStringAsync();

                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        optionData = JsonSerializer.Deserialize<BankRoot>(jsonContent, options);

                        if (optionData == null || optionData.Filtered == null || optionData.Records == null)
                        {
                            _logger.LogInformation("Failed to parse JSON content.");
                            Utility.LogDetails($"{nameof(GetBankNiftyOptionData)} -> Failed to parse JSON content.");
                            throw new Exception("Failed to parse JSON content.");
                        }
                    }
                    else
                    {
                        Utility.LogDetails($"{nameof(GetBankNiftyOptionData)} -> HTTP Error: {response.StatusCode}.");
                        _logger.LogInformation($"HTTP Error: {response.StatusCode}");
                        throw new Exception($"Http Error: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    Utility.LogDetails($"{nameof(GetBankNiftyOptionData)} -> Exception: {ex.Message}.");
                    _logger.LogInformation($"Exception: {ex.Message}");
                    counter = Convert.ToInt16(counter) + 1;
                    status = false;
                }
            }

            return (status, counter, optionData);
        }

        private async Task<bool> StoreBankOptionDataInTable(BankRoot? optionData, IJobExecutionContext context)
        {
            try
            {
                _logger.LogInformation("Adding bank nifty data to table.");

                if (optionData != null)
                {
                    await _optionDbContext.Database.BeginTransactionAsync();

                    if (optionData.Records != null
                        && optionData.Filtered != null
                        && optionData.Records.Data != null
                        && optionData.Filtered.Data != null)
                    {
                        optionData.Records.Data?.ForEach(r =>
                        {
                            r.EntryDate = DateTime.Now.Date;
                            r.Time = DateTime.Now.TimeOfDay;
                        });

                        //await _optionDbContext.BankOptionData.AddRangeAsync(optionData.Records.Data);

                        //await _optionDbContext.SaveChangesAsync();

                        //await _optionDbContext.BankExpiryOptionData.AddRangeAsync(new BankExpiryOptionData().ConvertToFilterOptionData(optionData.Filtered.Data));

                        //await _optionDbContext.SaveChangesAsync();

                        // Calculate the Summary

                        var currentCPEOIValue = optionData.Filtered.CE.TotOI - optionData.Filtered.PE.TotOI;
                        var currentCPEVolValue = optionData.Filtered.CE.TotVol - optionData.Filtered.PE.TotVol;

                        long previousCEPEOIId = 0;

                        if (await _optionDbContext.BankSummary.AnyAsync())
                        {
                            previousCEPEOIId = await _optionDbContext.BankSummary.MaxAsync(m => m.Id);
                        }

                        var lastRecord = await _optionDbContext.BankSummary.Where(w => w.Id == previousCEPEOIId).FirstOrDefaultAsync();

                        if (lastRecord != null)
                        {
                            previousCPEOIDiffValue = lastRecord.CEPEOIDiff;
                            previousCPEColDiffValue = lastRecord.CEPEVolDiff;
                        }

                        double CEPEOIPreDiff = previousCPEOIDiffValue.HasValue ? ((currentCPEOIValue) - (previousCPEOIDiffValue.Value)) : 0;
                        double CEPEVolPreDiff = previousCPEColDiffValue.HasValue ? ((currentCPEOIValue) - (previousCPEColDiffValue.Value)) : 0;

                        BankSummary summary = new BankSummary
                        {
                            TotOICE = optionData.Filtered.CE.TotOI,
                            TotVolCE = optionData.Filtered.CE.TotVol,

                            TotOIPE = optionData.Filtered.PE.TotOI,
                            TotVolPE = optionData.Filtered.PE.TotVol,

                            CEPEOIDiff = optionData.Filtered.CE.TotOI - optionData.Filtered.PE.TotOI,
                            CEPEVolDiff = optionData.Filtered.CE.TotVol - optionData.Filtered.PE.TotVol,

                            CEPEOIPrevDiff = CEPEOIPreDiff,
                            CEPEVolPrevDiff = CEPEVolPreDiff,

                            Time = context.FireTimeUtc.ToLocalTime().TimeOfDay,
                            EntryDate = DateTime.Now.Date
                        };

                        await _optionDbContext.BankSummary.AddAsync(summary);

                        await _optionDbContext.SaveChangesAsync();

                    }
                    else
                    {
                        await _optionDbContext.Database.RollbackTransactionAsync();
                    }

                    await _optionDbContext.Database.CommitTransactionAsync();

                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"DB Function Exception: {ex.Message}");
                Utility.LogDetails($"{nameof(StoreBankOptionDataInTable)} -> Exception: {ex.Message}.");
                await _optionDbContext.Database.RollbackTransactionAsync();
                return false;
            }

            return true;
        }
    }
}
