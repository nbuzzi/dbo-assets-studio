using System.Diagnostics;
using System.Text.Json;

namespace DboAssetsStudio;

/// <summary>
/// Runtime configuration. Secrets (such as the PAK index key) are never compiled
/// into the program: they are read from a local <c>studio.config.json</c> that is
/// excluded from version control (see <c>studio.config.example.json</c>).
///
/// Lookup order:
///   1. Path in the DBO_STUDIO_CONFIG environment variable
///   2. studio.config.json next to the executable
///   3. studio.config.json in the current directory
///   4. %LOCALAPPDATA%\DBO Assets Studio\studio.config.json
/// The environment variable DBO_PAK_KEY, when set, overrides the file.
/// </summary>
internal static class StudioConfig
{
    public const string FileName = "studio.config.json";
    public const string PakKeyVariable = "DBO_PAK_KEY";

    private sealed record Model(string? PakKey);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("DBO_STUDIO_CONFIG") is { Length: > 0 } explicitPath) yield return explicitPath;
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
        yield return Path.Combine(Environment.CurrentDirectory, FileName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBO Assets Studio", FileName);
    }

    /// <summary>The PAK index key, or null when it has not been configured.</summary>
    public static string? PakKey()
    {
        if (Environment.GetEnvironmentVariable(PakKeyVariable) is { Length: > 0 } fromEnvironment) return fromEnvironment;
        foreach (string path in Candidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(path), Options);
                if (!string.IsNullOrWhiteSpace(model?.PakKey)) return model!.PakKey!.Trim();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// Passes the key to the native pak-tool through its environment (never on the
    /// command line, where it would be visible to other processes).
    /// </summary>
    public static void ApplyPakKey(ProcessStartInfo start)
    {
        string key = PakKey() ?? throw new InvalidOperationException(
            $"Falta la clave de los PAK. Copiá {FileName.Replace(".json", ".example.json")} a {FileName}, " +
            $"completá \"pakKey\" (o definí la variable de entorno {PakKeyVariable}). Ver README.");
        start.Environment[PakKeyVariable] = key;
    }
}
