using System.Text;
using System.Windows.Forms.Integration;
using System.Windows.Media.Media3D;
using W = System.Windows;
using WC = System.Windows.Controls;
using M = System.Windows.Media;
using I = System.Windows.Media.Imaging;

namespace DboAssetsStudio;

internal sealed record ModelMaterial(uint Color, string Texture);
internal sealed record ModelPart(Point3D[] Positions, W.Point[] UV, int[][] Triangles, ModelMaterial[] Materials);

internal sealed class StudioModel
{
    public List<ModelPart> Parts { get; } = [];
    public int VertexCount => Parts.Sum(p => p.Positions.Length);
    public int TriangleCount => Parts.Sum(p => p.Triangles.Length);
    public IEnumerable<string> TextureNames => Parts.SelectMany(p => p.Materials).Select(m => m.Texture).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    public static StudioModel Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "OSM1") throw new InvalidDataException("Formato de geometría no válido.");
        int Count(int maximum) { uint value = reader.ReadUInt32(); if (value > maximum) throw new InvalidDataException("Demasiados elementos en la geometría."); return (int)value; }
        double Float() { float value = reader.ReadSingle(); if (!float.IsFinite(value) || Math.Abs(value) > 1e8f) throw new InvalidDataException("Coordenadas inválidas."); return value; }
        var model = new StudioModel(); int count = Count(512), totalVertices = 0, totalTriangles = 0;
        for (int part = 0; part < count; part++)
        {
            int nv = Count(1_000_000), nt = Count(1_000_000), nm = Count(4096);
            if (nv == 0 || (long)nv * 20 + (long)nt * 16 + (long)nm * 132 > stream.Length - stream.Position)
                throw new InvalidDataException("Geometría vacía o truncada.");
            totalVertices += nv; totalTriangles += nt;
            if (totalVertices > 1_000_000 || totalTriangles > 1_000_000 || nm == 0) throw new InvalidDataException("Geometría fuera de límites.");
            var positions = new Point3D[nv]; var uv = new W.Point[nv]; var triangles = new int[nt][]; var materials = new ModelMaterial[nm];
            for (int v = 0; v < nv; v++) { positions[v] = new Point3D(Float(), Float(), Float()); uv[v] = new W.Point(Float(), Float()); }
            for (int t = 0; t < nt; t++)
            {
                int a = Count(nv - 1), b = Count(nv - 1), c = Count(nv - 1), material = Count(nm - 1);
                triangles[t] = [a, b, c, material];
            }
            for (int m = 0; m < nm; m++)
            {
                uint color = reader.ReadUInt32(); byte[] name = reader.ReadBytes(128);
                int end = Array.IndexOf(name, (byte)0);
                if (name.Length != 128 || end < 0) throw new InvalidDataException("Material truncado.");
                materials[m] = new ModelMaterial(color, Encoding.Latin1.GetString(name, 0, end));
            }
            model.Parts.Add(new ModelPart(positions, uv, triangles, materials));
        }
        if (stream.Position != stream.Length || model.TriangleCount == 0) throw new InvalidDataException("Modelo vacío o salida inesperada.");
        return model;
    }
}

