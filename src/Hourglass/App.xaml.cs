using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Hourglass.Services;
using Hourglass.Services.Interfaces;
using Hourglass.Utilities;
using Hourglass.ViewModels;
using Hourglass.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Hourglass;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\HourglassSingleInstance";
    private const string ActivationPipeName = "Hourglass-Activate";

    private ServiceProvider? _services;
    private Views.MainWindow? _mainWindow;
    private Mutex? _instanceMutex;
    private CancellationTokenSource? _pipeCancellation;

    public App()
    {
        InitializeComponent();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        var store = _services.GetRequiredService<IConfigStore>();
        store.Load();

        _mainWindow = _services.GetRequiredService<Views.MainWindow>();
        MainWindow = _mainWindow;

        var viewModel = _services.GetRequiredService<MainViewModel>();
        viewModel.Initialize();

        var startHidden = e.Args.Any(argument =>
                              argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                          || store.Config.StartMinimized;

        if (!startHidden)
            _mainWindow.Show();

        StartActivationListener();

        // Start and finish both leave a mark. Without them a program that is gone in the
        // morning is indistinguishable from one that was closed on purpose, and the
        // journal is the only witness there is.
        _services.GetRequiredService<IAppLogger>().Info(
            AppLogScopes.App, $"Программа запущена, версия {UpdateService.CurrentVersionText}");

        SessionEnding += OnSessionEnding;

        base.OnStartup(e);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) =>
        _services?.GetService<IAppLogger>()?.Info(
            AppLogScopes.App,
            e.ReasonSessionEnding == ReasonSessionEnding.Shutdown
                ? "Windows выключается — закрываюсь"
                : "Выход из учётной записи Windows — закрываюсь");

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<IAppLogger>()?.Info(AppLogScopes.App, "Программа закрыта");

        _pipeCancellation?.Cancel();
        _pipeCancellation?.Dispose();

        _services?.GetService<SystemTrayService>()?.Dispose();
        _services?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppLogger, AppLogger>();
        services.AddSingleton<IConfigStore, ConfigStore>();
        services.AddSingleton<SteamRuntime>();
        services.AddSingleton<SteamClientWatcher>();
        services.AddSingleton<SteamLoginService>();
        services.AddSingleton<CapsuleCache>();
        services.AddSingleton<AutoStartService>();
        services.AddSingleton<TelegramBotService>();
        services.AddSingleton<CardFarmService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<SystemTrayService>();
        services.AddSingleton<SleepBlocker>();
        services.AddSingleton<IDialogService, DialogService>();

        services.AddHttpClient(HttpClients.SteamApi, client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Hourglass/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // GitHub rejects requests without a User-Agent and wants the versioned API media type.
        services.AddHttpClient(HttpClients.GitHub, client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Hourglass-Updater");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            client.Timeout = TimeSpan.FromMinutes(10);
        });

        services.AddSingleton<Func<string, SteamBoostSession>>(provider => username =>
            new SteamBoostSession(
                username,
                provider.GetRequiredService<IAppLogger>(),
                provider.GetRequiredService<SteamClientWatcher>(),
                provider.GetRequiredService<SteamRuntime>().Configuration));

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<Views.MainWindow>();
    }

    // ------------------------------------------------------- single instance

    private void StartActivationListener()
    {
        _pipeCancellation = new CancellationTokenSource();
        var token = _pipeCancellation.Token;

        AsyncHelper.FireAndForget(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        ActivationPipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(() => _mainWindow?.RestoreFromTray());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
        }, nameof(StartActivationListener));
    }

    private static void SignalRunningInstance()
    {
        SafeExec.Try(() =>
        {
            using var client = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out);
            client.Connect(2000);
        });
    }

    // ------------------------------------------------------- crash handling

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("Dispatcher", e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        LogCrash("AppDomain", e.ExceptionObject as Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Task", e.Exception);
        e.SetObserved();
    }

    private void LogCrash(string source, Exception? exception)
    {
        var logger = _services?.GetService<IAppLogger>();
        if (logger is not null)
        {
            logger.Error(AppLogScopes.App, $"Необработанная ошибка ({source})", exception);
            return;
        }

        SafeExec.Try(() =>
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(
                Path.Combine(AppPaths.DataDirectory, "crash.log"),
                $"{Environment.NewLine}[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}");
        });
    }
}
