using System.Globalization;
using System.Text;

namespace DboAssetsStudio;

/// <summary>Creates isolated authoring drafts, never runtime scripts or a completed TMQ.</summary>
internal static class TqsTemplates
{
    public static WpsDocument Create(int id, string newPath)
    {
        if (id is < 1 or > 99999) throw new ArgumentOutOfRangeException(nameof(id), "El ID TQS debe ser 1..99999.");
        string output = ContentSafety.SafeOutput(newPath);
        string expected = id.ToString("D5", CultureInfo.InvariantCulture) + ".tqs";
        if (!Path.GetFileName(output).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El nombre debe ser " + expected + "; el servidor deriva el ID del nombre.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("El borrador debe ser un archivo nuevo.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        const string text = "-- DRAFT TQS: configure world, mobs, objectives, success/failure and rewards before playtesting.\n" +
            "-- UD/TMQ party maximum remains 5. This draft does not change party limits or complete a TMQ.\n" +
            "GameBegin()\n\tAction( \"change stage\" )\n\t\tParam( \"stage\", 1 )\n\tEnd()\nEnd()\n\n" +
            "GameStage( 1 )\n\tAction( \"wait\" )\n\t\tCondition( \"check time\" )\n\t\t\tParam( \"time\", 1 )\n\t\tEnd()\n\tEnd()\nEnd()\n\n" +
            "GameEnd()\nEnd()\n";
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(new UTF8Encoding(false, true).GetBytes(text));
            stream.Flush(true);
        }
        return WpsDocument.LoadTqs(output);
    }
}
