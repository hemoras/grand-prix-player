using System.Text.Json;

namespace GrandPrixPlayer;

internal enum GridLayoutMode { Grid, Main }

internal sealed class WinBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    public Rectangle ToRect() => new(X, Y, W, H);
    public static WinBounds From(Rectangle r) => new() { X = r.X, Y = r.Y, W = r.Width, H = r.Height };

    /// <summary>Vrai si le rectangle est raisonnablement visible sur un des écrans actuels.</summary>
    public bool IsVisibleOnScreen()
    {
        var r = ToRect();
        if (r.Width < 100 || r.Height < 60) return false;
        return Screen.AllScreens.Any(s =>
        {
            var i = Rectangle.Intersect(s.WorkingArea, r);
            return i.Width >= 80 && i.Height >= 60;
        });
    }
}

internal sealed class AppSettings
{
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public bool Detached { get; set; } = true;
    public GridLayoutMode Layout { get; set; } = GridLayoutMode.Grid;
    public double GridX { get; set; } = 0.5;
    public double GridY { get; set; } = 0.5;
    public double MainX { get; set; } = 0.7;
    public WinBounds? MainGlobal { get; set; }
    public WinBounds? MainDetached { get; set; }
    public List<WinBounds?> DetachedWindows { get; set; } = new();
    public List<string> AudioTitleOrder { get; set; } = new();
    public bool ShowAudioMapOnOpen { get; set; } = true;
    public bool RememberAudioOrder { get; set; } = true;
    /// <summary>Nom de piste vidéo → nom de sa piste audio principale.</summary>
    public Dictionary<string, string> PrincipalAudioByVideoTitle { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowPrincipalAudioOnOpen { get; set; } = true;
    public bool RememberPrincipalAudio { get; set; } = true;
    public string? LastFolder { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrandPrixPlayer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* réglages corrompus : on repart des valeurs par défaut */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* pas bloquant */ }
    }
}
