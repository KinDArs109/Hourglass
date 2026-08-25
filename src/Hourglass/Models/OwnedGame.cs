namespace Hourglass.Models;

/// <summary>
/// A game the account can play. <paramref name="HasCards"/> comes from Steam's own
/// product data: it is what tells the card farmer that a game is worth starting at all,
/// including the free ones that were never launched and so have no badge yet.
/// </summary>
public sealed record OwnedGame(uint AppId, string Name, long PlaytimeMinutes, bool HasCards = false);
