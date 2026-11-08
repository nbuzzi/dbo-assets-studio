namespace DboAssetsStudio;

internal static class QuestTemplates
{
    public static QuestDocument NewSkeleton(uint id, string newPath)
    {
        if (id == 0 || id > 65534) throw new ArgumentOutOfRangeException(nameof(id));
        if (!Path.GetFileName(newPath).Equals(id + ".t", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("El archivo nuevo debe llamarse <tid>.t.");
        // Native Start/GCond yeslnk -> End/GAct cid. This is authoring scaffolding,
        // not an automatically registered/accepted/rewarded quest.
        return QuestDocument.Create(newPath, "// Authoring skeleton: complete acceptance, objectives, text and rewards before deployment.\nCNtlTSTrigger\n{\n\tsm = 1;\n\tsq = 1;\n\tqc = 0;\n\trq = 0;\n\ttid = " + id + ";\n\ttitle = -1;\n\tCNtlTSGroup\n\t{\n\t\tgid = 0;\n\t\tCDboTSContStart\n\t\t{\n\t\t\tcid = 0;\n\t\t\tstdiag = -1;\n\t\t\tyeslnk = 254;\n\t\t\tnolnk = 255;\n\t\t\trm = 0;\n\t\t}\n\t\tCDboTSContEnd\n\t\t{\n\t\t\tcid = 254;\n\t\t\tprelnk = \"0;\";\n\t\t\ttype = 1;\n\t\t}\n\t}\n}\n");
    }
    public static object CloneAsNew(string source, uint id, string newDirectory)
    {
        if (id == 0 || id > 65534) throw new ArgumentOutOfRangeException(nameof(id));
        if (File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, id + ".t"))) throw new IOException("El ID nuevo ya existe en el directorio fuente.");
        var doc = QuestDocument.Load(source);
        var root = doc.Nodes.Single(x => x.Kind == "CNtlTSTrigger" && x.ParentId == null);
        doc.SetField(root.Id, "tid", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return doc.SaveCandidate(newDirectory, id + ".t", "Clon: solo se cambia tid y nombre. Referencias a otras quests/textos/objetivos/recompensas conservadas; revisarlas antes de desplegar.");
    }
}
