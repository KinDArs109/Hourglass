using System.Collections.ObjectModel;
using System.Windows.Input;
using Hourglass.Models;
using Hourglass.Services;
using Hourglass.Utilities;

namespace Hourglass.ViewModels;

/// <summary>One row in the achievement list.</summary>
public sealed class AchievementViewModel : ViewModelBase
{
    private bool _isUnlocked;

    public AchievementViewModel(Achievement achievement)
    {
        ApiName = achievement.ApiName;
        Title = achievement.Title;
        Description = achievement.Description;
        CanChange = achievement.CanChange;
        WasUnlocked = achievement.IsUnlocked;
        _isUnlocked = achievement.IsUnlocked;
    }

    public string ApiName { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>False for the ones only a game server may grant.</summary>
    public bool CanChange { get; }

    /// <summary>State when the list was read, for spotting what the user changed.</summary>
    public bool WasUnlocked { get; }

    public bool IsUnlocked
    {
        get => _isUnlocked;
        set
        {
            if (!CanChange)
            {
                OnPropertyChanged();
                return;
            }

            SetProperty(ref _isUnlocked, value);
        }
    }

    public string Note => CanChange ? Description : "Выдаёт только сервер игры";
}

/// <summary>
/// Backs the achievements window: pick a game, see what it has, tick what should be
/// there, apply. Reading and writing both go over the account's live Steam connection,
/// so the account has to be running.
/// </summary>
public sealed class AchievementsViewModel : ViewModelBase
{
    private readonly AccountViewModel _account;

    private AchievementSet? _set;
    private OwnedGame? _selectedGame;
    private string _search = "";
    private string _status = "";
    private bool _isStatusBad;
    private bool _isBusy;

    public AchievementsViewModel(AccountViewModel account)
    {
        _account = account;

        LoadCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => SelectedGame is not null && !IsBusy);
        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => HasAchievements && !IsBusy);
        MarkAllCommand = new RelayCommand(_ => MarkAll(true), _ => HasAchievements);
        ClearAllCommand = new RelayCommand(_ => MarkAll(false), _ => HasAchievements);

        RefreshGames();

        if (!_account.IsSignedOn)
            SetStatus("Аккаунт не в сети. Запустите его — без этого Steam не отдаст достижения.", isBad: true);
    }

    public ObservableCollection<OwnedGame> Games { get; } = new();

    public ObservableCollection<AchievementViewModel> Achievements { get; } = new();

    public ICommand LoadCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand MarkAllCommand { get; }
    public ICommand ClearAllCommand { get; }

    public string AccountName => _account.DisplayName;

    public bool HasAchievements => Achievements.Count > 0;

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
                RefreshGames();
        }
    }

    public OwnedGame? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value))
                return;

            CommandManager.InvalidateRequerySuggested();

            if (value is not null)
                AsyncHelper.FireAndForget(LoadAsync, nameof(LoadAsync));
        }
    }

    public string Summary => Achievements.Count == 0
        ? ""
        : $"Всего {Achievements.Count} · выдано {Achievements.Count(item => item.IsUnlocked)}";

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => Status.Length > 0;

    public bool IsStatusBad
    {
        get => _isStatusBad;
        private set => SetProperty(ref _isStatusBad, value);
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

    private void RefreshGames()
    {
        var needle = Search.Trim();

        var matches = _account.Library
            .Where(game => needle.Length == 0 ||
                           game.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(500)
            .ToList();

        Games.Clear();
        foreach (var game in matches)
            Games.Add(game);
    }

    private async Task LoadAsync()
    {
        if (SelectedGame is not { } game)
            return;

        IsBusy = true;
        Achievements.Clear();
        _set = null;

        SetStatus($"Читаем достижения «{game.Name}»…", isBad: false);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            _set = await _account.GetAchievementsAsync(game.AppId, timeout.Token).ConfigureAwait(true);

            foreach (var achievement in _set.Achievements.OrderBy(item => item.Title,
                         StringComparer.CurrentCultureIgnoreCase))
                Achievements.Add(new AchievementViewModel(achievement));

            var locked = Achievements.Count(item => !item.CanChange);
            SetStatus(
                locked > 0
                    ? $"Готово. {locked} из них выдаёт только сервер игры — их изменить нельзя."
                    : "Готово. Отметьте нужные и нажмите «Применить».",
                isBad: false);
        }
        catch (AchievementException ex)
        {
            SetStatus(ex.Message, isBad: true);
        }
        catch (Exception ex)
        {
            SetStatus("Не получилось: " + ex.Message, isBad: true);
        }
        finally
        {
            IsBusy = false;
            RaiseListProperties();
        }
    }

    private async Task ApplyAsync()
    {
        if (_set is null)
            return;

        IsBusy = true;

        try
        {
            var wanted = Achievements
                .Where(item => item.IsUnlocked)
                .Select(item => item.ApiName)
                .ToHashSet(StringComparer.Ordinal);

            var changed = _account.SetAchievements(_set, wanted);

            if (changed == 0)
            {
                SetStatus("Менять нечего — всё уже так, как отмечено.", isBad: false);
                return;
            }

            SetStatus($"Отправлено в Steam: изменено {changed}. Перечитываем…", isBad: false);

            // Steam is the judge of what actually stuck, so the list is read back rather
            // than assumed. A refused achievement will simply come back as it was.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (AchievementException ex)
        {
            SetStatus(ex.Message, isBad: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void MarkAll(bool unlocked)
    {
        foreach (var achievement in Achievements.Where(item => item.CanChange))
            achievement.IsUnlocked = unlocked;

        OnPropertyChanged(nameof(Summary));
    }

    private void RaiseListProperties()
    {
        OnPropertyChanged(nameof(HasAchievements));
        OnPropertyChanged(nameof(Summary));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetStatus(string message, bool isBad)
    {
        Status = message;
        IsStatusBad = isBad;
    }
}
