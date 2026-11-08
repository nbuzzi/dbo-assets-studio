namespace DboAssetsStudio;

internal static class WorkspaceCli
{
    public static async Task<object> RunAsync(string[] args)
    {
        var tokens = new List<string>(); string? client = null, server = null; bool replace = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--replace") { if (replace) throw new ArgumentException("--replace repetido."); replace = true; }
            else if (args[i] is "--client" or "--server")
            { string option = args[i]; if (++i >= args.Length) throw new ArgumentException("Falta valor de " + option); if (option == "--client") client = args[i]; else server = args[i]; }
            else if (args[i].StartsWith("--")) throw new ArgumentException("Opción desconocida: " + args[i]);
            else tokens.Add(args[i]);
        }
        if (tokens.Count == 0) throw new ArgumentException(Help);
        if (tokens[0] != "init" && (client != null || server != null)) throw new ArgumentException("--client/--server sólo en init.");
        if (replace && tokens[0] is not ("add" or "import-pak" or "import-mob")) throw new ArgumentException("--replace sólo al importar/adoptar.");
        return tokens[0] switch
        {
            "init" when tokens.Count == 3 => StudioWorkspace.Create(tokens[1], tokens[2], client, server).Status(),
            "status" when tokens.Count == 2 => StudioWorkspace.Open(tokens[1]).Status(),
            "add" when tokens.Count == 4 => StudioWorkspace.Open(tokens[1]).Adopt(tokens[2], tokens[3], replace),
            "import-pak" when tokens.Count == 3 => await StudioWorkspace.Open(tokens[1]).ImportPakAsync(tokens[2], replace),
            "import-mob" when tokens.Count == 3 => await StudioWorkspace.Open(tokens[1]).ImportMobAsync(tokens[2], replace),
            "commit" when tokens.Count == 3 => StudioWorkspace.Open(tokens[1]).Commit(tokens[2]),
            "restore" when tokens.Count == 3 => StudioWorkspace.Open(tokens[1]).Restore(tokens[2]),
            "export" when tokens.Count == 3 => StudioWorkspace.Open(tokens[1]).Export(tokens[2]),
            "verify" when tokens.Count == 2 => StudioWorkspace.VerifyZip(tokens[1]),
            _ => throw new ArgumentException(Help)
        };
    }
    public const string Help = "workspace init <new-project> <name> [--client client-root] [--server server-root] | status <project> | add <project> <file> <server/resource/...destination> [--replace] | import-pak <project> <candidate> [--replace] | import-mob <project> <candidate> [--replace] | commit <project> <message> | restore <project> <revision-id> | export <project> <new.zip> | verify <delivery.zip>";
}
