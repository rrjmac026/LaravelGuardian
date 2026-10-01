using System.Configuration;
using System.Data;
using System.Windows;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Services;
using LaravelGuardian.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.IO;


namespace LaravelGuardian.UI;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaravelGuardian", "logs");

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(logDir, "guardian-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI thread crash");
            MessageBox.Show(args.Exception.ToString(), "Guardian crashed (UI)");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Background thread crash");
            MessageBox.Show(args.ExceptionObject.ToString(), "Guardian crashed (background)");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(s =>
            {
                s.AddSingleton<IProjectScanner, ProjectScanner>();
                s.AddSingleton<IToolDetector, ToolDetector>();
                s.AddSingleton<MainViewModel>();
                s.AddSingleton<MainWindow>();
                s.AddSingleton<IProcessManager, ProcessManager>();
                s.AddSingleton<IEnvironmentManager, EnvironmentManager>();
                s.AddSingleton<IArtisanRunner, ArtisanRunner>();
                s.AddSingleton<INativeTestRunner, NativeTestRunner>();
                s.AddSingleton<IRouteScanner, RouteScanner>();
                s.AddSingleton<IHttpCheckRunner, HttpCheckRunner>();
            })
            .Build();

        await _host.StartAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.Services.GetRequiredService<IProcessManager>().StopAllAsync().GetAwaiter().GetResult();
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}