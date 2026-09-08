using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoidClip.Audio;
using VoidClip.Models;

namespace VoidClip.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoidClip");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string DefaultClipsFolder => Path.Combine(Root, "Clips");
    public static string LogFile => Path.Combine(Root, "voidclip.log");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DefaultClipsFolder);
    }

    public static void Log(string message)
    {
        try
        {
            EnsureRoot();
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

// ══════════════════════════════════════════════════════════════
public static class SettingsService
{
    public static AppSettings Load()
    {
        try
        {
            AppPaths.EnsureRoot();
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(AppPaths.SettingsFile), Json.Options);
                if (s != null) return Fix(s);
            }
        }
        catch (Exception ex) { AppPaths.Log("Settings load failed: " + ex.Message); }
        return Fix(new AppSettings());
    }

    private static AppSettings Fix(AppSettings s)
    {
        s.QuickLengths ??= new List<double> { 5, 10, 15, 30, 60 };
        s.Hotkeys ??= new Dictionary<string, string>();
        foreach (var kv in new AppSettings().Hotkeys)
            if (!s.Hotkeys.ContainsKey(kv.Key)) s.Hotkeys[kv.Key] = kv.Value;
        if (string.IsNullOrWhiteSpace(s.ClipsFolder)) s.ClipsFolder = AppPaths.DefaultClipsFolder;
        if (string.IsNullOrWhiteSpace(s.ExportFolder))
            s.ExportFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "VoidClip Exports");
        return s;
    }

    public static void Save(AppSettings s)
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(s, Json.Options));
        }
        catch (Exception ex) { AppPaths.Log("Settings save failed: " + ex.Message); }
    }
}

// ══════════════════════════════════════════════════════════════
public sealed class LibraryService
{
    private readonly Dictionary<string, float[]> _cache = new();
    private System.Threading.Timer _saveTimer;
    private readonly object _sync = new();

    public ObservableCollection<Clip> Clips { get; } = new();
    public ObservableCollection<ClipCategory> Categories { get; } = new();
    public string ClipsFolder { get; set; } = AppPaths.DefaultClipsFolder;

    public event Action Changed;

