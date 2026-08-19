namespace Hourglass.Models;

public sealed record OwnedGame(uint AppId, string Name, long PlaytimeMinutes);
