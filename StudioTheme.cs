namespace DboAssetsStudio;

// Pages cover the native tab strip; the workspace navigation is drawn by our
// own buttons. No native SplitContainer size constraints are needed at startup.
internal sealed class WorkspaceTabs : TabControl
{
    public override Rectangle DisplayRectangle => ClientRectangle;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x1328) { m.Result = new IntPtr(1); return; }
        base.WndProc(ref m);
    }
}

internal sealed partial class MainForm
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int enabled = 1;
        DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
    }

    private void AddWorkspaceNavigation(TableLayoutPanel shell)
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Ink, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.Controls.Add(body, 0, 1);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Panel, Padding = new Padding(12, 24, 10, 0) };
        navigation.Controls.Add(new Label { Text = "CONTENIDO", ForeColor = Color.FromArgb(126, 150, 158), Width = 155, Height = 36, Font = new Font(Font, FontStyle.Bold) });
        string[] labels = ["01   Tablas RDF", "02   Recursos locales", "03   PAK · 3D · Audio", "04   Revisar cambios"];
        var buttons = new List<Button>();
        for (int i = 0; i < labels.Length; ++i)
        {
            int index = i;
            var button = new Button { Text = labels[i], TextAlign = ContentAlignment.MiddleLeft, Width = 158, Height = 52, FlatStyle = FlatStyle.Flat,
                BackColor = Panel, ForeColor = Fg, Padding = new Padding(8, 0, 0, 0), Margin = new Padding(0, 0, 0, 8), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Panel2;
            button.Click += (_, _) => _tabs.SelectedIndex = index;
            navigation.Controls.Add(button); buttons.Add(button);
        }
        navigation.Controls.Add(new Label { Text = "TAIWAN 2.0\n\nRevisiones con respaldo\ny auditoría de cambios", ForeColor = Color.FromArgb(137, 161, 173), Width = 155, Height = 130, Margin = new Padding(4, 40, 0, 0) });
        void Highlight()
        {
            for (int i = 0; i < buttons.Count; i++) { buttons[i].BackColor = i == _tabs.SelectedIndex ? Panel2 : Panel; buttons[i].ForeColor = i == _tabs.SelectedIndex ? Accent : Fg; }
        }
        _tabs.SelectedIndexChanged += (_, _) => { Highlight(); ApplyInitialSplitters(); }; Highlight();
        body.Controls.Add(navigation, 0, 0); body.Controls.Add(_tabs, 1, 0);
    }

    private static void ThemeResourceList(ListView list)
    {
        list.OwnerDraw = true; list.MultiSelect = false;
        list.DrawColumnHeader += (_, e) =>
        {
            using var brush = new SolidBrush(Panel2); e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, list.Font, Rectangle.Inflate(e.Bounds, -8, 0), Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        list.DrawItem += (_, e) => { if (list.View != View.Details) e.DrawDefault = true; };
        list.DrawSubItem += (_, e) =>
        {
            bool selected = e.Item!.Selected;
            using var brush = new SolidBrush(selected ? Color.FromArgb(38, 78, 73) : e.ItemIndex % 2 == 0 ? Panel : Ink);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, list.Font, Rectangle.Inflate(e.Bounds, -8, 0), selected ? Accent : Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }

    private static void ThemeInputs(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TextBox box) { box.BackColor = Panel2; box.ForeColor = Fg; box.BorderStyle = BorderStyle.FixedSingle; }
            if (child is ComboBox combo)
            {
                combo.BackColor = Panel2; combo.ForeColor = Fg; combo.FlatStyle = FlatStyle.Flat;
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.DrawItem += (_, e) =>
                {
                    using var brush = new SolidBrush(Panel2); e.Graphics.FillRectangle(brush, e.Bounds);
                    string text = e.Index >= 0 ? combo.Items[e.Index]?.ToString() ?? "" : combo.Text;
                    TextRenderer.DrawText(e.Graphics, text, combo.Font, Rectangle.Inflate(e.Bounds, -4, 0), Fg, TextFormatFlags.VerticalCenter);
                };
            }
            ThemeInputs(child);
        }
    }
}
