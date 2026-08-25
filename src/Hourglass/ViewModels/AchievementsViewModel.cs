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
    private bool _isRefused;

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

    /// <summary>Steam says outright that only a game server may set this one.</summary>
    public bool IsProtected => !CanChange;

    /// <summary>
    /// Set after an attempt that Steam quietly ignored. The schema does not always
    /// admit which achievements are out of reach, so the refusal is noticed by asking
    /// Steam again and seeing what came back unchanged.
    /// </summary>
    public bool IsRefused
    {
        get => _isRefused;
        set
        {
            if (SetProperty(ref _isRefused, value))
                OnPropertyChanged(nameof(IsBlocked));
        }
    }

    /// <summary>Either kind of "cannot be granted", for the list to colour in.</summary>
    public bool IsBlocked => IsProtected || IsRefused;

    public string BlockedNote => IsProtected
        ? "Выдать нельзя — это достижение ставит сервер игры"
        : "Steam не принял — выдать нельзя";
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
    private IReadOnlySet<string>? _requested;
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

    public string Summary
    {
        get
        {
            if (Achievements.Count == 0)
                return "";

            var summary = $"Всего {Achievements.Count} · выдано {Achievements.Count(item => item.IsUnlocked)}";
            var locked = Achievements.Count(item => item.IsBlocked);

            return locked > 0 ? summary + $" · нельзя выдать {locked}" : summary;
        }
    }

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

            // The ones that can be granted come first: the rest are there to be seen,
            // not acted on.
            foreach (var achievement in _set.Achievements
                         .OrderBy(item => item.IsProtected)
                         .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
                Achievements.Add(new AchievementViewModel(achievement));

            MarkRefused();

            var locked = Achievements.Count(item => item.IsBlocked);
            SetStatus(
                locked > 0
                    ? $"Готово. Красным помечены {locked} — их Steam выдать не даёт."
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
            // than assumed. Anything that comes back unchanged was refused.
            _requested = wanted;

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

    /// <summary>
    /// Compares what was asked for with what Steam gave back. Whatever did not move is
    /// out of reach, whatever the schema claimed about it.
    /// </summary>
    private void MarkRefused()
    {
        if (_requested is null)
            return;

        foreach (var achievement in Achievements)
        {
            var asked = _requested.Contains(achievement.ApiName);
            achievement.IsRefused = asked != achievement.IsUnlocked;
        }

        _requested = null;
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
