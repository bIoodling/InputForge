using System.Globalization;
using System.Diagnostics;
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
        Material Active, TranslateTransform3D Offset, Point3D Origin, AxisAngleRotation3D? Tilt);

    private readonly Viewport3D viewport = new() { ClipToBounds = true };
    private readonly PerspectiveCamera camera = new() { FieldOfView = 37, NearPlaneDistance = .1, FarPlaneDistance = 60 };
    private readonly Model3DGroup models = new();
    private readonly Dictionary<GeometryModel3D, Control> hits = new();
    private readonly Dictionary<Control, List<Part>> parts = new();
    private readonly Material shell = Finish(49, 49, 53, 24, 25);
    private readonly Material grip = Finish(30, 30, 32, 8, 12);
    private readonly Material metal = Finish(66, 66, 72, 48, 38);
    private OrbitPose pose = OrbitPose.Front, target = OrbitPose.Front;
    private bool orbiting;
    private bool paused;
    private long lastFrame;
    private Point start, last;
    private bool dragging;
    private int clicks;
    private Control? selected;

    public event Action<Control>? ControlSelected;

    public ControllerViewport()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "3D controller camera. Use the arrow keys to orbit and plus or minus to zoom.");
        Children.Add(viewport);
        viewport.Camera = camera;
        models.Children.Add(new AmbientLight(Color.FromRgb(168, 168, 168)));
        models.Children.Add(new DirectionalLight(Color.FromRgb(215, 215, 215), new Vector3D(.32, -.55, -.8)));
        models.Children.Add(new DirectionalLight(Color.FromRgb(133, 133, 133), new Vector3D(-.7, -.22, .65)));
        models.Children.Add(new DirectionalLight(Color.FromRgb(68, 68, 68), new Vector3D(-.5, .8, -.3)));
        viewport.Children.Add(new ModelVisual3D { Content = models });
        SizeChanged += (_, _) => UpdateCamera();
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopOrbit(); else if (pose != target) StartOrbit(); };
        Unloaded += (_, _) => StopOrbit();
        BuildModel();
        Reset();
    }

    private void BuildModel()
    {
        models.Children.Add(ControllerShell.Loft([(.84, -.48), (.93, -.42), (1, -.24), (1, -.045)], grip));
        models.Children.Add(ControllerShell.Cap(.84, -.48, false, grip));
        models.Children.Add(ControllerShell.Loft([(1, -.045), (1, -.006)], Matte(19, 19, 20)));
        models.Children.Add(ControllerShell.Loft([(1, -.006), (1, .15), (.98, .25), (.94, .34), (.89, .39)], shell));
        models.Children.Add(ControllerShell.Cap(.89, .39, true, shell));
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
            Add(new(x,y,.40), new(.225,.225,.045), metal);
            Add(new(x,y,.49), new(.100,.100,.127), metal, id);
            Add(new(x,y,.59), new(.167,.167,.061), grip, id);
            Add(new(x,y,.628), new(.130,.130,.035), shell, id);
        }

        Add(new(-.55,-.29,.397), new(.266,.266,.026), grip);
        AddDpad();

        Add(new(-1.13,.79,.13), new(.43,.115,.20), shell, Control.LeftBumper, 12);
        Add(new(1.13,.79,.13), new(.43,.115,.20), shell, Control.RightBumper, -12);
        Add(new(-1.15,.77,-.37), new(.275,.225,.235), shell, Control.LeftTrigger, 14);
        Add(new(1.15,.77,-.37), new(.275,.225,.235), shell, Control.RightTrigger, -14);
        Add(new(-.99,-.29,-.52), new(.34,.077,.046), metal, angle: 31);
        Add(new(-.75,-.05,-.51), new(.23,.068,.04), metal, angle: 29);
        Add(new(.99,-.29,-.52), new(.34,.077,.046), metal, angle: -31);
        Add(new(.75,-.05,-.51), new(.23,.068,.04), metal, angle: -29);
        for (int i = -1; i <= 1; i++) Add(new(i * .068, .064, .391), new(.016,.010,.006), metal);
        for (int i = -1; i <= 1; i++) Add(new(i * .095, -.53, -.487), new(.023,.023,.012), metal);
    }

    private void AddDpad()
    {
        var directions = new[] { Control.Right, Control.Up, Control.Left, Control.Down };
        for (int i = 0; i < 4; i++)
        {
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(new(.0, .0, .015));
            double middle = Math.PI * i / 2;
            foreach (double angle in new[] { middle - Math.PI / 4, middle - Math.PI / 8, middle, middle + Math.PI / 8, middle + Math.PI / 4 })
                mesh.Positions.Add(new(.243 * Math.Cos(angle), .243 * Math.Sin(angle), -.035));
            for (int vertex = 1; vertex < 5; vertex++)
            {
                mesh.TriangleIndices.Add(0);
                mesh.TriangleIndices.Add(vertex);
                mesh.TriangleIndices.Add(vertex + 1);
            }
            mesh.Freeze();
            var origin = new Point3D(-.55, -.29, .445);
            var move = new TranslateTransform3D(origin.X, origin.Y, origin.Z);
            var model = new GeometryModel3D(mesh, metal) { Transform = move };
            models.Children.Add(model);
            Track(directions[i], model, metal, move, origin, null);
        }
    }

    private static Material Finish(byte r, byte g, byte b, byte sheen, double power)
    {
        var material = new MaterialGroup();
        material.Children.Add(Matte(r, g, b));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(sheen, sheen, sheen)), power));
        material.Freeze();
        return material;
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
        AxisAngleRotation3D? tilt = null;
        if (control is Control.LeftTrigger or Control.RightTrigger)
        {
            tilt = new AxisAngleRotation3D(new(1, 0, 0), 0);
            transform.Children.Add(new RotateTransform3D(tilt, 0, .13, 0));
        }
        transform.Children.Add(offset);
        var model = new GeometryModel3D(Mesh(longitude), material) { Transform = transform };
        models.Children.Add(model);
        if (control is { } id) Track(id, model, material, offset, position, tilt);
        if (label is not null) Label(size, label, control, offset);
    }

    private void Track(Control id, GeometryModel3D model, Material material, TranslateTransform3D offset,
        Point3D position, AxisAngleRotation3D? tilt)
    {
        hits.Add(model, id);
        if (!parts.TryGetValue(id, out var list)) parts.Add(id, list = []);
        list.Add(new Part(model, material, Finish(92, 40, 44, 20, 25), Finish(156, 32, 35, 32, 25), offset, position, tilt));
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

    private void Label(Vector3D size, string label, Control? control, TranslateTransform3D offset)
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
        double d = size.X * .66, z = size.Z + .002;
        var face = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(-d,-d,z), new(d,-d,z), new(d,d,z), new(-d,d,z)
            },
            TextureCoordinates = new PointCollection { new(0,1), new(1,1), new(1,0), new(0,0) },
            TriangleIndices = new Int32Collection { 0,1,2,0,2,3 }
        };
        face.Freeze();
        var model = new GeometryModel3D(face, material) { Transform = offset };
        models.Children.Add(model);
        if (control is { } id) hits.Add(model,id);
    }

    public void ShowState(ControllerState state, Control? selection)
    {
        selected = selection;
        foreach (var (id, group) in parts)
        {
            var motion = PartMotion.From(id, state);
            foreach (Part part in group)
            {
                var material = motion.Active ? part.Active : selected == id ? part.Selected : part.Idle;
                if (part.Model.Material != material) part.Model.Material = material;
                double stickTravel = Math.Max(0, part.Origin.Z - .40) * .34;
                double x = part.Origin.X + motion.Stick.X * stickTravel;
                double y = part.Origin.Y + motion.Stick.Y * stickTravel;
                double z = part.Origin.Z - (part.Tilt is null ? motion.Travel * .026 : 0);
                if (part.Offset.OffsetX != x) part.Offset.OffsetX = x;
                if (part.Offset.OffsetY != y) part.Offset.OffsetY = y;
                if (part.Offset.OffsetZ != z) part.Offset.OffsetZ = z;
                if (part.Tilt is { } tilt && tilt.Angle != motion.Travel * 19) tilt.Angle = motion.Travel * 19;
            }
        }
    }

    public void Reset() => SetPose(OrbitPose.Front);
    public void Rear() => SetPose(OrbitPose.Rear with { Distance = target.Distance });

    public void PauseOrbit() { paused = true; StopOrbit(); }
    public void ResumeOrbit() { paused = false; if (IsVisible && pose != target) StartOrbit(); }

    public void StopOrbit()
    {
        if (!orbiting) return;
        CompositionTarget.Rendering -= RenderOrbit;
        orbiting = false;
    }

    private void SetPose(OrbitPose next)
    {
        target = next;
        if (IsVisible && SystemParameters.ClientAreaAnimation) StartOrbit();
        else { pose = target; UpdateCamera(); }
    }

    private void StartOrbit()
    {
        if (orbiting || paused) return;
        lastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += RenderOrbit;
        orbiting = true;
    }

    private void RenderOrbit(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        pose = pose.Interpolate(target, 1 - Math.Exp(-27 * Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds));
        lastFrame = now;
        if (Math.Abs(Math.IEEERemainder(pose.Yaw-target.Yaw, 2*Math.PI)) < .001 &&
            Math.Abs(pose.Pitch-target.Pitch) < .001 && Math.Abs(pose.Distance-target.Distance) < .003)
        {
            pose = target;
            StopOrbit();
        }
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        double aspect = ActualHeight > 0 ? ActualWidth / ActualHeight : 1.5;
        var position = pose.Position((float)Math.Max(1, aspect / 1.68));
        var anchor = new Point3D(0, -.15, 0);
        camera.Position = anchor + new Vector3D(position.X, position.Y, position.Z);
        camera.LookDirection = anchor - camera.Position;
        camera.UpDirection = new Vector3D(0,1,0);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        start = last = e.GetPosition(this);
        dragging = false;
        clicks = e.ClickCount;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        Point now = e.GetPosition(this);
        dragging |= (now - start).Length >= 5;
        if (dragging) SetPose(target.Drag(now.X-last.X, now.Y-last.Y));
        last = now;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        Point position = e.GetPosition(viewport);
        if (!dragging && (e.GetPosition(this) - start).Length < 5)
        {
            RayMeshGeometry3DHitTestResult? hit = null;
            VisualTreeHelper.HitTest(viewport, null, result =>
            {
                if (result is not RayMeshGeometry3DHitTestResult ray) return HitTestResultBehavior.Continue;
                if (hit is null || ray.DistanceToRayOrigin < hit.DistanceToRayOrigin) hit = ray;
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(position));
            if (hit?.ModelHit is GeometryModel3D part && hits.TryGetValue(part, out Control control))
                ControlSelected?.Invoke(control);
            else if (clicks == 2 && hit is null) Reset();
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetPose(target.Zoom(e.Delta / 120.0));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: SetPose(target.Drag(-14, 0)); break;
            case Key.Right: SetPose(target.Drag(14, 0)); break;
            case Key.Up: SetPose(target.Drag(0, -14)); break;
            case Key.Down: SetPose(target.Drag(0, 14)); break;
            case Key.Add: case Key.OemPlus: SetPose(target.Zoom(1)); break;
            case Key.Subtract: case Key.OemMinus: SetPose(target.Zoom(-1)); break;
            default: return;
        }
        e.Handled = true;
    }

}
