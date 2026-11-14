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
        Material Active, QuaternionRotation3D Rotation, TranslateTransform3D Offset, bool DoubleSided);

    private readonly Viewport3D viewport = new() { ClipToBounds = true };
    private readonly PerspectiveCamera camera = new() { FieldOfView = 37, NearPlaneDistance = .1, FarPlaneDistance = 60 };
    private readonly Model3DGroup models = new();
    private readonly Dictionary<GeometryModel3D, Control> hits = new();
    private readonly Dictionary<Control, List<Part>> parts = new();
    private OrbitPose pose = OrbitPose.Front, target = OrbitPose.Front;
    private bool orbiting;
    internal bool IsRenderingCamera => orbiting;
    private bool paused;
    public bool MeasureFrames { get; set; }
    private int frames;
    private double frameSum, frameMax;
    private TimeSpan? previousFrame;
    public string ReadFrameStats()
    {
        if (frames == 0) return "No active camera frames";
        string result = $"{frames * 1000 / frameSum:F0} FPS  ·  {frameSum / frames:F1} ms mean  ·  {frameMax:F1} ms max";
        frames = 0;
        frameSum = frameMax = 0;
        return result;
    }
    public bool ReduceMotion { get; set; }
    private long lastFrame;
    private Point start, last;
    private bool dragging;
    private int clicks;
    private bool testingInput;
    public bool TestingInput
    {
        get => testingInput;
        set
        {
            testingInput = value;
            if (value && IsMouseCaptured) ReleaseMouseCapture();
        }
    }
    private Control? selected;

    public event Action<Control>? ControlSelected;
    public event Action? CameraManuallyMoved;

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
        Children.Add(modelStatus);
        Loaded += LoadModel;
        Reset();
    }

    private bool assetLoaded;
    private readonly TextBlock modelStatus = new()
    {
        Text = "Loading controller…", HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };

    private async void LoadModel(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadModel;
        try
        {
            var asset = await EliteModel.Cached.Value;
            foreach (var part in asset.Parts)
            {
                var geometry = new GeometryModel3D(part.Mesh, part.Material);
                if (part.DoubleSided) geometry.BackMaterial = part.Material;
                models.Children.Add(geometry);
                if (part.Control is not { } id) continue;

                var turn = new QuaternionRotation3D();
                var offset = new TranslateTransform3D();
                var transform = new Transform3DGroup();
                transform.Children.Add(new RotateTransform3D(turn, part.Pivot));
                transform.Children.Add(offset);
                geometry.Transform = transform;
                hits.Add(geometry, id);
                if (!parts.TryGetValue(id, out var group)) parts.Add(id, group = []);
                group.Add(new(geometry, part.Material, Tint(part.Material, Color.FromRgb(105, 21, 28)),
                    Tint(part.Material, Color.FromRgb(172, 24, 32)), turn, offset, part.DoubleSided));
            }
            assetLoaded = true;
            Children.Remove(modelStatus);
            ShowState(lastState, selected);
        }
        catch (Exception ex)
        {
            modelStatus.Text = $"Controller asset could not load ({ex.GetType().Name})";
        }
    }

    internal Viewport3D Viewport => viewport;

    private static Material Tint(Material original, Color color)
    {
        var group = ((MaterialGroup)original).Clone();
        group.Children.Add(new EmissiveMaterial(new SolidColorBrush(color) { Opacity = .47 }));
        group.Freeze();
        return group;
    }

    private ControllerState lastState;

    public void ShowState(ControllerState state, Control? selection)
    {
        if (lastState == state && selected == selection && assetPresented) return;
        lastState = state;
        selected = selection;
        assetPresented = assetLoaded;
        foreach (var (id, group) in parts)
        {
            var motion = PartMotion.From(id, state);
            bool selectedStick = id switch
            {
                Control.LeftClick => selection is Control.LeftUp or Control.LeftDown or Control.LeftLeft or Control.LeftRight,
                Control.RightClick => selection is Control.RightUp or Control.RightDown or Control.RightLeft or Control.RightRight,
                _ => false
            };
            Quaternion rotation = Quaternion.Identity;
            if (motion.Stick != default)
                rotation = new Quaternion(new Vector3D(-motion.Stick.Y, motion.Stick.X, 0), 15 * motion.Stick.Length());
            else if (id is Control.LeftTrigger or Control.RightTrigger && motion.Travel > 0)
                rotation = new Quaternion(new Vector3D(1, 0, 0), motion.Travel * 14);
            double depth = id is Control.LeftTrigger or Control.RightTrigger or Control.Up or Control.Down or Control.Left or Control.Right
                ? 0 : -motion.Travel * .024;
            foreach (var part in group)
            {
                var material = motion.Active ? part.Active : selection == id || selectedStick ? part.Selected : part.Idle;
                if (part.Model.Material != material)
                {
                    part.Model.Material = material;
                    if (part.DoubleSided) part.Model.BackMaterial = material;
                }
                if (part.Rotation.Quaternion != rotation) part.Rotation.Quaternion = rotation;
                if (part.Offset.OffsetZ != depth) part.Offset.OffsetZ = depth;
            }
        }
    }

    private bool assetPresented;

    public void Reset() => SetPose(OrbitPose.Front);
    public void Front() => SetPose(OrbitPose.Front with { Distance = target.Distance });
    public void Rear() => SetPose(OrbitPose.Rear with { Distance = target.Distance });
    public void Shoulder() => SetPose(new OrbitPose(-.23, .93, target.Distance));
    public OrbitPose CameraPose => target;
    public void RestoreView(OrbitPose previous) => SetPose(previous);

    public void PauseOrbit() { paused = true; StopOrbit(); }
    public void ResumeOrbit() { paused = false; if (IsVisible && pose != target) StartOrbit(); }

    public void StopOrbit()
    {
        if (!orbiting) return;
        CompositionTarget.Rendering -= RenderOrbit;
        orbiting = false;
        previousFrame = null;
    }

    private void SetPose(OrbitPose next)
    {
        target = next;
        if (IsVisible && !ReduceMotion && SystemParameters.ClientAreaAnimation) StartOrbit();
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
        if (MeasureFrames && e is RenderingEventArgs render)
        {
            if (render.RenderingTime == previousFrame) return;
            if (previousFrame is { } start)
            {
                double interval = (render.RenderingTime - start).TotalMilliseconds;
                if (interval > 0)
                {
                    frames++;
                    frameSum += interval;
                    frameMax = Math.Max(frameMax, interval);
                }
            }
            previousFrame = render.RenderingTime;
        }
        if (dragging) return;
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
        var anchor = new Point3D(0, 0, 0);
        camera.Position = anchor + new Vector3D(position.X, position.Y, position.Z);
        camera.LookDirection = anchor - camera.Position;
        camera.UpDirection = new Vector3D(0,1,0);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (TestingInput) { e.Handled = true; return; }
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
        if (!dragging && (now - start).Length < 5) return;
        CameraManuallyMoved?.Invoke();
        dragging = true;
        target = pose = target.Drag(now.X-last.X, now.Y-last.Y);
        if (MeasureFrames) StartOrbit();
        else StopOrbit();
        UpdateCamera();
        last = now;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        bool wasDragging = dragging;
        ReleaseMouseCapture();
        Point position = e.GetPosition(viewport);
        if (!wasDragging && (e.GetPosition(this) - start).Length < 5)
        {
            RayMeshGeometry3DHitTestResult? hit = null;
            VisualTreeHelper.HitTest(viewport, null, result =>
            {
                if (result is not RayMeshGeometry3DHitTestResult ray) return HitTestResultBehavior.Continue;
                if (hit is null || ray.DistanceToRayOrigin < hit.DistanceToRayOrigin) hit = ray;
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(position));
            if (hit?.ModelHit is GeometryModel3D part && hits.TryGetValue(part, out Control control))
            {
                if (control is Control.Up or Control.Down or Control.Left or Control.Right)
                {
                    Point3D point = hit.PointHit;
                    control = DirectionalPad.Direction(point.X / .265 + 2.01, (point.Y / .265 - .95 + 1.04) / .964);
                }
                ControlSelected?.Invoke(control);
            }
            else if (clicks == 2 && hit is null) Reset();
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (dragging) StopOrbit();
        dragging = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (TestingInput) { e.Handled = true; return; }
        CameraManuallyMoved?.Invoke();
        SetPose(target.Zoom(e.Delta / 120.0));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (TestingInput) { e.Handled = true; return; }
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
