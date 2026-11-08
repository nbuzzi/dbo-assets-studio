using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DboAssetsStudio;

internal sealed partial class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(13, 19, 25);
    private static readonly Color Panel = Color.FromArgb(23, 32, 40);
    private static readonly Color Panel2 = Color.FromArgb(33, 46, 53);
    private static readonly Color Accent = Color.FromArgb(76, 215, 174);
    private static readonly Color Fg = Color.FromArgb(231, 238, 245);
    private readonly IReadOnlyList<RdfSchema> _schemas;
    private readonly WorkspaceTabs _tabs = new();
    private readonly DataGridView _tableGrid = new();
    private readonly TextBox _tableFilter = new();
    private readonly Label _tableInfo = new();
    private readonly Label _tableStatus = new();
    private readonly PictureBox _tableIcon = new();
    private readonly Label _tableIconName = new();
    private readonly CheckBox _showPadding = new();
    private readonly DataGridView _validationGrid = new();
    private readonly TextBox _assetRoot = new();
    private readonly TextBox _assetFilter = new();
    private readonly ListView _assetList = new();
    private readonly PictureBox _assetPreview = new();
    private readonly Label _assetDetails = new();
    private readonly Label _assetCount = new();
    private readonly Stack<(int row, byte[] before, byte[] after)> _undo = new();
    private readonly Stack<(int row, byte[] before, byte[] after)> _redo = new();
    private RdfDocument? _document;
    private readonly List<RdfRow> _visibleRdfRows = [];
    private RdfRow? SelectedRdfRow => _tableGrid.CurrentCell != null && _tableGrid.CurrentCell.RowIndex < _visibleRdfRows.Count ? _visibleRdfRows[_tableGrid.CurrentCell.RowIndex] : null;
    private string? _schemaCatalogPath;
    private bool _rebuildingGrid;
    private bool _rebuildingAssets;

    public MainForm(bool modern = true)
    {
        Text = "DBO Assets Studio · Content Studio 5.4";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1280, 760);
        Size = new Size(1500, 920);
        BackColor = Ink;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        Icon = SystemIcons.Application;
        _schemaCatalogPath = LocateCatalog();
        _schemas = RdfSchema.LoadCatalog(_schemaCatalogPath);
        BuildLayout();
        if (modern) InstallModernShell();
        Shown += (_, _) => ApplyInitialSplitters();
        UpdateDocumentState();
        FormClosing += (_, e) => { if (!ConfirmLeaveDocument() || !ConfirmLeaveWps() || !ConfirmLeaveQuest()) e.Cancel = true; else ClearMedia(); };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (modern) return; // The web shell routes shortcuts to the active editor.
            if (e.Control && e.KeyCode == Keys.S) { SaveCopy(); e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.O) { OpenRdf(); e.SuppressKeyPress = true; }
        };
    }

    private void ApplyInitialSplitters()
    {
        ApplySplitter(_tableGrid.Parent?.Parent as SplitContainer, 0.72);
        ApplySplitter(_assetList.Parent?.Parent as SplitContainer, 0.60);
    }

    private static void ApplySplitter(SplitContainer? split, double ratio)
    {
        if (split == null || split.ClientSize.Width <= split.SplitterWidth + 60) return;
        int maximum = split.ClientSize.Width - split.SplitterWidth - split.Panel2MinSize;
        if (maximum >= split.Panel1MinSize)
            split.SplitterDistance = Math.Clamp((int)(split.ClientSize.Width * ratio), split.Panel1MinSize, maximum);
    }

    private static string LocateCatalog()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "taiwan.txt"),
            Path.Combine(Environment.CurrentDirectory, "tools", "taiwan-table-studio", "taiwan.txt"),
            Path.Combine(Environment.CurrentDirectory, "config", "rdf-schemas", "taiwan.txt")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private void BuildLayout()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Ink, Padding = new Padding(0) };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(shell);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(22, 12, 22, 10) };
        var title = new Label { Text = "DBO  /  ASSETS STUDIO", ForeColor = Fg, Font = new Font("Segoe UI Semibold", 17f), AutoSize = true, Location = new Point(22, 12) };
        var subtitle = new Label { Text = "WORKSPACE  /  Tablas, tiendas y recursos del juego  /  Taiwan 2.0", ForeColor = Color.FromArgb(157, 179, 199), Font = new Font("Segoe UI", 9.5f), AutoSize = true, Location = new Point(24, 48) };
        var badge = new Label { Text = "STUDIO 03   •   GM WORKSPACE", Dock = DockStyle.Right, Width = 270, TextAlign = ContentAlignment.MiddleRight, ForeColor = Accent };
        header.Controls.Add(badge);
        header.Controls.Add(title); header.Controls.Add(subtitle); shell.Controls.Add(header, 0, 0);

        _tabs.Dock = DockStyle.Fill;
        BuildTableTab();
        BuildAssetsTab();
        BuildPakTab();
        BuildChangesTab();
        AddWorkspaceNavigation(shell);
        ThemeInputs(this);
        ThemeResourceList(_assetList);
        ThemeResourceList(_pakList);
    }

    private void BuildTableTab()
    {
        var page = new TabPage("Tablas RDF") { BackColor = Ink, Padding = new Padding(12) };
        _tabs.TabPages.Add(page);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Ink };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 26));
        page.Controls.Add(root);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(10), WrapContents = true, AutoScroll = true };
        toolbar.Controls.Add(MakeButton("Abrir RDF", OpenRdf, Accent, Ink));
        toolbar.Controls.Add(MakeButton("Validar", () => ValidateDocument(true), Panel2, Fg));
        toolbar.Controls.Add(MakeButton("Guardar revisión…", SaveCopy, Accent, Ink));
        toolbar.Controls.Add(MakeButton("Deshacer", UndoEdit, Panel2, Fg));
        toolbar.Controls.Add(MakeButton("Rehacer", RedoEdit, Panel2, Fg));
        toolbar.Controls.Add(MakeButton("Descartar cambios", RevertEdits, Panel2, Fg));
        _showPadding.Text = "Incluir padding"; _showPadding.ForeColor = Fg; _showPadding.AutoSize = true; _showPadding.Margin = new Padding(14, 12, 10, 0); _showPadding.CheckedChanged += (_, _) => RebuildGrid();
        toolbar.Controls.Add(_showPadding);
        _tableFilter.Width = 230; _tableFilter.Margin = new Padding(8, 8, 4, 0); _tableFilter.PlaceholderText = "Filtrar filas / IDs…"; _tableFilter.TextChanged += (_, _) => RebuildGrid();
        toolbar.Controls.Add(_tableFilter);
        AddFieldPreset(toolbar);
        root.Controls.Add(toolbar, 0, 0);

        var center = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Ink };
        root.Controls.Add(center, 0, 1);
        ConfigureGrid(_tableGrid);
        _tableGrid.Dock = DockStyle.Fill;
        _tableGrid.VirtualMode = true;
        _tableGrid.CellValueNeeded += (_, e) =>
        {
            if (_document != null && e.RowIndex < _visibleRdfRows.Count && _tableGrid.Columns[e.ColumnIndex].Tag is RdfField field)
                e.Value = _document.Schema.Decode(field, _visibleRdfRows[e.RowIndex].Current);
        };
        _tableGrid.CellValuePushed += OnCellValuePushed;
        _tableGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _visibleRdfRows.Count)
                e.CellStyle!.ForeColor = _visibleRdfRows[e.RowIndex].Modified ? Color.FromArgb(255, 207, 133) : Fg;
        };
        _tableGrid.CellBeginEdit += (_, e) => { if (_document == null || _tableGrid.Columns[e.ColumnIndex].Tag is not RdfField f || f.IsPadding) e.Cancel = true; };
        _tableGrid.SelectionChanged += (_, _) => UpdateRowPreview();
        _tableGrid.DataError += (_, e) => { e.ThrowException = false; e.Cancel = true; };
        center.Panel1.Controls.Add(_tableGrid);
        var previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(14) };
        var inspectHeading = new Label { Text = "VISTA DEL REGISTRO", ForeColor = Accent, Font = new Font("Segoe UI Semibold", 10), Dock = DockStyle.Top, Height = 28 };
        _tableIcon.SizeMode = PictureBoxSizeMode.Zoom; _tableIcon.BackColor = Color.FromArgb(11, 18, 28); _tableIcon.Dock = DockStyle.Top; _tableIcon.Height = 240;
        _tableIconName.Text = "Seleccioná una fila para ver su icono y referencias."; _tableIconName.ForeColor = Color.FromArgb(177, 193, 208); _tableIconName.Dock = DockStyle.Top; _tableIconName.Height = 150; _tableIconName.Padding = new Padding(0, 10, 0, 0);
        previewPanel.Controls.Add(_tableIconName); previewPanel.Controls.Add(_tableIcon); previewPanel.Controls.Add(inspectHeading);
        center.Panel2.Controls.Add(previewPanel);

        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Panel };
        lower.RowStyles.Add(new RowStyle(SizeType.Absolute, 31)); lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _tableInfo.Text = "Abrí una tabla RDF Taiwan para empezar."; _tableInfo.ForeColor = Color.FromArgb(165, 183, 201); _tableInfo.Dock = DockStyle.Fill; _tableInfo.Padding = new Padding(10, 5, 0, 0);
        ConfigureGrid(_validationGrid); _validationGrid.Dock = DockStyle.Fill; _validationGrid.AllowUserToAddRows = false; _validationGrid.ReadOnly = true;
        _validationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "level", HeaderText = "Nivel", Width = 95 });
        _validationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "check", HeaderText = "Validación", Width = 230 });
        _validationGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "message", HeaderText = "Detalle", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        lower.Controls.Add(_tableInfo, 0, 0); lower.Controls.Add(_validationGrid, 0, 1); root.Controls.Add(lower, 0, 2);
    }

    private void BuildAssetsTab()
    {
        var page = new TabPage("Recursos e iconos") { BackColor = Ink, Padding = new Padding(12) };
        _tabs.TabPages.Add(page);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Ink };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); page.Controls.Add(root);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(10), WrapContents = false, AutoScroll = true };
        bar.Controls.Add(MakeButton("Carpeta de recursos…", ChooseAssetRoot, Accent, Ink));
        bar.Controls.Add(MakeButton("Buscar archivos", SearchAssets, Panel2, Fg));
        bar.Controls.Add(MakeButton("Explorar un PAK…", async () => { _tabs.SelectedIndex = 2; await OpenPakAsync(); }, Panel2, Fg));
        _assetRoot.Width = 490; _assetRoot.Margin = new Padding(8, 9, 8, 0); _assetRoot.PlaceholderText = "Elegí una carpeta de texturas o recursos extraídos"; bar.Controls.Add(_assetRoot);
        _assetFilter.Width = 220; _assetFilter.Margin = new Padding(8, 9, 8, 0); _assetFilter.PlaceholderText = "Nombre, extensión o icono…"; bar.Controls.Add(_assetFilter);
        root.Controls.Add(bar, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Ink };
        root.Controls.Add(split, 0, 1);
        _assetList.Dock = DockStyle.Fill; _assetList.View = View.Details; _assetList.FullRowSelect = true; _assetList.HideSelection = false; _assetList.BackColor = Panel; _assetList.ForeColor = Fg;
        _assetList.Columns.Add("Recurso", 330); _assetList.Columns.Add("Tipo", 90); _assetList.Columns.Add("Tamaño", 110); _assetList.Columns.Add("Dimensiones", 120);
        _assetList.SelectedIndexChanged += (_, _) => ShowAssetPreview();
        split.Panel1.Controls.Add(_assetList);
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Panel, Padding = new Padding(12) };
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 73)); detail.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        _assetPreview.Dock = DockStyle.Fill; _assetPreview.SizeMode = PictureBoxSizeMode.Zoom; _assetPreview.BackColor = Color.FromArgb(9, 15, 23);
        _assetDetails.Dock = DockStyle.Fill; _assetDetails.ForeColor = Color.FromArgb(174, 192, 209); _assetDetails.Padding = new Padding(2, 10, 2, 0); _assetDetails.Text = "Elegí un recurso para ver su vista previa y metadatos.";
        detail.Controls.Add(_assetPreview, 0, 0); detail.Controls.Add(_assetDetails, 0, 1); split.Panel2.Controls.Add(detail);
        _assetCount.Text = "Vista previa de imágenes PNG, JPG, BMP y DDS (DXT1/3/5); otros recursos muestran metadatos."; _assetCount.Dock = DockStyle.Fill; _assetCount.ForeColor = Color.FromArgb(151, 172, 191); _assetCount.Padding = new Padding(8, 6, 0, 0); root.Controls.Add(_assetCount, 0, 2);
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.BackgroundColor = Panel; grid.BorderStyle = BorderStyle.None; grid.GridColor = Color.FromArgb(48, 68, 88);
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Panel2; grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg; grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Panel2; grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Fg;
        grid.DefaultCellStyle.BackColor = Panel; grid.DefaultCellStyle.ForeColor = Fg; grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(52, 84, 109); grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.RowHeadersVisible = false; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; grid.RowTemplate.Height = 28;
        grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
    }

    private static Button MakeButton(string caption, Action action, Color back, Color fore)
    {
        var button = new Button { Text = caption, AutoSize = true, MinimumSize = new Size(100, 36), Height = 38, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore, Font = new Font("Segoe UI Semibold", 9f), Margin = new Padding(4) };
        button.FlatAppearance.BorderColor = Color.FromArgb(66, 88, 108); button.FlatAppearance.MouseOverBackColor = Color.FromArgb(53, 74, 93); button.Click += (_, _) => action(); return button;
    }

    private void OpenRdf()
    {
        using var dialog = new OpenFileDialog { Filter = "RDF Taiwan (*.rdf)|*.rdf", Title = "Abrir tabla RDF Taiwan", InitialDirectory = DefaultTableFolder() };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadRdfPath(dialog.FileName);
    }

    private void LoadRdfPath(string path)
    {
        if (!ConfirmLeaveDocument()) return;
        try
        {
            var schema = RdfSchema.Detect(path, _schemas) ?? throw new InvalidDataException("Esta tabla todavía no tiene esquema Taiwan registrado.");
            var document = RdfDocument.Open(path, schema);
            _document = document; _savedHash = null;
            _workspaceRdfOrigin = null;
            _workspaceRdfHash = null;
            _undo.Clear(); _redo.Clear(); RebuildGrid(); UpdateDocumentState(); ValidateDocument(false);
        }
        catch (Exception ex) { ShowError("No se pudo abrir el RDF", ex.Message); }
    }

    private static string DefaultTableFolder()
    {
        string root = FindWorkspaceRoot();
        return root;
    }

    private static string FindWorkspaceRoot() => Environment.CurrentDirectory;

    private static string DefaultStagingFolder() =>
        Path.Combine(AppPaths.Root, "staging");

    private static bool IsWithin(string path, string root)
    {
        string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildGrid()
    {
        _rebuildingGrid = true;
        try
        {
            _tableGrid.RowCount = 0; _visibleRdfRows.Clear(); _tableGrid.Columns.Clear();
            if (_document == null) return;
            foreach (var field in _document.Schema.Fields)
            {
                var column = new DataGridViewTextBoxColumn { Name = $"field_{_tableGrid.Columns.Count}", HeaderText = field.Name, Tag = field, SortMode = DataGridViewColumnSortMode.NotSortable, Width = Math.Clamp(field.Name.Length * 9 + 28, 90, 210), ReadOnly = field.IsPadding };
                if (field.IsPadding && !_showPadding.Checked) column.Visible = false;
                _tableGrid.Columns.Add(column);
            }
            string filter = _tableFilter.Text.Trim();
            var key = _document.Schema.Fields.FirstOrDefault(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase));
            var searchFields = _document.Schema.Fields.Where(f => f.Name.Contains("name", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("icon", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("item", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var row in _document.Rows)
            {
                if (filter.Length > 0)
                {
                    bool found = key != null && _document.Schema.Decode(key, row.Current).Contains(filter, StringComparison.OrdinalIgnoreCase);
                    if (!found) found = searchFields.Any(f => _document.Schema.Decode(f, row.Current).Contains(filter, StringComparison.OrdinalIgnoreCase));
                    if (!found) continue;
                }
                _visibleRdfRows.Add(row);
            }
            _tableGrid.RowCount = _visibleRdfRows.Count;
            _tableGrid.AutoResizeColumnHeadersHeight();
        }
        finally { _rebuildingGrid = false; }
        ApplyFieldPreset();
        UpdateRowPreview();
    }

    private void OnCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_rebuildingGrid || _document == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (e.RowIndex >= _visibleRdfRows.Count || _tableGrid.Columns[e.ColumnIndex].Tag is not RdfField field) return;
        var row = _visibleRdfRows[e.RowIndex];
        var before = (byte[])row.Current.Clone();
        try
        {
            _document.Schema.Encode(field, Convert.ToString(e.Value), row.Current);
            var after = (byte[])row.Current.Clone();
            if (!before.AsSpan().SequenceEqual(after)) { _undo.Push((row.Index, before, after)); _redo.Clear(); }
            _tableGrid.InvalidateRow(e.RowIndex);

            UpdateDocumentState();
        }
        catch (Exception ex)
        {
            before.CopyTo(row.Current, 0);
            _tableGrid.InvalidateRow(e.RowIndex);
            ShowError("Valor no válido", ex.Message);
        }
    }

    private void UndoEdit()
    {
        if (_document == null || _undo.Count == 0) return;
        var edit = _undo.Pop(); var row = _document.Rows[edit.row]; _redo.Push((edit.row, edit.before, edit.after)); edit.before.CopyTo(row.Current, 0); RebuildGrid(); UpdateDocumentState();
    }
    private void RedoEdit()
    {
        if (_document == null || _redo.Count == 0) return;
        var edit = _redo.Pop(); var row = _document.Rows[edit.row]; _undo.Push((edit.row, edit.before, edit.after)); edit.after.CopyTo(row.Current, 0); RebuildGrid(); UpdateDocumentState();
    }
    private void RevertEdits()
    {
        if (_document == null || _document.ModifiedRows == 0) return;
        if (MessageBox.Show(this, "¿Descartar todas las modificaciones sin guardar?", "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var row in _document.Rows) row.Original.CopyTo(row.Current, 0);
        _undo.Clear(); _redo.Clear(); RebuildGrid(); UpdateDocumentState(); ValidateDocument(false);
    }

    private void UpdateDocumentState()
    {
        RefreshChanges();
        if (_document == null)
        {
            _tableInfo.Text = "Abrí una tabla RDF Taiwan. El archivo original siempre queda intacto: los cambios se guardan como copia validada.";
            _tableStatus.Text = "SIN TABLA"; return;
        }
        _tableInfo.Text = $"{Path.GetFileName(_document.Path)}   ·   {_document.Schema.Name}   ·   {_document.Rows.Count:N0} filas   ·   stride {_document.Schema.RowSize} B   ·   margen {_document.Margin}   ·   modificadas {_document.ModifiedRows:N0}   ·   SHA256 {_document.OriginalHash[..16]}…";
        _tableStatus.Text = _document.ModifiedRows == 0 ? "SIN CAMBIOS" : $"{_document.ModifiedRows} FILA(S) MODIFICADA(S) · SOLO COPIA";
    }

    private void ValidateDocument(bool showDialog)
    {
        _validationGrid.Rows.Clear();
        if (_document == null) return;
        var issues = new List<(string level, string check, string message)>();
        var schema = _document.Schema;
        if ((_document.OriginalBytes.Length - 1) % schema.RowSize != 0) issues.Add(("ERROR", "Estructura", "El RDF no coincide con el stride del esquema."));
        var keyField = schema.Fields.FirstOrDefault(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase));
        if (keyField != null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in _document.Rows)
            {
                var key = schema.Decode(keyField, row.Current);
                if (key != "0" && !seen.Add(key)) issues.Add(("ERROR", "Clave duplicada", $"tblidx {key}, fila {row.Index + 1}."));
            }
        }
        try { ValidateReferences(issues); } catch (Exception ex) { issues.Add(("ERROR", "Lectura de referencias", ex.Message)); }
        if (issues.Count == 0) issues.Add(("OK", "Integridad", "Stride RDF válido; identificadores únicos y referencias verificadas cuando hay tabla destino."));
        foreach (var issue in issues)
        {
            int i = _validationGrid.Rows.Add(issue.level, issue.check, issue.message);
            _validationGrid.Rows[i].DefaultCellStyle.ForeColor = issue.level == "ERROR" ? Color.FromArgb(255, 139, 126) : issue.level == "OK" ? Color.FromArgb(132, 213, 176) : Color.FromArgb(255, 207, 133);
        }
        if (showDialog)
        {
            int errors = issues.Count(i => i.level == "ERROR");
            MessageBox.Show(this, errors == 0 ? $"Validación completa: {issues.Count} observación(es), sin errores bloqueantes." : $"La validación encontró {errors} error(es). Corregilos antes de guardar.", "Validación RDF", MessageBoxButtons.OK, errors == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
    }

    private void ValidateReferences(List<(string level, string check, string message)> issues)
    {
        if (_document == null || !_document.Schema.Name.Equals("MerchantTable", StringComparison.OrdinalIgnoreCase) && !_document.Schema.Name.Equals("HLSItem", StringComparison.OrdinalIgnoreCase)) return;
        string dir = Path.GetDirectoryName(_document.Path)!;
        string[] itemNames = ["Table_Item_Data.rdf", "o_table_item_data.rdf"];
        string? itemPath = itemNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
        if (itemPath == null) { issues.Add(("WARN", "Referencias de ítems", "No encontré la tabla de ítems hermana; se omitió la validación cruzada.")); return; }
        var itemSchema = RdfSchema.Detect(itemPath, _schemas);
        if (itemSchema == null) { issues.Add(("WARN", "Referencias de ítems", "No hay esquema Taiwan registrado para la tabla hermana.")); return; }
        var itemDoc = RdfDocument.Open(itemPath, itemSchema);
        var keyField = itemSchema.Fields.First(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase));
        var ids = itemDoc.Rows.Select(r => itemSchema.Decode(keyField, r.Current)).ToHashSet(StringComparer.Ordinal);
        var candidates = _document.Schema.Fields.Where(f => f.Name.Equals("itemTblidx", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("aitem_Tblidx", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("aNeedItemTblidx", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var field in candidates)
            foreach (var row in _document.Rows)
                foreach (var id in _document.Schema.Decode(field, row.Current).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (id != "0" && id != "4294967295" && !ids.Contains(id)) issues.Add(("ERROR", "Referencia inválida", $"fila {row.Index + 1}, {field.Name}: no existe ítem {id}."));
    }

    private void SaveCopy()
    {
        if (_document == null) return;
        _tableGrid.EndEdit();
        try
        {
            ValidateDocument(false);
            if (_validationGrid.Rows.Cast<DataGridViewRow>().Any(r => Convert.ToString(r.Cells[0].Value) == "ERROR"))
                throw new InvalidDataException("Hay errores bloqueantes. Revisá el panel de validación.");
            bool warnings = _validationGrid.Rows.Cast<DataGridViewRow>().Any(r => Convert.ToString(r.Cells[0].Value) == "WARN");
            if (warnings && MessageBox.Show(this, "Hay referencias que no pudieron comprobarse. La revisión se guardará para revisión manual. ¿Continuar?", "Validación incompleta", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var notes = _validationGrid.Rows.Cast<DataGridViewRow>().Select(r => string.Join(" | ", r.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value)))).ToArray();
            string target = RdfSaveService.SaveRevision(_document, DefaultStagingFolder(), notes);
            _lastSavedRdf = target;
            _savedHash = Convert.ToHexString(SHA256.HashData(_document.BuildBytes()));
            RefreshChanges();
            MessageBox.Show(this, "Revisión guardada y releída desde disco. Incluye respaldo original y audit.json.\n\n" + target,
                "Guardado completado", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError("No se guardó la revisión", ex.Message); }
    }

    private void UpdateRowPreview()
    {
        _tableIcon.Image?.Dispose(); _tableIcon.Image = null;
        if (_document == null || SelectedRdfRow is not RdfRow row) { _tableIconName.Text = "Seleccioná una fila."; return; }
        var icon = _document.Schema.Fields.FirstOrDefault(f => f.Name.Equals("szIcon_Name", StringComparison.OrdinalIgnoreCase));
        var display = new List<string>();
        var key = _document.Schema.Fields.FirstOrDefault(f => f.Name.Equals("tblidx", StringComparison.OrdinalIgnoreCase));
        if (key != null) display.Add("ID " + _document.Schema.Decode(key, row.Current));
        if (icon != null)
        {
            var iconName = _document.Schema.Decode(icon, row.Current).Trim(); display.Add("Icono: " + (iconName.Length == 0 ? "(vacío)" : iconName));
            var path = FindIcon(iconName);
            if (path != null) { try { _tableIcon.Image = LoadPreview(path); display.Add("Archivo: " + path); } catch (Exception ex) { display.Add("Preview: " + ex.Message); } }
            else if (iconName.Length > 0) display.Add("No está suelto en la carpeta seleccionada; buscá el icono en la pestaña Recursos.");
        }
        _tableIconName.Text = string.Join(Environment.NewLine, display);
    }

    private string? FindIcon(string fileName)
    {
        string root = _assetRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(fileName)) return null;
        string normalized = Path.GetFileName(fileName);
        string[] direct = [Path.Combine(root, "texture", "icon", normalized), Path.Combine(root, "icon", normalized), Path.Combine(root, normalized)];
        var found = direct.FirstOrDefault(File.Exists); if (found != null) return found;
        foreach (var fallback in new[] { Path.ChangeExtension(normalized, ".png"), Path.ChangeExtension(normalized, ".dds") })
        {
            found = direct.Select(p => Path.ChangeExtension(p, Path.GetExtension(fallback))).FirstOrDefault(File.Exists);
            if (found != null) return found;
        }
        return null;
    }

    private void ChooseAssetRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "Elegí la carpeta con recursos sueltos o una extracción del PAK tex.pak.", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { _assetRoot.Text = dialog.SelectedPath; SearchAssets(); UpdateRowPreview(); }
    }

    private async void SearchAssets()
    {
        string root = _assetRoot.Text.Trim();
        if (!Directory.Exists(root)) { MessageBox.Show(this, "Seleccioná una carpeta existente de recursos o texturas.", "Recursos", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (_rebuildingAssets) return;
        _rebuildingAssets = true;
        _assetCount.Text = "Buscando recursos…";
        try
        {
            _assetList.Items.Clear();
            string filter = _assetFilter.Text.Trim();
            var extensions = new HashSet<string>([".dds", ".png", ".jpg", ".jpeg", ".bmp", ".srf", ".frm", ".dff", ".rws", ".rw4"], StringComparer.OrdinalIgnoreCase);
            var files = await Task.Run(() => EnumerateFilesSafe(root)
                .Where(file => extensions.Contains(Path.GetExtension(file)) && (filter.Length == 0 || file.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Take(3000).Select(file => (file, size: new FileInfo(file).Length)).ToArray());
            if (IsDisposed) return;
            _assetList.BeginUpdate();
            try
            {
                foreach (var file in files)
                {
                    var item = new ListViewItem(Path.GetRelativePath(root, file.file));
                    item.SubItems.Add(Path.GetExtension(file.file).TrimStart('.').ToUpperInvariant());
                    item.SubItems.Add(FormatSize(file.size)); item.SubItems.Add("Al seleccionar"); item.Tag = file.file;
                    _assetList.Items.Add(item);
                }
            }
            finally { _assetList.EndUpdate(); }
            int count = files.Length;
            _assetCount.Text = $"{count:N0} archivo(s) en la vista · máximo 3.000 resultados por búsqueda · imágenes DDS DXT1/3/5, PNG, JPG y BMP";
        }
        catch (Exception ex) { ShowError("Falló el índice de recursos", ex.Message); }
        finally { _rebuildingAssets = false; }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop(); string[] files = [], dirs = [];
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); } catch { }
            foreach (var file in files) yield return file;
            foreach (var sub in dirs) { try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub); } catch { } }
        }
    }

    private void ShowAssetPreview()
    {
        if (_rebuildingAssets || _assetList.SelectedItems.Count == 0) return;
        string path = Convert.ToString(_assetList.SelectedItems[0].Tag) ?? "";
        if (!File.Exists(path)) return;
        _assetPreview.Image?.Dispose(); _assetPreview.Image = null;
        var info = new FileInfo(path);
        var sb = new StringBuilder().AppendLine(Path.GetFileName(path)).AppendLine($"Formato: {info.Extension.ToUpperInvariant()} · {FormatSize(info.Length)}").AppendLine(path);
        try
        {
            if (IsImagePath(path)) { _assetPreview.Image = LoadPreview(path); sb.AppendLine($"Dimensiones: {_assetPreview.Image.Width} × {_assetPreview.Image.Height}"); }
            if (info.Length <= 128L * 1024 * 1024) sb.AppendLine("SHA256: " + HashFile(path));
            else sb.AppendLine("Hash omitido en vista previa para archivos mayores de 128 MiB.");
        }
        catch (Exception ex) { sb.AppendLine("Vista previa no disponible: " + ex.Message); }
        _assetDetails.Text = sb.ToString();
    }

    private static bool IsImagePath(string path) => new[] { ".dds", ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Image LoadPreview(string path)
    {
        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase)) return DdsPreview.Load(path);
        if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("Imagen demasiado grande para vista previa.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var image = Image.FromStream(stream);
        if ((long)image.Width * image.Height > 16_777_216) throw new InvalidDataException("Dimensiones de imagen demasiado grandes.");
        return new Bitmap(image);
    }
    private static string AssetDimensions(string path)
    {
        if (!IsImagePath(path)) return "—";
        try { using var image = LoadPreview(path); return $"{image.Width}×{image.Height}"; } catch { return "sin preview"; }
    }
    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / (1024d * 1024):0.0} MB";
    private void ShowError(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
