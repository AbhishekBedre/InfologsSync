using Amazon.Lambda.Core;
using Infologs.SessionReader;
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
        SessionReader sessionReader = new SessionReader(_optionDbContext);

        var cookie = sessionReader.ReadSession().GetAwaiter().GetResult();

        return cookie;
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

