using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrackEditor.Core.Services;

public enum OnlineElevationProvider { OpenTopoData, OpenElevation }

public enum BaseMapProvider { OpenStreetMap, OpenTopoMap, CyclOSM, EsriWorldImagery, CartoLight }

/// <summary>
/// User settings persisted as JSON under %AppData%\TrackEditor\settings.json.
/// Migrates the legacy srtm_folder.txt on first load.
/// </summary>
public class AppSettings
{
    public string? SrtmFolder { get; set; }
    public bool SrtmEnabled { get; set; } = true;
    public bool SrtmAutoDownload { get; set; } = true;

    public bool OnlineEnabled { get; set; }
    public OnlineElevationProvider OnlineProvider { get; set; } = OnlineElevationProvider.OpenTopoData;
    public string OpenTopoDataset { get; set; } = "srtm90m";

    public BaseMapProvider BaseMap { get; set; } = BaseMapProvider.OpenStreetMap;
    /// <summary>Per-map tile cache size cap in MB; 0 or negative means no limit (LRU eviction above it).</summary>
    public int TileCacheLimitMB { get; set; } = 500;

    /// <summary>Waypoint label background/marker colour (hex) on the map and profile plot.</summary>
    public string WaypointLabelBackHex { get; set; } = "#6A1B9A";
    /// <summary>Waypoint label text colour (hex) on the map and profile plot.</summary>
    public string WaypointLabelTextHex { get; set; } = "#FFFFFF";

    /// <summary>When on, drawing a new point routes along real paths instead of a straight line.</summary>
    public bool AutoRoute { get; set; }
    /// <summary>BRouter profile used for auto-routing (see RoutingService.Profiles).</summary>
    public string RoutingProfile { get; set; } = "trekking";

    /// <summary>Thin out routed legs, which come back very densely sampled from the router.</summary>
    public bool AutoRouteSimplify { get; set; } = true;
    /// <summary>Douglas-Peucker tolerance (metres) applied to a routed leg when simplification is on.</summary>
    public double AutoRouteToleranceM { get; set; } = 10;

    /// <summary>Which optional columns the points list shows (the index column is always visible).</summary>
    public bool ColWaypoint { get; set; } = true;
    public bool ColLat { get; set; } = true;
    public bool ColLon { get; set; } = true;
    public bool ColEle { get; set; } = true;
    public bool ColTime { get; set; } = true;
    public bool ColDist { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrackEditor");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s is not null) return s;
            }
            else
            {
                // migrate the old single-value settings file
                string legacy = Path.Combine(Dir, "srtm_folder.txt");
                if (File.Exists(legacy))
                    return new AppSettings { SrtmFolder = File.ReadAllText(legacy).Trim() };
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal: settings just won't persist */ }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
