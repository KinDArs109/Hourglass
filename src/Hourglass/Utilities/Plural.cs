namespace Hourglass.Utilities;

public static class Plural
{
    /// <summary>Picks the Russian form: 1 игра, 2 игры, 5 игр.</summary>
    public static string Of(int count, string one, string few, string many)
    {
        var tail = Math.Abs(count) % 100;
        if (tail is >= 11 and <= 14)
            return many;

        return (tail % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    public static string Games(int count) => $"{count} {Of(count, "игра", "игры", "игр")}";

    public static string Accounts(int count) => $"{count} {Of(count, "аккаунт", "аккаунта", "аккаунтов")}";
}
