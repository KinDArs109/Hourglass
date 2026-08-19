namespace Hourglass.Services;

/// <summary>What a session should report to Steam. Immutable so it can be swapped atomically.</summary>
public sealed record BoostPlan(
    IReadOnlyList<uint> AppIds,
    string? CustomStatus,
    bool ShowOnline,
    bool PauseWhenClientRuns)
{
    /// <summary>Steam ignores anything past the 32nd entry of a games-played message.</summary>
    public const int MaxGames = 32;
}
