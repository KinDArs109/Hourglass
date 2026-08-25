using System.IO;
using SteamKit2;
using SteamKit2.Internal;

namespace Hourglass.Services;

/// <summary>One achievement as Steam describes it, plus where its bit lives.</summary>
public sealed record Achievement(
    string ApiName,
    string Title,
    string Description,
    uint StatId,
    int Bit,
    bool IsUnlocked,
    bool IsProtected)
{
    /// <summary>Steam refuses to let the client set these; only the game server can.</summary>
    public bool CanChange => !IsProtected;
}

/// <summary>Everything one game's achievements need, in the shape Steam wants it back.</summary>
public sealed record AchievementSet(
    uint AppId,
    IReadOnlyList<Achievement> Achievements,
    IReadOnlyDictionary<uint, uint> StatValues,
    uint Crc);

/// <summary>
/// Reads and writes achievements over the same signed-in connection the boost uses.
///
/// Steam keeps achievements as bits packed into ordinary integer stats: the schema says
/// which stat holds which achievement and at which bit, and the stats say what is set.
/// Unlocking one therefore means flipping a bit in a number and handing the number back.
/// The schema arrives as binary key-values, which is why it is parsed rather than read.
/// </summary>
public sealed class AchievementHandler : ClientMsgHandler
{
    /// <summary>
    /// How the schema names a block of achievement bits. Steam spells it out rather
    /// than sending the number the documentation suggests, so both are accepted.
    /// </summary>
    private static readonly string[] AchievementTypes = { "ACHIEVEMENTS", "GROUPACHIEVEMENTS", "4", "5" };

    /// <summary>Bit 1 of an achievement's permission marks it as server-only.</summary>
    private const uint ProtectedPermission = 2;

    private readonly object _gate = new();
    private readonly Dictionary<ulong, TaskCompletionSource<CMsgClientGetUserStatsResponse>> _pending = new();

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType != EMsg.ClientGetUserStatsResponse)
            return;

        var response = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packetMsg);

        lock (_gate)
        {
            if (!_pending.Remove(packetMsg.TargetJobID, out var waiting))
                return;

            waiting.TrySetResult(response.Body);
        }
    }

    public async Task<AchievementSet> FetchAsync(uint appId, ulong steamId, CancellationToken cancellationToken)
    {
        var request = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats)
        {
            SourceJobID = Client.GetNextJobID()
        };

        request.Body.game_id = appId;
        request.Body.steam_id_for_user = steamId;
        request.Body.schema_local_version = -1;
        request.Body.crc_stats = 0;

        var waiting = new TaskCompletionSource<CMsgClientGetUserStatsResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
            _pending[request.SourceJobID] = waiting;

        Client.Send(request);

        CMsgClientGetUserStatsResponse body;
        try
        {
            body = await waiting.Task
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            lock (_gate)
                _pending.Remove(request.SourceJobID);

            throw new AchievementException("Steam не прислал достижения этой игры.");
        }

        if (body.schema is null || body.schema.Length == 0)
            throw new AchievementException("У этой игры нет достижений или Steam их не отдал.");

        return Parse(appId, body);
    }

    /// <summary>
    /// Hands back what the achievements should look like — the whole picture, not the
    /// difference. Several achievements share one number, so anything less would send a
    /// number that undoes a change made a moment earlier.
    /// </summary>
    /// <returns>How many achievements actually changed.</returns>
    public int Apply(AchievementSet set, IReadOnlySet<string> unlocked, ulong steamId)
    {
        var values = set.StatValues.ToDictionary(pair => pair.Key, pair => pair.Value);
        var touched = new HashSet<uint>();
        var changed = 0;

        foreach (var achievement in set.Achievements.Where(achievement => achievement.CanChange))
        {
            values.TryGetValue(achievement.StatId, out var value);

            var mask = 1u << achievement.Bit;
            var wanted = unlocked.Contains(achievement.ApiName);
            var updated = wanted ? value | mask : value & ~mask;

            if (updated == value)
                continue;

            values[achievement.StatId] = updated;
            touched.Add(achievement.StatId);
            changed++;
        }

        if (changed == 0)
            return 0;

        var request = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2)
        {
            SourceJobID = Client.GetNextJobID()
        };

        request.Body.game_id = set.AppId;
        request.Body.settor_steam_id = steamId;
        request.Body.settee_steam_id = steamId;
        request.Body.explicit_reset = false;
        request.Body.crc_stats = set.Crc;

        foreach (var statId in touched)
        {
            request.Body.stats.Add(new CMsgClientStoreUserStats2.Stats
            {
                stat_id = statId,
                stat_value = values[statId]
            });
        }

        Client.Send(request);
        return changed;
    }

    private static AchievementSet Parse(uint appId, CMsgClientGetUserStatsResponse body)
    {
        var values = body.stats.ToDictionary(stat => stat.stat_id, stat => stat.stat_value);

        using var stream = new MemoryStream(body.schema);
        var schema = new KeyValue();
        if (!schema.TryReadAsBinary(stream))
            throw new AchievementException("Схему достижений прочитать не удалось.");

        var achievements = new List<Achievement>();

        foreach (var stat in schema["stats"].Children)
        {
            if (!AchievementTypes.Contains(stat["type"].Value ?? "", StringComparer.OrdinalIgnoreCase))
                continue;

            if (!uint.TryParse(stat.Name, out var statId))
                continue;

            values.TryGetValue(statId, out var value);

            foreach (var bit in stat["bits"].Children)
            {
                if (!int.TryParse(bit.Name, out var index))
                    continue;

                var apiName = bit["name"].Value ?? "";
                if (apiName.Length == 0)
                    continue;

                achievements.Add(new Achievement(
                    apiName,
                    Localized(bit["display"]["name"]) is { Length: > 0 } title ? title : apiName,
                    Localized(bit["display"]["desc"]) ?? "",
                    statId,
                    index,
                    (value & (1u << index)) != 0,
                    (bit["permission"].AsUnsignedInteger() & ProtectedPermission) != 0));
            }
        }

        if (achievements.Count == 0)
            throw new AchievementException("У этой игры нет достижений.");

        return new AchievementSet(appId, achievements, values, body.crc_stats);
    }

    /// <summary>
    /// Titles arrive either as a plain value or as a block of languages. Russian first,
    /// then English, then whatever is there — a name in the wrong language still beats
    /// an empty row.
    /// </summary>
    private static string? Localized(KeyValue node)
    {
        if (!string.IsNullOrEmpty(node.Value))
            return node.Value;

        return node["russian"].Value
               ?? node["english"].Value
               ?? node.Children.FirstOrDefault()?.Value;
    }
}

public sealed class AchievementException : Exception
{
    public AchievementException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
