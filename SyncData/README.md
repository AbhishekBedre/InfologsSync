using Microsoft.Extensions.Hosting;
using Quartz;
public class Program
{
	public static void Main(string[] args)
	{
		CreateHostBuilder(args).Build().Run();
	}
	public static IHostBuilder CreateHostBuilder(string[] args) =>
		Host.CreateDefaultBuilder(args).UseWindowsService()
			.ConfigureServices((hostContext, services) =>
			{				
				// Add the required Quartz.NET services
				services.AddQuartz(q =>
				{
					var test = JobKey.Create("Test");
					q.AddJob<Test>(test).AddTrigger(trigger =>
					{
						trigger.ForJob(test).WithSimpleSchedule(s => s.WithIntervalInSeconds(5).RepeatForever());
					});
				});
				// Add the Quartz.NET hosted service
				services.AddQuartzHostedService(
					q => q.WaitForJobsToComplete = true);				
			});
}
public class Test : IJob
{
	public async Task Execute(IJobExecutionContext context)
	{
		File.AppendAllText("D:\\Test.txt", DateTime.Now.ToLongTimeString() + Environment.NewLine);
		Console.WriteLine(DateTime.Now);
		await Task.CompletedTask;
	}
}

<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Hosting.WindowsServices" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="8.0.0" />
    <PackageReference Include="Quartz" Version="3.13.1" />
    <PackageReference Include="Quartz.Extensions.Hosting" Version="3.13.1" />
  </ItemGroup>
</Project> (edited) 

