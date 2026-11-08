namespace DboAssetsStudio;

/// <summary>Per-user application data locations.</summary>
internal static class AppPaths
{
    /// <summary>Override with the DBO_STUDIO_DATA_ROOT environment variable.</summary>
    public static string Root => Environment.GetEnvironmentVariable("DBO_STUDIO_DATA_ROOT") is { Length: > 0 } configured
        ? ContentSafety.SafeOutput(configured)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBO Assets Studio");

    /// <summary>Hook for scrubbing text before it is logged or shown. Currently a pass-through.</summary>
    public static string Redact(string text) => text;
}
