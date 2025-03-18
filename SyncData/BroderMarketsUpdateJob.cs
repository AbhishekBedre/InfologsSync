using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OptionChain;
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SyncData
{
    public class NullableDecimalConverter : JsonConverter<decimal?>
    {
        public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string value = reader.GetString();
                if (value == "-")
                    return null;
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetDecimal();
            }

            return null;
        }

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteNumberValue(value.Value);
            else
                writer.WriteNullValue();
        }
    }


    public class BroderMarketsUpdateJob : IJob
    {
        private readonly ILogger<BroderMarketsUpdateJob> _logger;
        private readonly OptionDbContext _optionDbContext;
        private object counter = 0;
        private object stockCounter = 0;
        private double? previousCPEOIDiffValue = null; // To store the previous X value
        private double? previousCPEColDiffValue = null; // To store the previous X value

        public BroderMarketsUpdateJob(ILogger<BroderMarketsUpdateJob> log, OptionDbContext optionDbContext)
        {
            _logger = log;
            _optionDbContext = optionDbContext;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation($"{nameof(BroderMarketsUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));
            Utility.LogDetails($"{nameof(BroderMarketsUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));

            await GetBroderMarketData(context);

            Console.WriteLine($"{nameof(BroderMarketsUpdateJob)} completed successfully. Time: - " + context.FireTimeUtc.ToLocalTime());

            await Task.CompletedTask;
        }

        public async Task GetBroderMarketData(IJobExecutionContext context)
        {

        STEP:

            try
            {
                (bool status, object result, BroderMarketRoot broderMarketRoot) = await GetBroderMarketData(stockCounter, context);

                if (status == false && Convert.ToInt16(result) <= 3)
                {
                    await Task.Delay(2000);
                    stockCounter = result;

                    goto STEP;
                }

                if (Convert.ToInt32(stockCounter) <= 3)
                {
                    stockCounter = 0;
                    // Make a Db Call                    
                    await StoreBroderMarketDataInTable(broderMarketRoot, context);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Multiple tried for stock data but not succeed. counter: {stockCounter}");
                stockCounter = 0;

                Utility.LogDetails($"{nameof(GetBroderMarketData)} Exception: {ex.Message}");
            }
        }

        private async Task<(bool, object, BroderMarketRoot)> GetBroderMarketData(object counter, IJobExecutionContext context)
        {
            Utility.LogDetails($"{nameof(GetBroderMarketData)} -> Send quots reqest counter:" + counter + ", Time: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm"));

            bool status = true;
            BroderMarketRoot broderMarketRoot = new BroderMarketRoot();
            string sessionCookie = "";

            _logger.LogInformation($"Exection time: {counter}");


            HttpClientHandler httpClientHandler = new HttpClientHandler();

            // Enable automatic decompression for gzip, deflate, and Brotli
            httpClientHandler.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                             System.Net.DecompressionMethods.Deflate |
                                             System.Net.DecompressionMethods.Brotli;

            using (HttpClient client = new HttpClient(httpClientHandler))
            {
                await Common.UpdateCookieAndHeaders(client, _optionDbContext, JobType.BroderMarketUpdate);

                string url = "https://www.nseindia.com/api/allIndices";

                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonContent = await response.Content.ReadAsStringAsync();

                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                            Converters = { new NullableDecimalConverter() }
                        };
                        broderMarketRoot = JsonSerializer.Deserialize<BroderMarketRoot>(jsonContent, options);

                        if (broderMarketRoot == null || broderMarketRoot.Data == null)
                        {
                            _logger.LogInformation("Failed to parse JSON content.");
                            Utility.LogDetails($"{nameof(GetBroderMarketData)} -> Failed to parse JSON content.");
                            throw new Exception("Failed to parse JSON content.");
                        }
                    }
                    else
                    {
                        Utility.LogDetails($"{nameof(GetBroderMarketData)} -> HTTP Error: {response.StatusCode}.");
                        _logger.LogInformation($"HTTP Error: {response.StatusCode}");
                        throw new Exception($"Http Error: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    Utility.LogDetails($"{nameof(GetBroderMarketData)} -> Exception: {ex.Message}.");
                    _logger.LogInformation($"Exception: {ex.Message}");
                    counter = Convert.ToInt16(counter) + 1;
                    status = false;
                }
            }

            return (status, counter, broderMarketRoot);
        }

        private async Task<bool> StoreBroderMarketDataInTable(BroderMarketRoot? broderMarketRoot, IJobExecutionContext context)
        {
            try
            {
                _logger.LogInformation("Adding data to broder market table.");

                if (broderMarketRoot != null
                    && broderMarketRoot.Data != null)
                {
                    await _optionDbContext.Database.BeginTransactionAsync();

                    broderMarketRoot.Data.ForEach(f =>
                    {
                        f.EntryDate = DateTime.Now.Date;
                        f.Time = context.FireTimeUtc.ToLocalTime().TimeOfDay;                        
                    });

                    await _optionDbContext.BroderMarkets.AddRangeAsync(broderMarketRoot.Data);

                    await _optionDbContext.SaveChangesAsync();

                    await _optionDbContext.Database.CommitTransactionAsync();
                }
            }
            catch (Exception ex)
            {
                Utility.LogDetails($"{nameof(StoreBroderMarketDataInTable)} -> Exception: {ex.Message}.");
                await _optionDbContext.Database.RollbackTransactionAsync();
                return false;
            }

            return true;
        }
    }
}
