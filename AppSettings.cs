using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRuler;

public enum MeasureMode
{
    Horizontal,
    Vertical,
    Area
}

public sealed class AppSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 72;
    public MeasureMode Mode { get; set; } = MeasureMode.Horizontal;
    public double Opacity { get; set; } = 0.60;
    public bool PhysicalPixels { get; set; } = true;
    public bool LightContrast { get; set; }
    public bool Locked { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenRuler",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
            return loaded ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Opacity = Math.Round(Math.Clamp(Opacity, 0.30, 0.90), 2);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
