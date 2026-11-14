using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using InputForge.Core;
using Microsoft.Win32;
using Control = InputForge.Core.Control;

namespace InputForge.App;

public partial class MainWindow : Window
{
    private sealed record Preferences(string Application, MouseSettings Mouse, Dictionary<Control, int> Bindings);
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InputForge", "preferences.json");
    private readonly ControllerViewport model = new();
    private readonly InputRuntime runtime;
    private readonly DispatcherTimer refresh;
    private readonly Dictionary<string, Button> nav = new();
    private Preferences preferences;
    private bool enabled, testing;
    private Control selected = Control.A;
    private bool rebinding;
    private TextBlock? applicationStatus, signals, binding, selection, notice;
    private Button? rebind;
    private ComboBox? controlPicker;
    private string page = "Dashboard";
    private string mouseTab = "Sensitivity";
    private RuntimeStatus? last;
    private string? persistenceError;

    public MainWindow()
    {
        InitializeComponent();
        preferences = LoadPreferences();
        runtime = new InputRuntime(Config());
        foreach (string name in new[] { "Dashboard", "Controller", "Mouse", "Profiles", "Testing Lab", "Settings" })
        {
            var button = ButtonFor(name, () => Navigate(name));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0,0,0,4);
            nav.Add(name, button);
            Navigation.Children.Add(button);
        }
        model.ControlSelected += id =>
        {
            if (page == "Dashboard") { selected = id; Navigate("Controller"); }
            else if (page == "Controller") SelectControl(id);
        };
        Toggle.Click += (_, _) => { enabled = !enabled; Apply(); };
        PreviewKeyDown += HandleKey;
        Deactivated += (_, _) => { if (rebinding) CancelRebind(); };
        refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        refresh.Tick += (_, _) => RefreshOutput();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) refresh.Stop();
            else { RefreshOutput(); refresh.Start(); }
        };
        Navigate("Dashboard");
        refresh.Start();
    }

    private Preferences LoadPreferences()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var stored = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath));
                if (stored is not null && stored.Application is not null && stored.Mouse is not null &&
                    stored.Bindings is not null && stored.Bindings.Count == DefaultBindings.Create().Count &&
                    stored.Bindings.All(pair => Enum.IsDefined(pair.Key) && pair.Value is > 0 and < 261) &&
                    ValidMouse(stored.Mouse))
                    return stored;
                persistenceError = "Saved preferences were invalid. Defaults are loaded.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            persistenceError = "Preferences could not be read. Defaults are loaded.";
        }
        return new("", new MouseSettings(), DefaultBindings.Create());
    }

    private static bool ValidMouse(MouseSettings settings) =>
        settings.Horizontal is >= .1f and <= 4 && settings.Vertical is >= .1f and <= 4 &&
        settings.Deadzone is >= 0 and <= .35f && settings.Acceleration is >= 0 and <= 3 &&
        settings.Point1 is >= 0 and <= 1 && settings.Point2 is >= 0 and <= 1 &&
        Enum.IsDefined(settings.Curve) && Enum.IsDefined(settings.Filter);

    private void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(preferences));
            File.Move(SettingsPath + ".tmp", SettingsPath, true);
            persistenceError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            persistenceError = "InputForge could not save preferences.";
            if (notice is not null) notice.Text = persistenceError;
        }
    }

    private RuntimeConfig Config() => new(enabled, testing, preferences.Application, preferences.Bindings, preferences.Mouse);
    private void Apply()
    {
        runtime.Configure(Config());
        Toggle.Content = enabled ? "Mapping on" : "Mapping off";
        Toggle.Background = Brush(enabled ? "Active" : "Elevated");
        ProfileLabel.Text = string.IsNullOrEmpty(preferences.Application)
            ? "Default profile" : System.IO.Path.GetFileNameWithoutExtension(preferences.Application);
        last = null;
    }

    private void RefreshOutput()
    {
        var current = runtime.Read();
        if (current == last) return;
        last = current;
        ConnectionLabel.Text = current.Connection;
        ConnectionDot.Fill = Brush(current.Connection == "Connected" ? "Accent" : "Muted");
        if (applicationStatus is not null) applicationStatus.Text = current.Application;
        if (signals is not null)
            signals.Text = $"Left    {current.State.Left.X,6:F2}  {current.State.Left.Y,6:F2}\n" +
                $"Right   {current.State.Right.X,6:F2}  {current.State.Right.Y,6:F2}\n" +
                $"LT      {current.State.LT:F2}    RT    {current.State.RT:F2}\n" +
                $"Reports sent   {current.ReportsPerSecond}/s";
        if (model.IsVisible) model.ShowState(current.State, page == "Controller" ? selected : null);
    }

    private void Navigate(string name)
    {
        if (testing && name != "Testing Lab") testing = false;
        CancelRebind();
        if (model.Parent is Panel oldParent) oldParent.Children.Remove(model);
        page = name;
        applicationStatus = signals = binding = selection = notice = null;
        controlPicker = null;
        rebind = null;
        foreach (var (key, item) in nav)
        {
            item.Background = Brush(key == page ? "Active" : "Sidebar");
            item.Foreground = Brush(key == page ? "Text" : "Muted");
        }
        PageHost.Content = name switch
        {
            "Dashboard" => Dashboard(),
            "Controller" => ControllerPage(),
            "Mouse" => MousePage(),
            "Profiles" => Profiles(),
            "Testing Lab" => TestingLab(),
            _ => Settings()
        };
        Apply();
        RefreshOutput();
    }

    private DockPanel Frame(string title)
    {
        var layout = new DockPanel();
        var heading = Text(title, 19);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0,0,0,14);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        return layout;
    }

    private Border Surface(UIElement child, double inset = 14) => new()
    {
        Background = Brush("Panel"), Padding = new Thickness(inset),
        CornerRadius = new CornerRadius(9), Child = child
    };

    private Border Scene()
    {
        var scene = new Grid();
        scene.Children.Add(model);
        var views = Row(ButtonFor("Rear view", model.Rear), ButtonFor("Reset view", model.Reset));
        views.HorizontalAlignment = HorizontalAlignment.Right;
        views.VerticalAlignment = VerticalAlignment.Bottom;
        scene.Children.Add(views);
        return Surface(scene, 8);
    }

    private DockPanel Dashboard()
    {
        var frame = Frame("Dashboard");
        var details = new Grid { Margin = new Thickness(0,10,0,0) };
        details.ColumnDefinitions.Add(new ColumnDefinition());
        details.ColumnDefinitions.Add(new ColumnDefinition());
        details.ColumnDefinitions.Add(new ColumnDefinition());
        var app = Column(Text("Active application", 11, "Muted"), applicationStatus = Text("Inactive"));
        details.Children.Add(app);
        var horizontal = SliderFor("Horizontal", .1, 4, preferences.Mouse.Horizontal, x => ChangeMouse(preferences.Mouse with { Horizontal = x }));
        var vertical = SliderFor("Vertical", .1, 4, preferences.Mouse.Vertical, x => ChangeMouse(preferences.Mouse with { Vertical = x }));
        Grid.SetColumn(horizontal,1); Grid.SetColumn(vertical,2);
        horizontal.Margin = vertical.Margin = new Thickness(12,0,0,0);
        details.Children.Add(horizontal);
        details.Children.Add(vertical);
        DockPanel.SetDock(details,Dock.Bottom);
        frame.Children.Add(details);
        frame.Children.Add(Scene());
        return frame;
    }

    private DockPanel ControllerPage()
    {
        var frame = Frame("Controller");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });
        grid.Children.Add(Scene());
        binding = Text("", 14);
        selection = Text("", 15);
        rebind = ButtonFor("Rebind", BeginRebind);
        var reset = ButtonFor("Reset", () => SetBinding(DefaultBindings.Create()[selected]));
        controlPicker = Combo("Controller control", Enum.GetValues<Control>(), selected, SelectControl);
        var controls = Column(Text("Bindings", 11, "Muted"), controlPicker, selection, binding,
            Row(rebind, reset),
            Row(ButtonFor("Left mouse", () => BindMouse(256)), ButtonFor("Right mouse", () => BindMouse(257))),
            Row(ButtonFor("Middle", () => BindMouse(258)), ButtonFor("Mouse 4", () => BindMouse(259)), ButtonFor("Mouse 5", () => BindMouse(260))),
            notice = Text("", 11, "Muted"));
        var right = Surface(controls, 12);
        right.Margin = new Thickness(12,0,0,0);
        Grid.SetColumn(right,1);
        grid.Children.Add(right);
        frame.Children.Add(grid);
        SelectControl(selected);
        return frame;
    }

    private void SelectControl(Control id)
    {
        if (id != selected) CancelRebind();
        selected = id;
        if (controlPicker is not null && !Equals(controlPicker.SelectedItem, id))
            controlPicker.SelectedItem = id;
        if (selection is not null) selection.Text = Display(id);
        if (binding is not null) binding.Text = DisplayInput(preferences.Bindings[id]);
        model.ShowState(runtime.Read().State, selected);
    }

    private void BeginRebind()
    {
        if (rebinding) { CancelRebind(); return; }
        rebinding = true;
        rebind!.Content = "Cancel";
        notice!.Text = "Press a keyboard key or choose a mouse button.";
    }

    private void CancelRebind()
    {
        rebinding = false;
        if (rebind is not null) rebind.Content = "Rebind";
        if (notice is not null) notice.Text = persistenceError ?? "";
    }

    private void BindMouse(int key)
    {
        if (rebinding) SetBinding(key);
        else if (notice is not null) notice.Text = "Select Rebind to choose a mouse input.";
    }

    private void SetBinding(int key)
    {
        var bindings = new Dictionary<Control,int>(preferences.Bindings) { [selected] = key };
        preferences = preferences with { Bindings = bindings };
        SavePreferences();
        CancelRebind();
        SelectControl(selected);
        var duplicate = bindings.FirstOrDefault(pair => pair.Key != selected && pair.Value == key);
        if (notice is not null && persistenceError is null && duplicate.Value != 0)
            notice.Text = $"Also assigned to {Display(duplicate.Key)}.";
        Apply();
    }

    private void HandleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (rebinding) CancelRebind();
            else { enabled = testing = false; if (page == "Testing Lab") Navigate(page); else Apply(); }
            e.Handled = true;
            return;
        }
        if (!rebinding || e.Key is Key.Tab or Key.System || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ||
            ((e.Key is Key.Space or Key.Enter) && Keyboard.FocusedElement is Button button && button != rebind))
            return;
        int raw = KeyInterop.VirtualKeyFromKey(e.Key);
        raw = raw switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => raw };
        if (raw is > 0 and < 255) SetBinding(raw);
        e.Handled = true;
    }

    private DockPanel MousePage()
    {
        var frame = Frame("Mouse");
        var tabs = Row("Sensitivity", "Filtering", "Response Curve");
        DockPanel.SetDock(tabs, Dock.Top);
        tabs.Margin = new Thickness(0,0,0,12);
        frame.Children.Add(tabs);
        UIElement content;
        var mouse = preferences.Mouse;
        switch (mouseTab)
        {
            case "Filtering":
                content = Column(Text("Smoothing", 12, "Muted"), Combo("Smoothing", Enum.GetValues<Smoothing>(), mouse.Filter,
                    value => ChangeMouse(preferences.Mouse with { Filter = value })),
                    SliderFor("Deadzone", 0, .35, mouse.Deadzone, x => ChangeMouse(preferences.Mouse with { Deadzone = x })));
                break;
            case "Response Curve":
                var chart = new CurveView(mouse, (x,y) =>
                    ChangeMouse(x == 0 ? preferences.Mouse with { Point1 = y } : preferences.Mouse with { Point2 = y }));
                var curve = new DockPanel();
                var options = Row(Text("Response", 12, "Muted"), Combo("Response curve", Enum.GetValues<ResponseCurve>(), mouse.Curve, value =>
                {
                    ChangeMouse(preferences.Mouse with { Curve = value });
                    PageHost.Content = MousePage();
                }));
                DockPanel.SetDock(options,Dock.Top);
                curve.Children.Add(options);
                var help = Text(mouse.Curve == ResponseCurve.Custom ? "Drag either red control point." : "Input magnitude → stick output", 11, "Muted");
                DockPanel.SetDock(help,Dock.Bottom);
                curve.Children.Add(help);
                curve.Children.Add(chart);
                content = curve;
                break;
            default:
                content = Column(
                    SliderFor("Horizontal sensitivity", .1, 4, mouse.Horizontal, x => ChangeMouse(preferences.Mouse with { Horizontal = x })),
                    SliderFor("Vertical sensitivity", .1, 4, mouse.Vertical, x => ChangeMouse(preferences.Mouse with { Vertical = x })),
                    SliderFor("Acceleration", 0, 3, mouse.Acceleration, x => ChangeMouse(preferences.Mouse with { Acceleration = x })));
                break;
        }
        frame.Children.Add(Surface(content));
        return frame;
    }

    private StackPanel Row(params string[] tabs)
    {
        var buttons = tabs.Select(name =>
        {
            var button = ButtonFor(name, () => { mouseTab = name; PageHost.Content = MousePage(); });
            if (name == mouseTab) button.Background = Brush("Active");
            return (UIElement)button;
        }).ToArray();
        return Row(buttons);
    }

    private void ChangeMouse(MouseSettings mouse)
    {
        preferences = preferences with { Mouse = mouse };
        Apply();
        delayedSave.Stop();
        delayedSave.Start();
    }

    private readonly DispatcherTimer delayedSave = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private DockPanel Profiles()
    {
        var frame = Frame("Profiles");
        var path = Text(preferences.Application.Length == 0 ? "No executable selected" : preferences.Application, 12, "Muted");
        var browse = ButtonFor("Choose executable", () =>
        {
            var picker = new OpenFileDialog { Filter = "Windows applications (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            preferences = preferences with { Application = picker.FileName };
            SavePreferences();
            Navigate(page);
        });
        frame.Children.Add(Surface(Column(Text("Allowed application", 15), path,
            Row(browse, ButtonFor("Clear", () =>
            {
                preferences = preferences with { Application = "" };
                SavePreferences();
                Navigate(page);
            })), notice = Text(persistenceError ?? "",11,"Muted"))));
        return frame;
    }

    private DockPanel TestingLab()
    {
        var frame = Frame("Testing Lab");
        var panel = Row(ButtonFor(testing ? "Stop local test" : "Start local test", () =>
            { testing = !testing; Navigate(page); }), Text(testing ? "Local test · real captured input · driver output disabled" : "Local test is off", 12, "Muted"));
        panel.Margin = new Thickness(0,0,0,10);
        DockPanel.SetDock(panel,Dock.Top);
        frame.Children.Add(panel);
        var readout = signals = Text("", 12, "Muted");
        readout.FontFamily = new FontFamily("Consolas");
        readout.Margin = new Thickness(0,10,0,0);
        DockPanel.SetDock(readout,Dock.Bottom);
        frame.Children.Add(readout);
        frame.Children.Add(Scene());
        return frame;
    }

    private DockPanel Settings()
    {
        var frame = Frame("Settings");
        frame.Children.Add(Surface(Column(Text("Controller backend", 15),
            Text("ViGEmBus is required for virtual Xbox output. It is separately installed and retired.", 12, "Muted"),
            Text("Virtual controllers may be visible system-wide. Local testing needs no driver.", 12, "Muted"),
            Text("Escape disables mapping when this window has focus.", 12, "Muted"),
            Text("Rendering tier " + (RenderCapability.Tier >> 16), 12, "Muted"))));
        return frame;
    }

    private static string Display(Control control) =>
        System.Text.RegularExpressions.Regex.Replace(control.ToString(), "([a-z])([A-Z])", "$1 $2");
    private static string DisplayInput(int value) => value >= 256
        ? new[] { "Left mouse", "Right mouse", "Middle mouse", "Mouse 4", "Mouse 5" }[value - 256]
        : KeyInterop.KeyFromVirtualKey(value).ToString();

    internal static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private static TextBlock Text(string value, double size = 13, string brush = "Text") =>
        new() { Text = value, FontSize = size, Foreground = Brush(brush), TextWrapping = TextWrapping.Wrap };

    private static Button ButtonFor(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel Column(params UIElement[] items)
    {
        var panel = new StackPanel();
        foreach (UIElement item in items)
        {
            if (item is FrameworkElement element) element.Margin = new Thickness(0,0,0,9);
            panel.Children.Add(item);
        }
        return panel;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement item in items)
        {
            if (item is FrameworkElement element) { element.Margin = new Thickness(0,0,5,0); element.VerticalAlignment = VerticalAlignment.Center; }
            panel.Children.Add(item);
        }
        return panel;
    }

    private static ComboBox Combo<T>(string name, T[] items, T value, Action<T> changed) where T : struct
    {
        var combo = new ComboBox { ItemsSource = items, SelectedItem = value, MinWidth = 125 };
        System.Windows.Automation.AutomationProperties.SetName(combo, name);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is T selected) changed(selected);
        };
        return combo;
    }

    private static StackPanel SliderFor(string label, double min, double max, float value, Action<float> changed)
    {
        var number = Text(value.ToString("F2"), 12, "Muted");
        var caption = new DockPanel();
        DockPanel.SetDock(number,Dock.Right);
        caption.Children.Add(number);
        caption.Children.Add(Text(label,12));
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = .01, LargeChange = .1 };
        System.Windows.Automation.AutomationProperties.SetName(slider,label);
        slider.ValueChanged += (_, _) =>
        {
            number.Text = slider.Value.ToString("F2");
            changed((float)slider.Value);
        };
        var panel = new StackPanel();
        panel.Children.Add(caption);
        panel.Children.Add(slider);
        return panel;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        delayedSave.Tick += (_, _) => { delayedSave.Stop(); SavePreferences(); };
    }

    protected override void OnClosed(EventArgs e)
    {
        refresh.Stop();
        delayedSave.Stop();
        SavePreferences();
        runtime.Dispose();
        base.OnClosed(e);
    }
}
