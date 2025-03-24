using Microsoft.Extensions.Logging;
using OptionChain;
using OptionChain.Models;
using Quartz;

using Infologs.SessionReader;

public class FiiDiiActivityJob : IJob
{
    private readonly ILogger<FiiDiiActivityJob> _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly OptionDbContext _optionDbContext;
    private object counter = 0;

    public FiiDiiActivityJob(ILogger<FiiDiiActivityJob> log, OptionDbContext optionDbContext, 
        ICacheHelper cacheHelper)
    {
        _logger = log;
        _cacheHelper = cacheHelper;
        _optionDbContext = optionDbContext;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation($"{nameof(FiiDiiActivityJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));
        Utility.LogDetails($"{nameof(FiiDiiActivityJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));

        await ExecuteFIIDIIActivityJob(context);

        Console.WriteLine($"{nameof(ExecuteFIIDIIActivityJob)} completed successfully. Time: - " + context.FireTimeUtc.ToLocalTime());

        await Task.CompletedTask;
    }
    public async Task ExecuteFIIDIIActivityJob(IJobExecutionContext context)
    {
    STEP:

        try
        {
            var fiidiiResult = await ReadFIIDIIActvity(counter, context);

            if (fiidiiResult.Status == false && Convert.ToInt16(fiidiiResult.Counter) <= 3)
            {
                await Task.Delay(2000);
                counter = fiidiiResult.Counter;

                goto STEP;
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Multiple tried for stock data but not succeed. counter: {counter}");
            counter = 0;
        }
    }

    public async Task<(bool Status, object Counter)> ReadFIIDIIActvity(object counter,
            IJobExecutionContext context)
    {
        bool status = true;

        try
        {
            DataReader dataReader = new DataReader(_optionDbContext, _cacheHelper);

            status = await dataReader.ReadFIIDIIActvityAsync();
        }
        catch (Exception ex)
        {
            counter = Convert.ToInt16(counter) + 1;
            status = false;
        }

        return (status, counter);
    }
}