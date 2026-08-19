using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Hourglass.Services;

namespace Hourglass.ViewModels;

/// <summary>Drives the update window: show what is new, download it, hand over the swap.</summary>
public sealed class UpdateViewModel : ViewModelBase
{
    private readonly UpdateService _updates;
    private readonly UpdateInfo _info;

    private CancellationTokenSource? _cts;
    private double _progress;
    private string _statusMessage = "";
    private bool _isBusy;
    private bool _isFailed;

    public UpdateViewModel(UpdateService updates, UpdateInfo info)
    {
        _updates = updates;
        _info = info;

        InstallCommand = new AsyncRelayCommand(_ => InstallAsync(), _ => !IsBusy);
        LaterCommand = new RelayCommand(_ => Cancel());
    }

    /// <summary>True when the new build is in place and the app should restart.</summary>
    public event EventHandler<bool>? Completed;

    public ICommand InstallCommand { get; }
    public ICommand LaterCommand { get; }

    public string Headline => $"Доступна версия {_info.Version.Major}.{_info.Version.Minor}";

    public string CurrentVersionText => $"У вас {UpdateService.CurrentVersionText}";

    public string Notes => string.IsNullOrWhiteSpace(_info.Notes)
        ? "Описание изменений не указано."
        : _info.Notes.Trim();

    public string SizeText => $"Загрузка {_info.Size / 1024 / 1024} МБ";

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsFailed
    {
        get => _isFailed;
        private set => SetProperty(ref _isFailed, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
                OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => StatusMessage.Length > 0;

    public void Cancel()
    {
        _cts?.Cancel();
        Completed?.Invoke(this, false);
    }

    private async Task InstallAsync()
    {
        IsBusy = true;
        IsFailed = false;
        StatusMessage = "Скачиваем…";
        Progress = 0;

        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));

        try
        {
            var progress = new Progress<double>(value => Progress = value);
            var file = await _updates.DownloadAsync(_info, progress, _cts.Token).ConfigureAwait(true);

            StatusMessage = "Готово. Программа закроется и откроется уже обновлённой.";
            _updates.ApplyAndRestart(file);

            Completed?.Invoke(this, true);
        }
        catch (UpdateException ex)
        {
            Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            Fail("Загрузка прервана.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Fail("Не удалось скачать обновление: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string message)
    {
        IsFailed = true;
        StatusMessage = message;
        Progress = 0;
    }
}
