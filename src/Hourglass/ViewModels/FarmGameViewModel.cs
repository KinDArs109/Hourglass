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

    public bool IsReady => HoursPlayed >= CardFarmPlanner.HoursBeforeDropsBegin;

    public string DropsText => $"{DropsRemaining} {Plural.Of(DropsRemaining, "карточка", "карточки", "карточек")}";

    public string StateText => IsActive
        ? "крутится сейчас"
        : IsReady
            ? "ждёт очереди"
            : $"нужно ещё {Math.Max(0, CardFarmPlanner.HoursBeforeDropsBegin - HoursPlayed):0.#} ч";

    public string HoursText => $"в Steam {HoursPlayed:0.#} ч";
}
