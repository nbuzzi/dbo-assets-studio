using System.Security.Cryptography;
using System.Text;

namespace DboAssetsStudio;

internal sealed partial class MainForm
{
    private PakArchive? _pak;
    private readonly ListView _pakList = new() { View = View.Details, FullRowSelect = true, VirtualMode = true, HideSelection = false };
    private readonly TextBox _pakSearch = new() { PlaceholderText = "Buscar ruta, nombre o extensión…", Width = 320 };
    private readonly Label _pakSummary = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly PictureBox _pakImage = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly TextBox _pakDetails = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both };
    private List<PakEntry> _pakVisible = [];
    private int _previewGeneration;
    private readonly ModelViewport _modelView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly AudioPreview _audioView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ComboBox _pakCategory = new() { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _pakSourceFormat = new() { Width = 155, DropDownStyle = ComboBoxStyle.DropDownList };
    private ResourceCatalog? _resourceCatalog;
    private PakArchive? _textureArchive;
    private CancellationTokenSource? _previewCancellation;

    private void BuildPakTab()
    {
        var page = new TabPage("Explorador PAK") { BackColor = Ink, Padding = new Padding(16) };
        _tabs.TabPages.Add(page);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); page.Controls.Add(layout);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Panel, Padding = new Padding(6), AutoScroll = true, WrapContents = true };
        _pakSourceFormat.Items.AddRange(["Taiwan"]);
        _pakSourceFormat.SelectedIndex = 0; actions.Controls.Add(_pakSourceFormat);
        actions.Controls.Add(MakeButton("Abrir PAK…", async () => await OpenPakAsync(), Accent, Ink));
        actions.Controls.Add(MakeButton("Extraer selección…", async () => await ExportPakSelectionAsync(false), Panel2, Fg));
        actions.Controls.Add(MakeButton("Editar RDF seleccionado", async () => await ExportPakSelectionAsync(true), Panel2, Fg));
        actions.Controls.Add(MakeButton("Referencias RDF…", async () => await LoadResourceCatalogAsync(), Panel2, Fg));
        actions.Controls.Add(MakeButton("Referencias desde PAK…", async () => await LoadPakReferencesAsync(), Panel2, Fg));
        actions.Controls.Add(MakeButton("Guardar vista 3D…", SaveModelPreview, Panel2, Fg));
        _pakCategory.Items.AddRange(["Todos los recursos", "Modelos 3D", "Mobs", "Dogis / vestuario", "Objetos", "Sonidos", "Texturas", "Animaciones"]);
        _pakCategory.SelectedIndex = 0; _pakCategory.Margin = new Padding(6, 10, 0, 0);
        _pakCategory.SelectedIndexChanged += (_, _) => FilterPak(); actions.Controls.Add(_pakCategory);
        _pakSearch.Margin = new Padding(18, 10, 0, 0); actions.Controls.Add(_pakSearch);
        _pakSearch.TextChanged += (_, _) => FilterPak(); layout.Controls.Add(actions, 0, 0);
        _pakSummary.Text = "Abrí tex.pak, gui.pak, scr.pak u otro índice Taiwan. Conservá sus unidades en la misma carpeta.";
        _pakSummary.ForeColor = Color.FromArgb(169, 189, 202); _pakSummary.Padding = new Padding(0, 12, 0, 0); layout.Controls.Add(_pakSummary, 0, 1);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        layout.Controls.Add(body, 0, 2);
        _pakList.Dock = DockStyle.Fill; _pakList.BackColor = Panel; _pakList.ForeColor = Fg; _pakList.BorderStyle = BorderStyle.None;
        _pakList.Columns.Add("RECURSO", 420); _pakList.Columns.Add("TIPO", 75); _pakList.Columns.Add("BYTES", 100); _pakList.Columns.Add("UNIDAD", 75); _pakList.Columns.Add("ESTADO", 135);
        _pakList.SizeChanged += (_, _) =>
        {
            int width = Math.Max(400, _pakList.ClientSize.Width - 22);
            double[] ratios = [.51, .10, .14, .09, .16];
            for (int i = 0; i < ratios.Length; i++) _pakList.Columns[i].Width = (int)(width * ratios[i]);
        };
        _pakList.RetrieveVirtualItem += (_, e) => { var item = _pakVisible[e.ItemIndex]; e.Item = new ListViewItem([item.Name, Path.GetExtension(item.Name), item.Size.ToString("N0"), item.Unit.ToString(), item.Status]); };
        _pakList.SelectedIndexChanged += async (_, _) => await PreviewPakAsync(); body.Controls.Add(_pakList, 0, 0);
        var preview = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(16, 0, 0, 0) };
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 66)); preview.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        _pakImage.BackColor = Color.FromArgb(10, 17, 23); _pakDetails.BackColor = Panel; _pakDetails.ForeColor = Fg;
        _pakImage.Paint += (_, e) =>
        {
            if (_pakImage.Image != null) return;
            using var title = new Font("Segoe UI Semibold", 19);
            TextRenderer.DrawText(e.Graphics, "RESOURCE INSPECTOR", title,
                new Rectangle(18, 60, Math.Max(1, _pakImage.Width - 36), 44), Accent, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(e.Graphics, "Modelos 3D · Texturas · Sonidos\n\nSeleccioná un recurso para explorar su contenido.", Font,
                new Rectangle(24, 118, Math.Max(1, _pakImage.Width - 48), 100), Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        };
        _pakDetails.BorderStyle = BorderStyle.None; _pakDetails.Font = new Font("Consolas", 10);
        var media = new Panel { Dock = DockStyle.Fill, BackColor = Ink };
        media.Controls.Add(_pakImage); media.Controls.Add(_modelView); media.Controls.Add(_audioView);
        preview.Controls.Add(media, 0, 0); preview.Controls.Add(_pakDetails, 0, 1); body.Controls.Add(preview, 1, 0);
    }

    private async Task OpenPakAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Índice PAK (*.pak)|*.pak", Title = "Abrir índice del formato seleccionado" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string format = "taiwan";
        await OpenPakPathAsync(dialog.FileName, format: format);
    }

    internal async Task OpenPakPathAsync(string path, string? resource = null, string? references = null, string format = "taiwan", string? family = null)
    {
        _tabs.SelectedIndex = 2;
        UseWaitCursor = true; _pakSummary.Text = "Leyendo índice y comprobando unidades…";
        try
        {
            _pak = await Task.Run(() => PakArchive.OpenAsync(path, format, family));
            _textureArchive = null;
            if (references != null)
                _resourceCatalog = Directory.Exists(references)
                    ? await Task.Run(() => ResourceCatalog.Load(references, _schemas))
                    : await ResourceCatalog.LoadPakAsync(await PakArchive.OpenAsync(references), _schemas);
            _pakCategory.SelectedIndex = 0; _pakSearch.Text = ""; FilterPak(); UpdateRowPreview();
            if (resource != null)
            {
                int selected = _pakVisible.FindIndex(e => e.Name.Equals(resource, StringComparison.OrdinalIgnoreCase));
                if (selected < 0) throw new FileNotFoundException("Recurso no encontrado en el índice: " + resource);
                _pakList.SelectedIndices.Add(selected); _pakList.EnsureVisible(selected);
            }
        }
        catch (Exception ex) { _pakSummary.Text = "No se pudo abrir el PAK."; ShowError("PAK no disponible", ex.Message); }
        finally { UseWaitCursor = false; }
    }

    private void FilterPak()
    {
        ++_previewGeneration;
        ClearMedia();
        _pakList.SelectedIndices.Clear(); _pakList.VirtualListSize = 0;
        _pakVisible = _pak?.Entries.Where(e => MatchesCategory(e.Name) && e.Name.Contains(_pakSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        _pakList.VirtualListSize = _pakVisible.Count; _pakList.Invalidate();
        if (_pak != null) _pakSummary.Text = $"{Path.GetFileName(_pak.IndexPath)}  /  {_pakVisible.Count:N0} de {_pak.Entries.Count:N0} recursos  /  {_pak.Entries.Count(e => e.Status != "Disponible"):N0} con problemas  /  Solo lectura";
    }

    private PakEntry? SelectedPakEntry => _pakList.SelectedIndices.Count == 0 ? null : _pakVisible[_pakList.SelectedIndices[0]];

    private void SaveModelPreview()
    {
        if (_modelView.MeshCount == 0) { ShowError("Vista 3D", "Seleccioná primero un modelo DFF que pueda previsualizarse."); return; }
        using var dialog = new SaveFileDialog { Filter = "Imagen PNG (*.png)|*.png", FileName = Path.GetFileNameWithoutExtension(SelectedPakEntry?.Name ?? "modelo") + ".png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _modelView.SaveRender(dialog.FileName); }
        catch (Exception ex) { ShowError("Guardar vista 3D", ex.Message); }
    }

    private bool MatchesCategory(string name)
    {
        string n = name.ToLowerInvariant(), ext = Path.GetExtension(n);
        return _pakCategory.SelectedIndex switch
        {
            1 => ext == ".dff", 2 => ext == ".dff" && n.Contains("mesh_mob"),
            3 => ext == ".dff" && (n.Contains("mesh_costume") || n.Contains("dogi")),
            4 => ext == ".dff" && n.Contains("object"),
            5 => ext is ".wav" or ".ogg" or ".mp3", 6 => ext is ".dds" or ".png" or ".bmp" or ".jpg",
            7 => ext is ".anm" or ".uva", _ => true
        };
    }

    private void ClearMedia()
    {
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = null;
        _pakImage.Image?.Dispose(); _pakImage.Image = null; _pakImage.Visible = true;
        _modelView.Clear(); _modelView.Visible = false; _audioView.Clear(); _audioView.Visible = false;
        _pakDetails.Clear();
    }

    private async Task LoadResourceCatalogAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Carpeta RDF Taiwan con tablas Item, Mob, NPC y Object" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _pakSummary.Text = "Relacionando modelos e iconos con IDs RDF…";
            _resourceCatalog = await Task.Run(() => ResourceCatalog.Load(dialog.SelectedPath, _schemas));
            _pakSummary.Text = $"{_resourceCatalog.Count:N0} referencias RDF cargadas / {_resourceCatalog.Notes.Count} tablas omitidas";
            await PreviewPakAsync();
            if (_resourceCatalog.Notes.Count > 0) _pakDetails.AppendText("\r\n\r\nTABLAS OMITIDAS\r\n" + string.Join("\r\n", _resourceCatalog.Notes));
        }
        catch (Exception ex) { ShowError("Referencias RDF", ex.Message); }
    }

    private async Task LoadPakReferencesAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "PAK de tablas Taiwan (*.pak)|*.pak", Title = "Seleccionar tbl.pak con sus unidades" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _pakSummary.Text = "Leyendo referencias RDF desde el PAK de tablas…";
            var archive = await PakArchive.OpenAsync(dialog.FileName);
            _resourceCatalog = await ResourceCatalog.LoadPakAsync(archive, _schemas);
            _pakSummary.Text = $"{_resourceCatalog.Count:N0} referencias cargadas de {Path.GetFileName(archive.IndexPath)} / {_resourceCatalog.Notes.Count} tablas omitidas";
            await PreviewPakAsync();
            if (_resourceCatalog.Notes.Count > 0) _pakDetails.AppendText("\r\n\r\nTABLAS OMITIDAS\r\n" + string.Join("\r\n", _resourceCatalog.Notes));
        }
        catch (Exception ex) { ShowError("Referencias PAK", ex.Message); }
    }

    private static Bitmap DecodeImage(byte[] bytes, string extension)
    {
        if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase)) return TgaPreview.Load(bytes);
        if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dds");
            try { File.WriteAllBytes(temp, bytes); return DdsPreview.Load(temp); } finally { File.Delete(temp); }
        }
        using var stream = new MemoryStream(bytes); using var img = Image.FromStream(stream); return new Bitmap(img);
    }

    private async Task<Dictionary<string, byte[]>> ReadModelTexturesAsync(StudioModel model, PakArchive source, CancellationToken token)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(Path.GetDirectoryName(source.IndexPath)!, "tex.pak");
        if (!File.Exists(path)) return result;
        if (_textureArchive?.IndexPath != path || _textureArchive.Format != source.Format)
            _textureArchive = await PakArchive.OpenAsync(path, source.Format);
        var textures = _textureArchive; long budget = 64L * 1024 * 1024, pixelBudget = 16_777_216;
        foreach (string name in model.TextureNames.Take(64))
        {
            token.ThrowIfCancellationRequested();
            var entry = textures.Entries.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e.Name).Equals(name, StringComparison.OrdinalIgnoreCase) && e.Status == "Disponible" && Path.GetExtension(e.Name).ToLowerInvariant() is ".dds" or ".png" or ".bmp");
            if (entry == null || entry.Size > 16 * 1024 * 1024 || entry.Size > budget) continue;
            budget -= entry.Size;
            try
            {
                byte[] bytes = await textures.ReadAsync(entry);
                using var bitmap = await Task.Run(() => DecodeImage(bytes, Path.GetExtension(entry.Name)), token);
                long pixels = (long)bitmap.Width * bitmap.Height;
                if (pixels > pixelBudget) continue;
                pixelBudget -= pixels;
                using var output = new MemoryStream(); bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png); result[name] = output.ToArray();
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { /* Missing/unsupported textures retain material color. */ }
        }
        return result;
    }

    private async Task PreviewPakAsync()
    {
        int generation = ++_previewGeneration; ClearMedia();
        var entry = SelectedPakEntry; var archive = _pak;
        if (entry == null || archive == null) return;
        _previewCancellation = new CancellationTokenSource(); var token = _previewCancellation.Token;
        string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
        _pakDetails.Text = $"{entry.Name}\r\n\r\nUnidad: {Path.GetFileName(entry.UnitPath)} / Offset: {entry.Offset:N0}\r\nBytes: {entry.Size:N0} / Estado: {entry.Status}";
        string references = _resourceCatalog?.Describe(entry.Name) ?? "Cargá Referencias RDF para consultar IDs y nombres asociados.";
        if (entry.Size > 128 * 1024 * 1024 || entry.Status != "Disponible") return;
        try
        {
            byte[] bytes = await archive.ReadAsync(entry);
            if (generation != _previewGeneration || IsDisposed) return;
            if (extension == ".dff")
            {
                _pakDetails.AppendText("\r\n\r\nCargando geometría y texturas…");
                var mesh = await NativeMedia.ConvertAsync("model", bytes, extension, token);
                var model = await Task.Run(() => StudioModel.Parse(mesh), token);
                var textures = await ReadModelTexturesAsync(model, archive, token);
                if (generation != _previewGeneration || IsDisposed) return;
                _modelView.SetModel(model, textures); _modelView.Visible = true; _modelView.BringToFront(); _pakImage.Visible = false;
                _pakDetails.AppendText($"\r\nPOSE ESTÁTICA / {model.VertexCount:N0} vértices / {model.TriangleCount:N0} triángulos\r\nTexturas: {textures.Count}/{model.TextureNames.Count()} / Sin animación ni composición de personaje.");
                var missing = model.TextureNames.Where(n => !textures.ContainsKey(n)).Take(20).ToArray();
                if (missing.Length > 0) _pakDetails.AppendText("\r\nTexturas pendientes: " + string.Join(", ", missing));
            }
            else if (extension is ".wav" or ".ogg" or ".mp3")
            {
                _pakDetails.AppendText("\r\n\r\nPreparando audio…");
                var wave = await NativeMedia.ConvertAsync("audio", bytes, extension, token);
                if (generation != _previewGeneration || IsDisposed) return;
                _audioView.LoadWave(wave); _audioView.Visible = true; _audioView.BringToFront(); _pakImage.Visible = false;
                _pakDetails.AppendText($"\r\nAudio: {_audioView.DurationSeconds:N1} segundos. Pulsá Reproducir para escucharlo.");
            }
            else if (extension is ".dds" or ".png" or ".jpg" or ".jpeg" or ".bmp")
            {
                var bitmap = await Task.Run(() => DecodeImage(bytes, extension), token);
                if (generation != _previewGeneration || IsDisposed) { bitmap.Dispose(); return; }
                _pakImage.Image = bitmap;
            }
            else if (extension is ".txt" or ".xml" or ".srf" or ".frm" or ".ini") _pakDetails.AppendText("\r\n\r\n" + Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 16000))));
            else _pakDetails.AppendText("\r\n\r\nSin visor para este formato. Primeros bytes:\r\n" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(64, bytes.Length))));
            _pakDetails.AppendText("\r\n\r\n" + references);
            _pakDetails.AppendText("\r\n\r\nSHA256: " + Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _previewGeneration && !IsDisposed) _pakDetails.AppendText("\r\n\r\nVista previa: " + ex.Message); }
    }

    private async Task ExportPakSelectionAsync(bool editRdf)
    {
        var entry = SelectedPakEntry; var archive = _pak;
        if (entry == null || archive == null) { ShowError("Seleccionar recurso", "Seleccioná primero un archivo del PAK."); return; }
        string leaf = Path.GetFileName(entry.Name);
        if (editRdf && RdfSchema.Detect(leaf, _schemas) == null) { ShowError("Esquema no soportado", "Este recurso no tiene un esquema RDF Taiwan registrado."); return; }
        try
        {
            byte[] bytes = await archive.ReadAsync(entry);
            string folder = Path.Combine(DefaultStagingFolder(), "pak-extract", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            // Use only a filename, never an archive-controlled directory hierarchy.
            if (leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || leaf.Length == 0) throw new InvalidDataException("Nombre de recurso no exportable.");
            string target = Path.Combine(folder, leaf);
            await File.WriteAllBytesAsync(target, bytes);
            if (editRdf) { LoadRdfPath(target); _tabs.SelectedIndex = 0; }
            else MessageBox.Show(this, "Recurso extraído:\n" + target, "Extracción completada");
        }
        catch (Exception ex) { ShowError("No se pudo extraer", ex.Message); }
    }
}
