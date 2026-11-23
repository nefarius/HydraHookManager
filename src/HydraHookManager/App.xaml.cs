using System.IO;
using System.Net.Http.Headers;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using HydraHookManager.Services;
using HydraHookManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Wpf.Ui;

namespace HydraHookManager;

public partial class App : Application
{
    private IHost? _host;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        LoggerConfiguration logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(AppPaths.LogDirectory, "HydraHookManager-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14);
#if DEBUG
        logger.MinimumLevel.Debug();
#endif
        Log.Logger = logger.CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Log.Error(args.Exception, "Unobserved task exception");

        Log.Information("HydraHook Manager {Version} starting", GitHubUpdateCheckService.GetLocalVersionString());

        _host = Host.CreateDefaultBuilder()
            .UseSerilog(Log.Logger)
            .ConfigureServices(ConfigureServices)
            .Build();

        MainWindow window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ICommandRunner, CliWrapCommandRunner>();
        services.AddSingleton<IInjectorService, InjectorService>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IElevationService, ElevationService>();
        services.AddSingleton<IUpdateCheckService, GitHubUpdateCheckService>();

        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<INotificationService, SnackbarNotificationService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        services.AddHttpClient(GitHubUpdateCheckService.ClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HydraHookManager", "1"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddStandardResilienceHandler();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        Log.Information("HydraHook Manager exiting");
        _host?.Dispose();
        Log.CloseAndFlush();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled dispatcher exception");
    }
}
