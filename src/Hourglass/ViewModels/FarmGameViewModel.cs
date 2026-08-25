using System.Windows.Media;
using Hourglass.Services;
using Hourglass.Utilities;

namespace Hourglass.ViewModels;

/// <summary>One row on the card-farming page: what Steam owes and where it stands.</summary>
public sealed class FarmGameViewModel : ViewModelBase
{
    private ImageSource? _capsule;

    public FarmGameViewModel(CardBadge badge, bool isActive, CapsuleCache capsules)
    {
        AppId = badge.AppId;
        Name = badge.Name;
        DropsRemaining = badge.DropsRemaining;
        HoursPlayed = badge.HoursPlayed;
        IsActive = isActive;
        IsUnstarted = badge.IsUnstarted;

        AsyncHelper.FireAndForget(async () =>
        {
            var image = await capsules.GetAsync(badge.AppId).ConfigureAwait(true);
            if (image is not null)
                Capsule = image;
        }, $"Capsule:{badge.AppId}");
    }

    public uint AppId { get; }

    public string Name { get; }

    public int DropsRemaining { get; }

    public double HoursPlayed { get; }

    /// <summary>True while this game is one of the ones actually being idled now.</summary>
    public bool IsActive { get; }

    public ImageSource? Capsule
    {
        get => _capsule;
        private set => SetProperty(ref _capsule, value);
    }

    public bool IsReady => !IsUnstarted && HoursPlayed >= CardFarmPlanner.HoursBeforeDropsBegin;

    /// <summary>The game has cards but was never launched, so Steam owes no count yet.</summary>
    public bool IsUnstarted { get; }

    public string DropsText => IsUnstarted
        ? "карточки есть"
        : $"{DropsRemaining} {Plural.Of(DropsRemaining, "карточка", "карточки", "карточек")}";

    public string StateText => IsActive
        ? "крутится сейчас"
        : IsReady
            ? "ждёт очереди"
            : IsUnstarted
                ? "ещё не начинали"
                : $"нужно ещё {Math.Max(0, CardFarmPlanner.HoursBeforeDropsBegin - HoursPlayed):0.#} ч";

    public string HoursText => IsUnstarted && HoursPlayed <= 0
        ? "в Steam ещё не запускали"
        : $"в Steam {HoursPlayed:0.#} ч";
}
