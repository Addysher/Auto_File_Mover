using System.Text;
using System.Text.Json;

namespace AutoFileMover.Core;

/// <summary>Reads and writes settings.json. Safe to use from two processes at once.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public SettingsStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>Loads settings, or returns defaults if the file does not exist. Throws on a corrupt file.</summary>
    public MoverSettings Load()
    {
        if (!File.Exists(FilePath))
            return new MoverSettings();

        var json = Retry(() =>
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        });

        return JsonSerializer.Deserialize<MoverSettings>(json, JsonOptions) ?? new MoverSettings();
    }

    /// <summary>Writes atomically (temp file + rename) so a reader never sees half a file.</summary>
    public void Save(MoverSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, Serialize(settings), new UTF8Encoding(false));
        Retry(() =>
        {
            File.Move(temp, FilePath, overwrite: true);
            return true;
        });
    }

    public static string Serialize(MoverSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>
    /// Imports source/target folders from the old Python version's settings.ini.
    /// Returns true if at least one folder was found.
    /// </summary>
    public static bool TryImportLegacyIni(string iniPath, MoverSettings into)
    {
        if (!File.Exists(iniPath))
            return false;

        var found = false;
        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Replace('/', '\\');
            if (value.Length == 0)
                continue;

            if (key.Equals("source_folder", StringComparison.OrdinalIgnoreCase))
            {
                into.SourceFolder = value;
                found = true;
            }
            else if (key.Equals("target_folder", StringComparison.OrdinalIgnoreCase))
            {
                into.TargetFolder = value;
                found = true;
            }
        }

        return found;
    }

    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
    }
}
