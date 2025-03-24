using Infologs.SessionReader;
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
        private ICacheHelper _cacheHelper;
        private object counter = 0;

        public SessionUpdateJob(ILogger<FetchAndProcessJob> log, OptionDbContext optionDbContext, 
            ICacheHelper cacheHelper)
        {
            _logger = log;
            _optionDbContext = optionDbContext;
            _cacheHelper = cacheHelper;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation($"{nameof(SessionUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));
            //Utility.LogDetails($"{nameof(SessionUpdateJob)} Started: " + context.FireTimeUtc.ToLocalTime().ToString("hh:mm:ss"));

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

                //Utility.LogDetails($"{nameof(ExecuteSessionUpdate)} Exception: {ex.Message}");
            }
        }

        public async Task<(bool Status, object Counter, string Cookie)> GetSessionUpdate(object counter,
            IJobExecutionContext context)
        {
            bool status = true;
            string cookie = "";

            try
            {
                DataReader dataReader = new DataReader(_optionDbContext, _cacheHelper);
                await dataReader.ReadSessionAsync();                
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
    }
}
