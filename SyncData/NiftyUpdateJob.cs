using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OptionChain;
using Quartz;
using System.Text.Json;

namespace SyncData
{
    public class NiftyUpdateJob : IJob
    {
        private readonly ILogger<NiftyUpdateJob> _logger;
        private readonly OptionDbContext _optionDbContext;
        private object counter = 0;
        private object stockCounter = 0;
        private double? previousCPEOIDiffValue = null; // To store the previous X value
        private double? previousCPEColDiffValue = null; // To store the previous X value

        public NiftyUpdateJob(ILogger<NiftyUpdateJob> log, OptionDbContext optionDbContext)
        {
            _logger = log;
            _optionDbContext = optionDbContext;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation($"{nameof(NiftyUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));
            Utility.LogDetails($"{nameof(NiftyUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));

            await GetNiftyOptions(context);

            Console.WriteLine($"{nameof(NiftyUpdateJob)} completed successfully. Time: - " + context.FireTimeUtc.ToLocalTime());

            await Task.CompletedTask;
        }

        public async Task GetNiftyOptions(IJobExecutionContext context)
        {
        STEP:

            try
            {
                (bool status, object result, Root? optionData) = await GetNiftyOptionData(counter, context);

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
                    await StoreOptionDataInTable(optionData, context);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Multiple tried nifty options but not succeed. counter: {counter}");
                counter = 0;

                Utility.LogDetails($"{nameof(GetNiftyOptions)} Exception: {ex.Message}");
            }
        }


        private async Task<(bool, object, Root?)> GetNiftyOptionData(object counter, IJobExecutionContext context)
        {
            Utility.LogDetails($"{nameof(GetNiftyOptionData)} -> Send quots reqest counter:" + counter + ", Time: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm"));

            bool status = true;
            Root? optionData = null;

            _logger.LogInformation($"Exection time: {counter}");

            HttpClientHandler httpClientHandler = new HttpClientHandler();

            // Enable automatic decompression for gzip, deflate, and Brotli
            httpClientHandler.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                             System.Net.DecompressionMethods.Deflate |
                                             System.Net.DecompressionMethods.Brotli;

            using (HttpClient client = new HttpClient(httpClientHandler))
            {
                await Common.UpdateCookieAndHeaders(client, _optionDbContext, JobType.NiftyUpdate);

                string url = "https://www.nseindia.com/api/option-chain-indices?symbol=NIFTY";

                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonContent = await response.Content.ReadAsStringAsync();

                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        optionData = JsonSerializer.Deserialize<Root>(jsonContent, options);

                        if (optionData == null || optionData.Filtered == null || optionData.Records == null)
                        {
                            _logger.LogInformation("Failed to parse JSON content.");
                            Utility.LogDetails($"{nameof(GetNiftyOptionData)} -> Failed to parse JSON content.");
                            throw new Exception("Failed to parse JSON content.");
                        }
                    }
                    else
                    {
                        Utility.LogDetails($"{nameof(GetNiftyOptionData)} -> HTTP Error: {response.StatusCode}.");
                        _logger.LogInformation($"HTTP Error: {response.StatusCode}");
                        throw new Exception($"Http Error: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    Utility.LogDetails($"{nameof(GetNiftyOptionData)} -> Exception: {ex.Message}.");
                    _logger.LogInformation($"Exception: {ex.Message}");
                    counter = Convert.ToInt16(counter) + 1;
                    status = false;
                }
            }

            return (status, counter, optionData);
        }

        private async Task<bool> StoreOptionDataInTable(Root? optionData, IJobExecutionContext context)
        {
            try
            {
                _logger.LogInformation("Adding nifty data to table.");

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

                        await _optionDbContext.AllOptionData.AddRangeAsync(optionData.Records.Data);

                        await _optionDbContext.SaveChangesAsync();

                        await _optionDbContext.CurrentExpiryOptionDaata.AddRangeAsync(new FilteredOptionData().ConvertToFilterOptionData(optionData.Filtered.Data));

                        await _optionDbContext.SaveChangesAsync();

                        // Calculate the Summary

                        var currentCPEOIValue = optionData.Filtered.CE.TotOI - optionData.Filtered.PE.TotOI;
                        var currentCPEVolValue = optionData.Filtered.CE.TotVol - optionData.Filtered.PE.TotVol;

                        long previousCEPEOIId = 0;

                        if (await _optionDbContext.Summary.AnyAsync())
                        {
                            previousCEPEOIId = await _optionDbContext.Summary.MaxAsync(m => m.Id);
                        }

                        var lastRecord = await _optionDbContext.Summary.Where(w => w.Id == previousCEPEOIId).FirstOrDefaultAsync();

                        if (lastRecord != null)
                        {
                            previousCPEOIDiffValue = lastRecord.CEPEOIDiff;
                            previousCPEColDiffValue = lastRecord.CEPEVolDiff;
                        }

                        double CEPEOIPreDiff = previousCPEOIDiffValue.HasValue ? ((currentCPEOIValue) - (previousCPEOIDiffValue.Value)) : 0;
                        double CEPEVolPreDiff = previousCPEColDiffValue.HasValue ? ((currentCPEOIValue) - (previousCPEColDiffValue.Value)) : 0;

                        Summary summary = new Summary
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

                        await _optionDbContext.Summary.AddAsync(summary);

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
                Utility.LogDetails($"{nameof(StoreOptionDataInTable)} -> Exception: {ex.Message}.");
                await _optionDbContext.Database.RollbackTransactionAsync();
                return false;
            }

            return true;
        }
    }
}