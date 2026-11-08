using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DboAssetsStudio;

internal static class ContentSafety
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    internal static int ReadBufferSize(string file) => (int)Math.Clamp(new FileInfo(file).Length, 4096L, 1024L * 1024);
    public static string Hash(string file)
    {
        // Keep large sequential reads for PAK units without allocating a MiB
        // buffer per tiny swap/metadata file in a many-resource sealed plan.
        int buffer = ReadBufferSize(file);
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.Read, buffer, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(s));
    }
    public static string ResourcePath(string path)
    {
        path = path.Replace('/', '\\'); if (path.StartsWith(".\\")) path = path[2..];
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Any(c => c < 32 || c > 255 || "<>:\"|?*".Contains(c))) throw new InvalidDataException("Ruta de recurso inválida: " + path);
        foreach (string part in path.Split('\\'))
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Ruta de recurso insegura: " + path);
        return path;
    }
    public static void NoLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
        {
            // One metadata query per ancestor. Do not cache ancestors: a link
            // introduced between selected files must still be detected.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(p); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("No se admiten enlaces/reparse points: " + p);
        }
    }
    public static string SafeOutput(string path)
    {
        string full = Path.GetFullPath(path); NoLinks(full);
        return full;
    }
    public static string FreshDirectory(string destination)
    {
        destination = SafeOutput(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("La salida debe ser una carpeta NUEVA: " + destination);
        string parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
        string pending = Path.Combine(parent, ".dbo-assets-pending-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending); return pending;
    }
    public static void WriteJson(string path, object value, bool canonicalLf = false)
    {
        // Sealed recipes must keep their byte hash after Git's LF checkout.
        // Indented serializers can use the host's newline (CRLF on Windows).
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json).Replace("\r\n", "\n"));
        using var s = new FileStream(SafeOutput(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        s.Write(bytes); s.Flush(true);
    }
}
