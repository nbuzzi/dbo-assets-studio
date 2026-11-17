using System.Security.Cryptography;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private readonly DataGridView _changes = new();
    private readonly Label _changeSummary = new() { Dock = DockStyle.Top, Height = 68, Padding = new Padding(12), ForeColor = Accent };
    private readonly ComboBox _fieldPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
    private string? _savedHash;

    private void AddFieldPreset(FlowLayoutPanel toolbar)
    {
        _fieldPreset.Items.AddRange([Loc.T("All fields", "Todos los campos"), Loc.T("Shop and prices", "Tienda y precios"), Loc.T("Combat and level", "Combate y nivel"), Loc.T("Models and icons", "Modelos e iconos")]);
        _fieldPreset.SelectedIndex = 0; _fieldPreset.Margin = new Padding(8, 9, 0, 0);
        _fieldPreset.SelectedIndexChanged += (_, _) => ApplyFieldPreset(); toolbar.Controls.Add(_fieldPreset);
    }

    private void ApplyFieldPreset()
    {
        foreach (DataGridViewColumn column in _tableGrid.Columns)
        {
            if (column.Tag is not RdfField field) continue;
            string name = field.Name.ToLowerInvariant();
            bool common = name is "tblidx" or "name" or "wsznametext" or "szicon_name";
            string[] terms = _fieldPreset.SelectedIndex switch
            {
                1 => ["cost", "cash", "price", "sale", "stack", "item", "zenny", "mileage", "category"],
                2 => ["attack", "defence", "offence", "level", "need", "rank", "durability", "attribute"],
                3 => ["model", "icon", "name", "resource"],
                _ => []
            };
            column.Visible = field.IsPadding ? _showPadding.Checked : common || terms.Length == 0 || terms.Any(name.Contains);
            if (name == "tblidx") column.Frozen = true;
        }
    }

    private void BuildChangesTab()
    {
        var page = new TabPage(Loc.T("Review changes", "Revisar cambios")) { BackColor = Ink, Padding = new Padding(16) };
        _tabs.TabPages.Add(page); ConfigureGrid(_changes);
        _changes.ReadOnly = true; _changes.Dock = DockStyle.Fill;
        _changes.Columns.Add("row", Loc.T("Row", "Fila")); _changes.Columns.Add("id", "ID");
        _changes.Columns.Add("field", Loc.T("Field", "Campo")); _changes.Columns.Add("before", "Original"); _changes.Columns.Add("after", Loc.T("New value", "Nuevo valor"));
        _changes.Columns[2].Width = 240; _changes.Columns[3].Width = 260;
        _changes.Columns[4].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _changeSummary.Text = Loc.T("No document open.\nEach revision includes the RDF, the original and an audit with the changed values.", "Sin documento abierto.\nCada revisión incluye RDF, original y auditoría con los valores modificados.");
        var save = MakeButton(Loc.T("Save verified revision…", "Guardar revisión verificada…"), SaveCopy, Accent, Ink); save.Dock = DockStyle.Bottom; save.Height = 48;
        var folder = MakeButton(Loc.T("Open revisions folder", "Abrir carpeta de revisiones"), () =>
        {
            string path = DefaultStagingFolder(); Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }, Panel2, Fg);
        folder.Dock = DockStyle.Bottom; folder.Height = 42;
        page.Controls.Add(_changes); page.Controls.Add(_changeSummary); page.Controls.Add(save); page.Controls.Add(folder);
    }

    private void RefreshChanges()
    {
        if (_changes.Columns.Count == 0) return;
        _changes.Rows.Clear();
        if (_document == null) return;
        var schema = _document.Schema;
        var key = schema.Fields.First(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase));
        foreach (var row in _document.Rows.Where(r => r.Modified))
            foreach (var field in schema.Fields)
                if (!row.Original.AsSpan(field.Offset, field.Size).SequenceEqual(row.Current.AsSpan(field.Offset, field.Size)))
                    _changes.Rows.Add(row.Index + 1, schema.Decode(key, row.Current), field.Name, schema.Decode(field, row.Original), schema.Decode(field, row.Current));
        bool saved = _savedHash != null && _savedHash == Convert.ToHexString(SHA256.HashData(_document.BuildBytes()));
        _changeSummary.Text = Loc.T($"{_document.ModifiedRows:N0} records modified  /  {_changes.Rows.Count:N0} fields  /  {(saved ? "Revision saved" : "Changes in memory")}\nSource: {_document.Path}", $"{_document.ModifiedRows:N0} registros modificados  /  {_changes.Rows.Count:N0} campos  /  {(saved ? "Revisión guardada" : "Cambios en memoria")}\nOrigen: {_document.Path}");
    }

    private bool ConfirmLeaveDocument()
    {
        _tableGrid.EndEdit();
        if (_document == null || _document.ModifiedRows == 0 || _savedHash == Convert.ToHexString(SHA256.HashData(_document.BuildBytes()))) return true;
        return MessageBox.Show(this, Loc.T("There are unsaved changes in a revision. Discard them and continue?", "Hay cambios sin guardar en una revisión. ¿Descartarlos y continuar?"), Loc.T("Pending changes", "Cambios pendientes"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }
}
