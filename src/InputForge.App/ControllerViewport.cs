using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using InputForge.Core;
using Control = InputForge.Core.Control;

namespace InputForge.App;

internal sealed class ControllerViewport : Grid
{
    private sealed record Part(GeometryModel3D Model, Material Idle, Material Selected,
        Material Active, TranslateTransform3D Offset, Point3D Origin);

    private readonly Viewport3D viewport = new() { ClipToBounds = true };
    private readonly PerspectiveCamera camera = new() { FieldOfView = 37, NearPlaneDistance = .1, FarPlaneDistance = 60 };
    private readonly Model3DGroup models = new();
    private readonly Dictionary<GeometryModel3D, Control> hits = new();
    private readonly Dictionary<Control, List<Part>> parts = new();
    private readonly DiffuseMaterial shell = Matte(39, 39, 42);
    private readonly DiffuseMaterial grip = Matte(27, 27, 29);
    private readonly DiffuseMaterial metal = Matte(67, 67, 72);
    private double yaw, pitch, distance;
    private Point start, last;
    private bool dragging;
    private Control? selected;

    public event Action<Control>? ControlSelected;

    public ControllerViewport()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "3D controller camera. Use the arrow keys to orbit and plus or minus to zoom.");
        Children.Add(viewport);
        viewport.Camera = camera;
        models.Children.Add(new AmbientLight(Color.FromRgb(126, 126, 126)));
        models.Children.Add(new DirectionalLight(Color.FromRgb(175, 175, 175), new Vector3D(.35, -.65, -.8)));
        models.Children.Add(new DirectionalLight(Color.FromRgb(88, 88, 88), new Vector3D(-.8, .2, .65)));
        viewport.Children.Add(new ModelVisual3D { Content = models });
        SizeChanged += (_, _) => UpdateCamera();
        BuildModel();
        Reset();
    }

    private void BuildModel()
    {
        Add(new(0, .15, -.10), new(1.80, .93, .44), shell);
        Add(new(0, .31, .12), new(1.64, .73, .32), shell);
        Add(new(-1.29, -.41, -.07), new(.61, 1.04, .43), grip, angle: -22);
        Add(new(1.29, -.41, -.07), new(.61, 1.04, .43), grip, angle: 22);
        Add(new(0, -.16, -.44), new(.63, .46, .17), shell);
        Add(new(0, .61, .41), new(.14, .14, .035), metal, label: "×");
        Add(new(0, .17, .46), new(.07, .07, .023), grip);

        foreach (var (id, x, y, text) in new (Control, double, double, string)[]
        {
            (Control.Y,1.04,.57,"Y"), (Control.X,.81,.33,"X"),
            (Control.B,1.27,.33,"B"), (Control.A,1.04,.09,"A"),
            (Control.View,-.24,.40,"−"), (Control.Menu,.24,.40,"≡")
        })
            Add(new(x, y, .45), new(.107, .107, .08), grip, id, label: text);

        foreach (var (id, x, y) in new (Control, double, double)[]
        {
            (Control.LeftClick,-1.10,.38), (Control.RightClick,.52,-.29)
        })
        {
            Add(new(x,y,.42), new(.27,.27,.06), metal);
            Add(new(x,y,.51), new(.178,.178,.16), grip, id);
            Add(new(x,y,.67), new(.178,.178,.052), shell, id);
        }

        Add(new(-.55,-.29,.43), new(.28,.28,.065), metal, longitude: 8);
        Add(new(-.55,-.155,.49), new(.10,.10,.035), metal, Control.Up, longitude: 8);
        Add(new(-.55,-.425,.49), new(.10,.10,.035), metal, Control.Down, longitude: 8);
        Add(new(-.685,-.29,.49), new(.10,.10,.035), metal, Control.Left, longitude: 8);
        Add(new(-.415,-.29,.49), new(.10,.10,.035), metal, Control.Right, longitude: 8);

        Add(new(-1.12,.91,.07), new(.48,.16,.24), shell, Control.LeftBumper, -7);
        Add(new(1.12,.91,.07), new(.48,.16,.24), shell, Control.RightBumper, 7);
        Add(new(-1.15,.84,-.36), new(.33,.31,.27), shell, Control.LeftTrigger);
        Add(new(1.15,.84,-.36), new(.33,.31,.27), shell, Control.RightTrigger);
        Add(new(-.93,-.20,-.52), new(.31,.085,.06), metal, angle: 31);
        Add(new(-.77,.02,-.51), new(.24,.075,.06), metal, angle: 31);
        Add(new(.93,-.20,-.52), new(.31,.085,.06), metal, angle: -31);
        Add(new(.77,.02,-.51), new(.24,.075,.06), metal, angle: -31);
    }

    private static DiffuseMaterial Matte(byte r, byte g, byte b)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(r,g,b)));
        material.Freeze();
        return material;
    }

    private void Add(Point3D position, Vector3D size, Material material, Control? control = null,
        double angle = 0, int longitude = 28, string? label = null)
    {
        var offset = new TranslateTransform3D(position.X, position.Y, position.Z);
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(size.X, size.Y, size.Z));
        if (angle != 0) transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0,0,1), angle)));
        transform.Children.Add(offset);
        var model = new GeometryModel3D(Mesh(longitude), material) { Transform = transform };
        models.Children.Add(model);
        if (control is { } id)
        {
            hits.Add(model, id);
            if (!parts.TryGetValue(id, out var list)) parts.Add(id, list = []);
            list.Add(new Part(model, material, Matte(111, 32, 39), Matte(220, 38, 38), offset, position));
        }
        if (label is not null) Label(position, size, label, control);
    }

    private static readonly Dictionary<int, MeshGeometry3D> Meshes = new();

    private static MeshGeometry3D Mesh(int segments)
    {
        if (Meshes.TryGetValue(segments, out var existing)) return existing;
        const int rings = 14;
        var mesh = new MeshGeometry3D();
        for (int row = 0; row <= rings; row++)
        {
            double theta = Math.PI * row / rings;
            for (int column = 0; column <= segments; column++)
            {
                double phi = 2 * Math.PI * column / segments;
                var normal = new Vector3D(Math.Sin(theta) * Math.Cos(phi), Math.Cos(theta), Math.Sin(theta) * Math.Sin(phi));
                mesh.Positions.Add(new Point3D(normal.X,normal.Y,normal.Z));
                mesh.Normals.Add(normal);
            }
        }
        for (int row = 0; row < rings; row++)
        for (int column = 0; column < segments; column++)
        {
            int a = row * (segments + 1) + column;
            int b = a + segments + 1;
            foreach (int index in new[] { a, a+1, b, a+1, b+1, b }) mesh.TriangleIndices.Add(index);
        }
        mesh.Freeze();
        Meshes.Add(segments, mesh);
        return mesh;
    }

    private void Label(Point3D center, Vector3D size, string label, Control? control)
    {
        var drawing = new DrawingGroup();
        using (DrawingContext dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 40, 40));
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 26, Brushes.Gainsboro, 1);
            dc.DrawText(text, new Point((40-text.Width)/2, (40-text.Height)/2));
        }
        var material = new EmissiveMaterial(new DrawingBrush(drawing));
        material.Freeze();
        double d = size.X * .66, z = center.Z + size.Z + .002;
        var face = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(center.X-d,center.Y-d,z), new(center.X+d,center.Y-d,z),
                new(center.X+d,center.Y+d,z), new(center.X-d,center.Y+d,z)
            },
            TextureCoordinates = new PointCollection { new(0,1), new(1,1), new(1,0), new(0,0) },
            TriangleIndices = new Int32Collection { 0,1,2,0,2,3 }
        };
        face.Freeze();
        var model = new GeometryModel3D(face, material);
        models.Children.Add(model);
        if (control is { } id) hits.Add(model,id);
    }

    public void ShowState(ControllerState state, Control? selection)
    {
        selected = selection;
        foreach (var (id, group) in parts)
        {
            bool active = state.IsPressed(id);
            var stick = id == Control.LeftClick ? state.Left : id == Control.RightClick ? state.Right : default;
            float travel = id == Control.LeftTrigger ? state.LT : id == Control.RightTrigger ? state.RT : 0;
            foreach (Part part in group)
            {
                var material = active ? part.Active : selected == id ? part.Selected : part.Idle;
                if (part.Model.Material != material) part.Model.Material = material;
                double x = part.Origin.X + stick.X * .075;
                double y = part.Origin.Y + stick.Y * .075 - travel * .09;
                if (part.Offset.OffsetX != x) part.Offset.OffsetX = x;
                if (part.Offset.OffsetY != y) part.Offset.OffsetY = y;
            }
        }
    }

    public void Reset() { yaw = -.17; pitch = .21; distance = 7.4; UpdateCamera(); }
    public void Rear() { yaw = Math.PI; pitch = .14; UpdateCamera(); }

    private void UpdateCamera()
    {
        double aspect = ActualHeight > 0 ? ActualWidth / ActualHeight : 1.5;
        double d = distance * Math.Max(1, aspect / 1.65);
        camera.Position = new Point3D(d * Math.Sin(yaw) * Math.Cos(pitch),
            d * Math.Sin(pitch), d * Math.Cos(yaw) * Math.Cos(pitch));
        camera.LookDirection = -((Vector3D)camera.Position);
        camera.UpDirection = new Vector3D(0,1,0);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        start = last = e.GetPosition(this);
        dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        Point now = e.GetPosition(this);
        dragging |= (now - start).Length >= 5;
        if (dragging) Orbit((now.X-last.X)*.009, (now.Y-last.Y)*.009);
        last = now;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        Point position = e.GetPosition(viewport);
        if (!dragging && (e.GetPosition(this) - start).Length < 5 &&
            VisualTreeHelper.HitTest(viewport, position) is RayMeshGeometry3DHitTestResult result &&
            result.ModelHit is GeometryModel3D model && hits.TryGetValue(model, out Control control))
            ControlSelected?.Invoke(control);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Zoom(e.Delta / 120.0);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Orbit(-.12, 0); break;
            case Key.Right: Orbit(.12, 0); break;
            case Key.Up: Orbit(0, .12); break;
            case Key.Down: Orbit(0, -.12); break;
            case Key.Add: case Key.OemPlus: Zoom(1); break;
            case Key.Subtract: case Key.OemMinus: Zoom(-1); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Orbit(double x, double y)
    {
        yaw += x;
        pitch = Math.Clamp(pitch+y, -1.50, 1.50);
        UpdateCamera();
    }

    private void Zoom(double steps)
    {
        distance = Math.Clamp(distance * Math.Pow(.88, steps), 5.4, 13);
        UpdateCamera();
    }
}
