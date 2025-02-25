using Amazon.Lambda.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OptionChain;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace aws_session_sync_net8;

public class Function
{
    private readonly OptionDbContext _optionDbContext;

    // Constructor injection
    public Function(OptionDbContext optionDbContext)
    {
        _optionDbContext = optionDbContext;
    }

    /// <summary>
    /// A simple function that takes a string and does a ToUpper
    /// </summary>
    /// <param name="input">The event for the Lambda function handler to process.</param>
    /// <param name="context">The ILambdaContext that provides methods for logging and describing the Lambda environment.</param>
    /// <returns></returns>
    public string FunctionHandler(string input, ILambdaContext context)
    {
        string sessionCookie =  ReadSession();

        var sessionRecord = _optionDbContext.Sessions.Where(x => x.Id > 0).FirstOrDefault();

        if (sessionRecord == null)
        {
            _optionDbContext.Sessions.AddAsync(new Sessions
            {
                Cookie = sessionCookie,
                UpdatedDate = DateTime.Now
            });
        }
        else
        {
            sessionRecord.Cookie = sessionCookie;
            sessionRecord.UpdatedDate = DateTime.Now;
        }

        _optionDbContext.SaveChanges();
        return sessionCookie;
    }

    public string ReadSession()
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
                HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();

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
                return ex.Message;
            }
        }

        return finalCookie;
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
        services.AddDbContext<OptionDbContext>(x => x.UseSqlServer("Data Source=103.83.81.7;Initial Catalog=karmajew_optionchain;User Id=karmajew_sa;Password=Prokyonz@2023;TrustServerCertificate=True"));
        services.AddTransient<Function>();
    }

    public static string Handler(string input, ILambdaContext context)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var function = scope.ServiceProvider.GetRequiredService<Function>();
            return function.FunctionHandler(input, context);
        }
    }
}

