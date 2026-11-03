using System.Text.Json;

namespace GameModeTray;

/// <summary>
/// How to talk to a pair of headphones. Byte strings are hex ("AA EC 09 00 01 01 A1");
/// in the reply patterns "??" matches any byte. Empty optional fields are ignored.
/// </summary>
sealed class Profile
{
    public string Name { get; set; } = "";
    public string DeviceName { get; set; } = "";     // paired device name (prefix match)
    public string Transport { get; set; } = "rfcomm"; // only RFCOMM is supported for now
    public int Channel { get; set; } = 1;
    public string OnCommand { get; set; } = "";
    public string OffCommand { get; set; } = "";
    public string StateQuery { get; set; } = "";
    public string StateOnReply { get; set; } = "";
    public string StateOffReply { get; set; } = "";
    public int ConnectDelayMs { get; set; } = 3000;
    public int Attempts { get; set; } = 3;

    static readonly Lazy<IReadOnlyList<Profile>> LazyPresets = new(LoadPresets);

    /// <summary>The profiles of the profiles folder, embedded in the exe at build time.</summary>
    public static IReadOnlyList<Profile> Presets => LazyPresets.Value;

    static IReadOnlyList<Profile> LoadPresets()
    {
        var assembly = typeof(Profile).Assembly;
        var presets = new List<Profile>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("profiles.") && n.EndsWith(".json")))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            try
            {
                presets.Add(FromJson(reader.ReadToEnd()));
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                Log.Write($"{name}: {ex.Message}");
            }
        }
        return presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public override string ToString() => Name;

    public string ToJson() => JsonSerializer.Serialize(this, AppSettings.JsonOptions);

    public static Profile FromJson(string json) =>
        JsonSerializer.Deserialize<Profile>(json, AppSettings.JsonOptions) ?? throw new FormatException("empty profile");
}

/// <summary>App settings, stored in %APPDATA%\GameModeTray\settings.json.</summary>
sealed class AppSettings
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameModeTray", "settings.json");

    public bool AutoEnable { get; set; } = true;
    public Profile Profile { get; set; } = new();

    /// <summary>The saved settings, or null on the first run (no readable settings file).</summary>
    public static AppSettings? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write("settings.json: " + ex.Message);
        }
        return null;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