internal sealed class ModelViewport : System.Windows.Forms.UserControl
{
    private readonly ElementHost _host = new() { Dock = DockStyle.Fill };
    private readonly WC.Viewport3D _view = new();
    private readonly WC.Grid _surface = new() { Background = new M.SolidColorBrush(M.Color.FromRgb(12, 19, 26)) };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 42 };
    private Point3D _target;
    private double _radius = 1, _yaw = .45, _pitch = .15, _distance = 4;
    private W.Point? _drag;
    public int MeshCount { get; private set; }

    public ModelViewport()
    {
        BackColor = System.Drawing.Color.FromArgb(12, 19, 26);
        Controls.Add(_host); _host.Child = _surface; _surface.Children.Add(_view); _view.Camera = _camera;
        var footer = new WC.TextBlock { Text = "ARRASTRAR: ROTAR    •    RUEDA: ZOOM    •    DOBLE CLIC: CENTRAR", Foreground = new M.SolidColorBrush(M.Color.FromRgb(125, 155, 166)),
            FontSize = 10, Margin = new W.Thickness(16), VerticalAlignment = W.VerticalAlignment.Bottom, IsHitTestVisible = false };
        _surface.Children.Add(footer);
        _surface.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { ResetView(); return; } _drag = e.GetPosition(_surface); _surface.CaptureMouse(); };
        _surface.MouseLeftButtonUp += (_, _) => { _drag = null; _surface.ReleaseMouseCapture(); };
        _surface.MouseMove += (_, e) =>
        {
            if (_drag is not W.Point previous) return;
            W.Point p = e.GetPosition(_surface); _yaw += (p.X - previous.X) * .012; _pitch = Math.Clamp(_pitch + (p.Y - previous.Y) * .012, -1.45, 1.45); _drag = p; UpdateCamera();
        };
        _surface.MouseWheel += (_, e) => { _distance = Math.Clamp(_distance * Math.Exp(-e.Delta / 1000d), _radius * .2, _radius * 30); UpdateCamera(); };
    }

    public void SetModel(StudioModel model, IReadOnlyDictionary<string, byte[]> textures)
    {
        _view.Children.Clear(); var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(M.Color.FromRgb(125, 125, 125)));
        group.Children.Add(new DirectionalLight(M.Colors.White, new Vector3D(-1, -2, -3)));
        var loaded = new Dictionary<string, I.BitmapImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in textures)
        {
            using var stream = new MemoryStream(texture.Value);
            var image = new I.BitmapImage(); image.BeginInit(); image.CacheOption = I.BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); loaded[texture.Key] = image;
        }
        var geometryGroup = new Model3DGroup();
        foreach (var part in model.Parts)
        {
            var positions = new Point3DCollection(part.Positions); positions.Freeze();
            var uv = new M.PointCollection(part.UV); uv.Freeze();
            foreach (var triangles in part.Triangles.GroupBy(t => t[3]))
            {
                int m = triangles.Key;
                var indices = new M.Int32Collection(triangles.SelectMany(t => t.Take(3)));
                if (indices.Count == 0) continue;
                var mesh = new MeshGeometry3D { Positions = positions, TextureCoordinates = uv, TriangleIndices = indices }; mesh.Freeze();
                var material = part.Materials[m]; M.Brush brush;
                if (loaded.TryGetValue(material.Texture, out var image)) brush = new M.ImageBrush(image) { TileMode = M.TileMode.Tile, ViewportUnits = M.BrushMappingMode.Absolute, Viewport = new W.Rect(0, 0, 1, 1) };
                else { uint c = material.Color; brush = new M.SolidColorBrush(M.Color.FromArgb((byte)(c >> 24), (byte)c, (byte)(c >> 8), (byte)(c >> 16))); }
                brush.Freeze(); var diffuse = new DiffuseMaterial(brush); diffuse.Freeze();
                geometryGroup.Children.Add(new GeometryModel3D(mesh, diffuse) { BackMaterial = diffuse });
            }
        }
        Rect3D bounds = geometryGroup.Bounds;
        if (bounds.IsEmpty) throw new InvalidDataException("Modelo sin geometría visible.");
        MeshCount = geometryGroup.Children.Count;
        _target = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        _radius = Math.Max(.01, Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2);
        group.Children.Add(geometryGroup); AddFloor(group, bounds.Y - _radius * .015);
        _view.Children.Add(new ModelVisual3D { Content = group }); ResetView();
    }

    private void AddFloor(Model3DGroup group, double y)
    {
        double spacing = _radius / 3, extent = spacing * 8, thin = spacing * .012;
        var positions = new Point3DCollection(); var indices = new M.Int32Collection();
        void Quad(double x1, double z1, double x2, double z2)
        {
            int n = positions.Count; positions.Add(new(x1 + _target.X, y, z1 + _target.Z)); positions.Add(new(x2 + _target.X, y, z1 + _target.Z)); positions.Add(new(x2 + _target.X, y, z2 + _target.Z)); positions.Add(new(x1 + _target.X, y, z2 + _target.Z));
            foreach (int i in new[] { 0, 2, 1, 0, 3, 2 }) indices.Add(n + i);
        }
        for (int i = -8; i <= 8; i++) { Quad(i * spacing - thin, -extent, i * spacing + thin, extent); Quad(-extent, i * spacing - thin, extent, i * spacing + thin); }
        var material = new EmissiveMaterial(new M.SolidColorBrush(M.Color.FromRgb(32, 54, 61)));
        group.Children.Add(new GeometryModel3D(new MeshGeometry3D { Positions = positions, TriangleIndices = indices }, material) { BackMaterial = material });
    }

    public void ResetView() { _yaw = .45; _pitch = .15; _distance = _radius * 3.4; UpdateCamera(); }
    private void UpdateCamera()
    {
        var delta = new Vector3D(Math.Sin(_yaw) * Math.Cos(_pitch), Math.Sin(_pitch), Math.Cos(_yaw) * Math.Cos(_pitch)); delta *= _distance;
        _camera.Position = _target + delta; _camera.LookDirection = -delta; _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.NearPlaneDistance = Math.Max(.0001, _radius * .01); _camera.FarPlaneDistance = _radius * 100;
    }
    internal void SaveRender(string path)
    {
        _surface.UpdateLayout();
        var image = new I.RenderTargetBitmap(Math.Max(1, (int)_surface.ActualWidth), Math.Max(1, (int)_surface.ActualHeight), 96, 96, M.PixelFormats.Pbgra32);
        image.Render(_surface); var png = new I.PngBitmapEncoder(); png.Frames.Add(I.BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }
    public void Clear() { _view.Children.Clear(); MeshCount = 0; }
}
