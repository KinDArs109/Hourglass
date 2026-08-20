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

    /// <summary>Writes a copy of the settings to <paramref name="path"/>, without sign-in tokens.</summary>
    bool Export(string path);

    /// <summary>Replaces the settings from <paramref name="path"/>, keeping local sign-ins.</summary>
    bool Import(string path);
}
