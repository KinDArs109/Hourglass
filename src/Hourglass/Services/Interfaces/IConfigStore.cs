using Hourglass.Models;

namespace Hourglass.Services.Interfaces;

public interface IConfigStore
{
    AppConfig Config { get; }

    void Load();

    /// <summary>Writes the current configuration to disk atomically.</summary>
    void Save();

    /// <summary>Coalesces frequent writes (ticking counters) into one save per few seconds.</summary>
    void SaveDeferred();
}
