using System.Text.Json;

namespace PinBot.Updater;

/// <summary>A swap that was started and not finished: the release being installed and the image to go back to.</summary>
public sealed record PendingSwap(long Version, string? PreviousImage);

/// <summary>What this server runs and what it refused, in /state/updater.json (also read by the server for its notices).</summary>
public sealed class UpdaterState
{
    public long Installed { get; set; }
    public string? Updater { get; set; }
    public PendingSwap? Pending { get; set; }
    public List<Refused> Refused { get; set; } = [];
    /// <summary>Failed attempts per release that were not the release's fault (Docker errors).</summary>
    public Dictionary<long, int> Failures { get; set; } = [];

    string _path = "";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Reads the state, falling back to the copy kept before the last save if the file is damaged.</summary>
    public static UpdaterState Load(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "updater.json");
        var s = Read(path) ?? Read(path + ".bak") ?? new();
        s.Refused ??= [];
        s.Failures ??= [];
        s._path = path;
        return s;
    }

    static UpdaterState? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UpdaterState>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Install(long version) { Installed = version; Pending = null; Failures.Remove(version); Save(); }
    public void Adopt(long version) { Installed = version; Save(); }
    public void BeginSwap(long version, string? previousImage) { Pending = new(version, previousImage); Save(); }
    public void ClearPending() { Pending = null; Save(); }
    public int CountFailure(long version) { Failures[version] = Failures.GetValueOrDefault(version) + 1; Save(); return Failures[version]; }
    public void Refuse(long version, string reason)
    {
        if (Refused.All(r => r.Version != version)) Refused.Add(new(version, reason, DateTime.UtcNow));
        Failures.Remove(version);
        Save();
    }
    public void SetUpdater(string image) { Updater = image; Save(); }

    void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true);
        File.Move(tmp, _path, overwrite: true);
    }
}
