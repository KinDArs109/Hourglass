using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Hourglass.Models;
using Hourglass.Services;
using Hourglass.Utilities;

namespace Hourglass.ViewModels;

public sealed class PickableGameViewModel : ViewModelBase
{
    private bool _isSelected;
    private ImageSource? _capsule;

    public PickableGameViewModel(
        uint appId, string name, long playtimeMinutes, bool isAlreadyAdded, CapsuleCache capsules)
    {
        AppId = appId;
        Name = name;
        PlaytimeMinutes = playtimeMinutes;
        IsAlreadyAdded = isAlreadyAdded;

        AsyncHelper.FireAndForget(async () =>
        {
            var image = await capsules.GetAsync(appId).ConfigureAwait(true);
            if (image is not null)
                Capsule = image;
        }, $"Capsule:{appId}");
    }

    public uint AppId { get; }

    public string Name { get; }

    public long PlaytimeMinutes { get; }

    public bool IsAlreadyAdded { get; }

    /// <summary>Store art, or null while it loads or when the title has none.</summary>
    public ImageSource? Capsule
    {
        get => _capsule;
        private set => SetProperty(ref _capsule, value);
    }

    public string SubtitleText => IsAlreadyAdded
        ? $"AppID {AppId} · уже добавлена"
        : $"AppID {AppId} · в Steam {TimeFormat.Compact(TimeSpan.FromMinutes(PlaytimeMinutes))}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsAlreadyAdded)
                return;

            SetProperty(ref _isSelected, value);
        }
    }
}

public sealed class GamePickerViewModel : ViewModelBase
{
    private readonly AccountViewModel _account;
    private readonly HashSet<uint> _alreadyAdded;
    private readonly ICollectionView _view;
    private readonly List<GameConfig> _manualPicks = new();

    private string _searchText = "";
    private string _manualAppId = "";
    private string _manualName = "";
    private string _statusMessage = "";
    private bool _isRefreshing;

    public GamePickerViewModel(AccountViewModel account)
    {
        _account = account;
        _alreadyAdded = account.Games.Select(game => game.AppId).ToHashSet();

        _view = CollectionViewSource.GetDefaultView(Items);
        _view.Filter = Matches;

        FillItems();

        if (Items.Count == 0)
        {
            StatusMessage = "Библиотека ещё не прочитана. Steam отдаёт список игр при каждом входе — " +
                            "нажмите «Запустить» или «Войти заново». Пока можно добавить игру по AppID вручную.";
        }

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsRefreshing);
        AddManualCommand = new RelayCommand(_ => AddManual(), _ => CanAddManual);
        ConfirmCommand = new RelayCommand(_ => Complete(BuildSelection()), _ => SelectedCount > 0);
        CancelCommand = new RelayCommand(_ => Complete(null));
    }

    public event EventHandler<IReadOnlyList<GameConfig>?>? Completed;

    public ObservableCollection<PickableGameViewModel> Items { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand AddManualCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public string AccountName => _account.DisplayName;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                _view.Refresh();
        }
    }

    public string ManualAppId
    {
        get => _manualAppId;
        set
        {
            if (SetProperty(ref _manualAppId, value))
                OnPropertyChanged(nameof(CanAddManual));
        }
    }

    public string ManualName
    {
        get => _manualName;
        set => SetProperty(ref _manualName, value);
    }

    public bool CanAddManual =>
        uint.TryParse(ManualAppId.Trim(), out var appId) && appId > 0 && !_alreadyAdded.Contains(appId);

    public bool ShowEmptyHint => Items.Count == 0 && !IsRefreshing;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetProperty(ref _isRefreshing, value))
                return;

            OnPropertyChanged(nameof(ShowEmptyHint));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Re-reads the library from Steam, for games bought since the last sign-in.</summary>
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        StatusMessage = "";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var before = Items.Count;

            if (!await _account.RefreshLibraryNowAsync(timeout.Token).ConfigureAwait(true))
            {
                StatusMessage = "Чтобы обновить список, аккаунт должен быть запущен — нажмите «Запустить».";
                return;
            }

            FillItems();

            var added = Items.Count - before;
            StatusMessage = added > 0
                ? $"Список обновлён, новых игр: {added}"
                : "Список обновлён, новых игр нет.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Steam не ответил вовремя. Попробуйте ещё раз.";
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(SelectedCount));
        }
    }

    /// <summary>Rebuilds the list from the account's cache, keeping current selections.</summary>
    private void FillItems()
    {
        var selected = Items.Where(item => item.IsSelected).Select(item => item.AppId).ToHashSet();

        foreach (var item in Items)
            item.PropertyChanged -= OnItemChanged;

        Items.Clear();

        foreach (var game in _account.Library)
        {
            var item = new PickableGameViewModel(
                game.AppId, game.Name, game.PlaytimeMinutes,
                _alreadyAdded.Contains(game.AppId), _account.Capsules)
            {
                IsSelected = selected.Contains(game.AppId)
            };

            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }

        _view.Refresh();
        OnPropertyChanged(nameof(ShowEmptyHint));
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

    public int SelectedCount => Items.Count(item => item.IsSelected) + _manualPicks.Count;

    private void AddManual()
    {
        if (!uint.TryParse(ManualAppId.Trim(), out var appId) || appId == 0)
            return;

        if (!_alreadyAdded.Add(appId))
            return;

        var name = ManualName.Trim();
        _manualPicks.Add(new GameConfig
        {
            AppId = appId,
            Name = name.Length > 0 ? name : $"AppID {appId}",
            IsEnabled = true
        });

        ManualAppId = "";
        ManualName = "";
        StatusMessage = $"Добавлено вручную: {_manualPicks.Count}";
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanAddManual));
    }

    private IReadOnlyList<GameConfig> BuildSelection()
    {
        var selection = Items
            .Where(item => item.IsSelected)
            .Select(item => new GameConfig
            {
                AppId = item.AppId,
                Name = item.Name,
                IsEnabled = true,
                SteamMinutes = item.PlaytimeMinutes
            })
            .ToList();

        selection.AddRange(_manualPicks);
        return selection;
    }

    private void Complete(IReadOnlyList<GameConfig>? selection) => Completed?.Invoke(this, selection);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PickableGameViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectedCount));
    }

    private bool Matches(object candidate)
    {
        if (SearchText.Trim() is not { Length: > 0 } query)
            return true;

        if (candidate is not PickableGameViewModel game)
            return false;

        return game.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || game.AppId.ToString().StartsWith(query, StringComparison.Ordinal);
    }
}