    // ──────────────────────────────────────────────────────────
    public void Load(string clipsFolder)
    {
        ClipsFolder = string.IsNullOrWhiteSpace(clipsFolder) ? AppPaths.DefaultClipsFolder : clipsFolder;
        Directory.CreateDirectory(ClipsFolder);

        Clips.Clear();
        Categories.Clear();
        try
        {
            if (File.Exists(AppPaths.LibraryFile))
            {
                var lib = JsonSerializer.Deserialize<LibraryFile>(
                    File.ReadAllText(AppPaths.LibraryFile), Json.Options);
                if (lib != null)
                {
                    foreach (var c in lib.Categories.OrderBy(c => c.Order)) Categories.Add(c);
                    foreach (var c in lib.Clips) Clips.Add(c);
                }
            }
        }
        catch (Exception ex) { AppPaths.Log("Library load failed: " + ex.Message); }

        if (Categories.Count == 0)
        {
            Categories.Add(new ClipCategory { Name = "Highlights", Color = "#8B3DFF", Order = 0 });
            Categories.Add(new ClipCategory { Name = "Cursed", Color = "#FF2E63", Order = 1 });
            Categories.Add(new ClipCategory { Name = "Bits", Color = "#3B82F6", Order = 2 });
        }
        RecountCategories();
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                AppPaths.EnsureRoot();
                var lib = new LibraryFile
                {
                    Clips = Clips.ToList(),
                    Categories = Categories.ToList()
                };
                var tmp = AppPaths.LibraryFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(lib, Json.Options));
                File.Copy(tmp, AppPaths.LibraryFile, true);
                File.Delete(tmp);
            }
            catch (Exception ex) { AppPaths.Log("Library save failed: " + ex.Message); }
        }
    }

    /// <summary>Coalesce rapid mutations into one write.</summary>
    public void SaveSoon()
    {
        _saveTimer?.Dispose();
        _saveTimer = new System.Threading.Timer(_ => Save(), null, 600, System.Threading.Timeout.Infinite);
    }

    // ──────────────────────────────────────────────────────────
    public string PathOf(Clip c) => Path.Combine(ClipsFolder, c.FileName);

    public Clip AddClip(float[] canonical, string name, string categoryId)
    {
        var clip = new Clip
        {
            Name = name,
            CategoryId = categoryId ?? "",
            Created = DateTime.Now,
            Duration = WavIO.Duration(canonical)
        };
        clip.FileName = clip.Id + ".wav";
        clip.Peaks = Dsp.Peaks(canonical, Fmt.Channels, 72);

        Directory.CreateDirectory(ClipsFolder);
        WavIO.Write(PathOf(clip), canonical);
        _cache[clip.Id] = canonical;

        Clips.Insert(0, clip);
        RecountCategories();
        SaveSoon();
        Changed?.Invoke();
        return clip;
    }

    public void ReplaceAudio(Clip clip, float[] canonical)
    {
        WavIO.Write(PathOf(clip), canonical);
        _cache[clip.Id] = canonical;
        clip.Duration = WavIO.Duration(canonical);
        clip.Peaks = Dsp.Peaks(canonical, Fmt.Channels, 72);
        clip.NotifyAll();
        SaveSoon();
        Changed?.Invoke();
    }

    public float[] GetSamples(Clip clip)
    {
        if (_cache.TryGetValue(clip.Id, out var cached)) return cached;
        try
        {
            var path = PathOf(clip);
            if (!File.Exists(path)) return Array.Empty<float>();
            var data = WavIO.Read(path);
            if (_cache.Count > 400) _cache.Clear();
            _cache[clip.Id] = data;
            return data;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Failed reading {clip.Name}: {ex.Message}");
            return Array.Empty<float>();
        }
    }

    public void Delete(Clip clip, bool deleteFile = true)
    {
        Clips.Remove(clip);
        _cache.Remove(clip.Id);
        if (deleteFile)
        {
            try { var p = PathOf(clip); if (File.Exists(p)) File.Delete(p); }
            catch (Exception ex) { AppPaths.Log("Delete failed: " + ex.Message); }
        }
        RecountCategories();
        SaveSoon();
        Changed?.Invoke();
    }

    public ClipCategory AddCategory(string name, string color)
    {
        var cat = new ClipCategory { Name = name, Color = color, Order = Categories.Count };
        Categories.Add(cat);
        SaveSoon();
        Changed?.Invoke();
        return cat;
    }

    public void DeleteCategory(ClipCategory cat, bool deleteClips)
    {
        var affected = Clips.Where(c => c.CategoryId == cat.Id).ToList();
        if (deleteClips) foreach (var c in affected) Delete(c);
        else foreach (var c in affected) c.CategoryId = "";
        Categories.Remove(cat);
        RecountCategories();
        SaveSoon();
        Changed?.Invoke();
    }

    public ClipCategory CategoryById(string id)
        => string.IsNullOrEmpty(id) ? null : Categories.FirstOrDefault(c => c.Id == id);

    public void RecountCategories()
    {
        foreach (var cat in Categories)
            cat.Count = Clips.Count(c => c.CategoryId == cat.Id);
    }

    public string NextClipName(string template)
    {
        var n = Clips.Count + 1;
        var name = (template ?? "Clip {n}")
            .Replace("{n}", n.ToString())
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{time}", DateTime.Now.ToString("HH-mm-ss"))
            .Replace("{datetime}", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
        if (string.IsNullOrWhiteSpace(name)) name = "Clip " + n;

        // keep names unique so the board stays readable
        var baseName = name;
        var suffix = 2;
        while (Clips.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";
        return name;
    }
}
