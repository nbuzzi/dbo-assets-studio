namespace DboAssetsStudio;

/// <summary>
/// UI language for native WinForms text (message boxes, file dialogs). The web UI translates itself
/// (web/i18n.js) and tells the host its language through the "set-language" action. English is the
/// default; the CLI always answers in English.
/// </summary>
internal static class Loc
{
    private static string SettingPath => Path.Combine(AppPaths.Root, "language.txt");

    /// <summary>Saved by the web UI's language switch, so native controls built at startup use it too.</summary>
    public static string Language { get; private set; } = Read();

    private static string Read()
    {
        try { return File.Exists(SettingPath) && File.ReadAllText(SettingPath).Trim() == "es" ? "es" : "en"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "en"; }
    }

    public static void Set(string? language)
    {
        string value = language == "es" ? "es" : "en";
        if (value == Language) return;
        Language = value;
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingPath)!); File.WriteAllText(SettingPath, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Returns the Spanish text when the UI is in Spanish, the English text otherwise.</summary>
    public static string T(string english, string spanish) => Language == "es" ? spanish : english;
}
