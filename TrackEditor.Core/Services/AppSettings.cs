using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrackEditor.Core.Services;

public enum OnlineElevationProvider { OpenTopoData, OpenElevation, OpenMeteo }

public enum BaseMapProvider { OpenStreetMap, OpenTopoMap, CyclOSM, EsriWorldImagery, CartoLight }

/// <summary>Per-map parameters, configured in Settings. The <em>active</em> map itself is chosen on the
/// main-window toolbar, not here — these settings only tune a given map, they don't switch to it.</summary>
public class MapParams
{
    /// <summary>Tile cache size cap in MB for this map; 0 or negative means no limit (LRU eviction above it).</summary>
    public int TileCacheLimitMB { get; set; } = AppSettings.DefaultTileCacheLimitMB;
}

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

    public const int DefaultTileCacheLimitMB = 500;

    /// <summary>The active base map, chosen from the toolbar selector on the main window.</summary>
    public BaseMapProvider BaseMap { get; set; } = BaseMapProvider.OpenStreetMap;

    /// <summary>Default tile cache cap (MB) for maps not yet individually configured. Also migrates
    /// the old single global value from earlier settings files into <see cref="MapParameters"/>.</summary>
    public int TileCacheLimitMB { get; set; } = DefaultTileCacheLimitMB;

    /// <summary>Per-map parameters keyed by provider. Entries are created on demand by <see cref="ParamsFor"/>,
    /// seeded from <see cref="TileCacheLimitMB"/> so existing installs keep their previous cache cap.</summary>
    public Dictionary<BaseMapProvider, MapParams> MapParameters { get; set; } = new();

    /// <summary>Returns the (get-or-created) per-map parameters for a provider.</summary>
    public MapParams ParamsFor(BaseMapProvider provider)
    {
        if (!MapParameters.TryGetValue(provider, out var mp))
        {
            mp = new MapParams { TileCacheLimitMB = TileCacheLimitMB };
            MapParameters[provider] = mp;
        }
        return mp;
    }

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

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        // Deep-copy the per-map dictionary so the settings dialog edits a detached copy.
        c.MapParameters = new Dictionary<BaseMapProvider, MapParams>();
        foreach (var kv in MapParameters)
            c.MapParameters[kv.Key] = new MapParams { TileCacheLimitMB = kv.Value.TileCacheLimitMB };
        return c;
    }
}
