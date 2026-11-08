using System.Media;
using System.Drawing.Drawing2D;

namespace DboAssetsStudio;

internal sealed class AudioPreview : System.Windows.Forms.UserControl
{
    private readonly SoundPlayer _player = new();
    private readonly Button _play = new() { Text = "▶  Reproducir", Width = 125, Height = 38, FlatStyle = FlatStyle.Flat };
    private readonly Button _stop = new() { Text = "■  Detener", Width = 115, Height = 38, FlatStyle = FlatStyle.Flat };
    private MemoryStream? _wave;
    private float[] _peaks = [];
    private string _format = "Seleccioná un sonido del PAK";
    public double DurationSeconds { get; private set; }
    public bool HasAudio => _wave != null;

    public AudioPreview()
    {
        DoubleBuffered = true; BackColor = Color.FromArgb(12, 19, 26); ForeColor = Color.FromArgb(220, 236, 235);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(14, 5, 0, 0), BackColor = BackColor };
        foreach (var b in new[] { _play, _stop }) { b.BackColor = Color.FromArgb(34, 64, 64); b.ForeColor = ForeColor; b.FlatAppearance.BorderSize = 0; actions.Controls.Add(b); }
        _play.Enabled = false;
        _play.Click += (_, _) => { if (_wave != null) { _wave.Position = 0; _player.Play(); } };
        _stop.Click += (_, _) => _player.Stop(); Controls.Add(actions);
    }

    public void LoadWave(byte[] bytes)
    {
        Clear();
        using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Salida de audio no es RIFF.");
        reader.ReadUInt32(); if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Salida de audio no es WAVE.");
        int channels = 0, sampleRate = 0, bits = 0, dataOffset = 0, dataSize = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string name = new(reader.ReadChars(4)); uint length = reader.ReadUInt32(); long end = stream.Position + length;
            if (end > stream.Length) throw new InvalidDataException("Audio truncado.");
            if (name == "fmt ")
            {
                if (length < 16 || reader.ReadUInt16() != 1) throw new InvalidDataException("La vista previa requiere PCM16.");
                channels = reader.ReadUInt16(); sampleRate = reader.ReadInt32(); reader.ReadInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
            }
            if (name == "data") { dataOffset = (int)stream.Position; dataSize = (int)length; }
            stream.Position = end + (length & 1);
        }
        if (channels < 1 || channels > 8 || sampleRate < 1000 || sampleRate > 384000 || bits != 16 || dataSize < 2) throw new InvalidDataException("Formato PCM fuera de límites.");
        DurationSeconds = (double)dataSize / (sampleRate * channels * 2);
        int sampleCount = dataSize / 2; _peaks = new float[180];
        for (int i = 0; i < _peaks.Length; i++)
        {
            int first = i * sampleCount / _peaks.Length, last = (i + 1) * sampleCount / _peaks.Length;
            for (int s = first; s < last; s += Math.Max(1, (last - first) / 100)) _peaks[i] = Math.Max(_peaks[i], Math.Abs(BitConverter.ToInt16(bytes, dataOffset + s * 2) / 32768f));
        }
        _wave = new MemoryStream(bytes, false); _player.Stream = _wave; _player.Load();
        _format = $"{TimeSpan.FromSeconds(DurationSeconds):mm\\:ss}  /  {sampleRate:N0} Hz  /  {channels} canal(es)  /  PCM16";
        _play.Enabled = true; Invalidate();
    }

    public void Clear()
    {
        _player.Stop(); _player.Stream = null; _wave?.Dispose(); _wave = null; _peaks = [];
        _play.Enabled = false; DurationSeconds = 0; _format = "Seleccioná un sonido del PAK"; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var title = new Font("Segoe UI Semibold", 18);
        e.Graphics.DrawString("AUDIO PREVIEW", title, Brushes.WhiteSmoke, 24, 24);
        TextRenderer.DrawText(e.Graphics, _format, Font, new Rectangle(24, 65, Math.Max(10, Width - 48), 45), Color.FromArgb(140, 173, 180));
        float middle = Height * .52f, space = Math.Max(1, (Width - 48) / 180f), available = Math.Max(1, Height * .23f);
        using var brush = new SolidBrush(Color.FromArgb(76, 215, 174));
        for (int i = 0; i < _peaks.Length; i++) { float height = Math.Max(2, _peaks[i] * available); e.Graphics.FillRectangle(brush, 24 + i * space, middle - height, Math.Max(1, space - 1), height * 2); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _player.Stop(); _player.Dispose(); _wave?.Dispose(); }
        base.Dispose(disposing);
    }
}
