using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Runtime.InteropServices;
using InputForge.Core;
using Microsoft.Win32;
using Control = InputForge.Core.Control;

namespace InputForge.App;

public partial class MainWindow : Window
{
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InputForge", "preferences.json");
    private readonly ControllerViewport model = new();
    private readonly InputRuntime runtime;
    private readonly DispatcherTimer refresh;
    private readonly Dictionary<string, Button> nav = new();
    private Preferences preferences;
    private bool enabled, testing;
    private readonly BindingSelection bindingSelection = new();
    private Control Selected => bindingSelection.Current;
    private bool rebinding;
    private TextBlock? applicationStatus, signals, binding, selection, notice;
    private Button? rebind;
    private Button? localTest;
    private TextBlock? localTestLabel;
    private ListBox? bindingList;
    private TextBlock? selectedBadge, controllerStatus, previewStatus;
    private readonly Dictionary<Control, (ListBoxItem Item, TextBlock Value)> bindingRows = new();
    private bool viewerExpanded;
    private WindowState previousWindowState;
    private WindowStyle previousWindowStyle;
    private string page = "Dashboard";
    private string mouseTab = "Sensitivity";
    private RuntimeStatus? last;
    private string? persistenceError;

    public MainWindow()
    {
        InitializeComponent();
        preferences = LoadPreferences();
        runtime = new InputRuntime(Config());
        foreach (var (name, icon) in new[] { ("Dashboard", "\uE80F"), ("Controller", "\uE7FC"), ("Mouse", "\uE962"),
            ("Profiles", "\uE8B7"), ("Testing Lab", "\uE9D9"), ("Settings", "\uE713") })
        {
            var button = ButtonFor(name, () => Navigate(name));
            button.Content = Row(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), Width = 18 }, new TextBlock { Text = name });
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0,0,0,4);
            nav.Add(name, button);
            Navigation.Children.Add(button);
        }
        bindingSelection.Changed += PresentSelection;
        model.ControlSelected += bindingSelection.Select;
        Toggle.Click += (_, _) => { enabled = !enabled; Apply(); };
        PreviewKeyDown += HandleKey;
        Deactivated += (_, _) => { if (rebinding) { CancelRebind(); Apply(); } };
        refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        refresh.Tick += (_, _) => RefreshOutput();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) { refresh.Stop(); model.PauseOrbit(); }
            else { model.ResumeOrbit(); RefreshOutput(); refresh.Start(); }
        };
        Navigate("Dashboard");
        SizeChanged += (_, _) => Navigation.Width = ActualWidth < 1030 ? 126 : 195;
        refresh.Start();
    }

    private Preferences LoadPreferences()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return ProfileStore.Read(SettingsPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            persistenceError = "Preferences could not be read. Defaults are loaded.";
        }
        return Preferences.Create();
    }

    private void SavePreferences()
    {
        try
        {
            ProfileStore.Write(SettingsPath, preferences);
            persistenceError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            persistenceError = "InputForge could not save preferences.";
            if (notice is not null) { notice.Text = persistenceError; notice.Visibility = Visibility.Visible; }
        }
    }

    private RuntimeConfig Config() => new(!rebinding && enabled, !rebinding && testing, preferences.Application, preferences.Bindings, preferences.Mouse);
    private void Apply()
    {
        model.TestingInput = testing || rebinding;
        runtime.Configure(Config());
        Toggle.Content = enabled ? "Mapping on" : "Mapping off";
        Toggle.Background = Brush(enabled ? "Active" : "Elevated");
        if (localTest is not null) localTest.Content = testing ? "Stop input test" : "Test inputs";
        if (localTestLabel is not null) localTestLabel.Text = testing ? "Testing Mode" : "";
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
        if (controllerStatus is not null) controllerStatus.Text = current.Connection;
        if (previewStatus is not null)
        {
            string inputs = string.Join(" · ", Enum.GetValues<Control>().Where(id => (int)id <= (int)Control.RightClick && current.State.IsPressed(id)).Select(Glyph));
            if (current.State.Left.LengthSquared() > .0025f) inputs += (inputs.Length == 0 ? "" : " · ") + "LS";
            if (current.State.Right.LengthSquared() > .0025f) inputs += (inputs.Length == 0 ? "" : " · ") + "RS";
            previewStatus.Text = inputs.Length == 0 ? "Neutral" : inputs;
            previewStatus.ToolTip = previewStatus.Text;
        }
        if (signals is not null)
            signals.Text = $"Left    {current.State.Left.X,6:F2}  {current.State.Left.Y,6:F2}\n" +
                $"Right   {current.State.Right.X,6:F2}  {current.State.Right.Y,6:F2}\n" +
                $"LT      {current.State.LT:F2}    RT    {current.State.RT:F2}\n" +
                $"Reports sent   {current.ReportsPerSecond}/s";
        if (model.IsVisible) model.ShowState(current.State, page is "Dashboard" or "Controller" ? Selected : null);
    }

    private void Navigate(string name)
    {
        if (testing && name is not ("Dashboard" or "Controller" or "Testing Lab")) testing = false;
        CancelRebind();
        if (model.Parent is Panel oldParent) oldParent.Children.Remove(model);
        page = name;
        applicationStatus = signals = binding = selection = notice = null;
        bindingList = null;
        bindingRows.Clear();
        selectedBadge = controllerStatus = previewStatus = null;
        rebind = null;
        mouseChoices = null;
        localTest = null;
        localTestLabel = null;
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
        var heading = Text(title, 17);
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
        scene.RowDefinitions.Add(new RowDefinition());
        scene.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        scene.Children.Add(model);
        var views = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var button in new[] { ButtonFor("Front view", model.Front), ButtonFor("Rear view", model.Rear),
            ButtonFor("Reset view", model.Reset), ButtonFor(viewerExpanded ? "Exit fullscreen" : "Fullscreen", ToggleFullscreen),
            localTest = ButtonFor("Test inputs", () => { testing = !testing; Apply(); }) })
        {
            button.Margin = new Thickness(3, 4, 3, 0);
            views.Children.Add(button);
        }
        localTestLabel = Text("", 11, "Muted");
        localTestLabel.HorizontalAlignment = HorizontalAlignment.Center;
        var bottom = new StackPanel();
        bottom.Children.Add(views);
        bottom.Children.Add(localTestLabel);
        Grid.SetRow(bottom, 1);
        scene.Children.Add(bottom);
        return new Border { Child = scene };
    }

    private void ToggleFullscreen()
    {
        viewerExpanded = !viewerExpanded;
        if (viewerExpanded)
        {
            previousWindowStyle = WindowStyle;
            previousWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Header.Visibility = SidebarPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowState = previousWindowState;
            WindowStyle = previousWindowStyle;
            Header.Visibility = SidebarPanel.Visibility = Visibility.Visible;
        }
        Navigate(page);
    }

    private GridLength inspectorWidth = new(455);

    private DockPanel Dashboard() => ControllerWorkspace("Dashboard");
    private DockPanel ControllerPage() => ControllerWorkspace("Controller");

    private DockPanel ControllerWorkspace(string title)
    {
        if (viewerExpanded)
        {
            var frame = new DockPanel();
            frame.Children.Add(Scene());
            return frame;
        }
        var workspace = new DockPanel();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 260 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        var inspector = new ColumnDefinition { Width = inspectorWidth, MinWidth = 280 };
        grid.ColumnDefinitions.Add(inspector);
        grid.SizeChanged += (_, _) =>
        {
            inspector.MaxWidth = Math.Max(280, grid.ActualWidth - 272);
            inspector.Width = inspectorWidth;
        };

        var viewer = Frame(title);
        var status = new Grid();
        for (int i = 0; i < 3; i++)
            status.ColumnDefinitions.Add(new ColumnDefinition());
        var cells = new[]
        {
            StatusCell("Controller status", controllerStatus = Text("Standby", 12, "Muted")),
            StatusCell("Active application", applicationStatus = Text("Inactive", 12, "Muted")),
            StatusCell("Live input preview", previewStatus = Text("Neutral", 12, "Muted"))
        };
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            status.Children.Add(cells[i]);
        }
        var statusStrip = Surface(status, 10);
        statusStrip.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(statusStrip, Dock.Bottom);
        viewer.Children.Add(statusStrip);
        viewer.Children.Add(Scene());
        grid.Children.Add(viewer);

        var splitter = new GridSplitter
        {
            Width = 8, Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        splitter.DragCompleted += (_, _) => inspectorWidth = new GridLength(inspector.ActualWidth);
        System.Windows.Automation.AutomationProperties.SetName(splitter, "Resize binding panel");
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        var panel = new DockPanel();
        var heading = new DockPanel { LastChildFill = true };
        selectedBadge = Text("", 17);
        var badge = Surface(selectedBadge, 9);
        badge.Background = Brush("Active");
        badge.Margin = new Thickness(0, 0, 11, 0);
        DockPanel.SetDock(badge, Dock.Left);
        heading.Children.Add(badge);
        selection = Text("", 16);
        selection.FontWeight = FontWeights.SemiBold;
        heading.Children.Add(selection);
        binding = Text("", 14);
        var currentBinding = Surface(binding, 9);
        currentBinding.Background = Brush("Elevated");
        rebind = ButtonFor("Rebind", BeginRebind);
        rebind.Background = Brush("Accent");
        var clear = ButtonFor("Clear", () => SetBinding(0));
        var reset = ButtonFor("Reset", () => SetBinding(DefaultBindings.Create()[Selected]));
        mouseChoices = Column(
            Row(ButtonFor("Left mouse", () => BindMouse(256)), ButtonFor("Right mouse", () => BindMouse(257))),
            Row(ButtonFor("Middle", () => BindMouse(258)), ButtonFor("Mouse 4", () => BindMouse(259)), ButtonFor("Mouse 5", () => BindMouse(260))));
        mouseChoices.Visibility = Visibility.Collapsed;
        notice = Text(persistenceError ?? "", 11, "Muted");
        notice.Visibility = string.IsNullOrEmpty(notice.Text) ? Visibility.Collapsed : Visibility.Visible;
        var card = Surface(Column(heading, Text("Binding", 12, "Muted"), currentBinding,
            Row(rebind, clear, reset), mouseChoices, notice), 14);
        card.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(card, Dock.Top);
        panel.Children.Add(card);

        var list = new ListBox
        {
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = Brush("Text"), Padding = new Thickness(3),
            ItemContainerStyle = (Style)FindResource("BindingRow")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        System.Windows.Automation.AutomationProperties.SetName(list, "Controller bindings");
        bindingList = list;
        foreach (Control id in Enum.GetValues<Control>())
        {
            var line = new Grid();
            foreach (GridLength width in new[] { new GridLength(44), new GridLength(1, GridUnitType.Star),
                new GridLength(1, GridUnitType.Star), new GridLength(14) })
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var glyph = new Border { BorderBrush = Brush("Muted"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Left, Child = Text(Glyph(id), 11) };
            var name = Text(Display(id), 12);
            var value = Text(DisplayInput(preferences.Bindings[id]), 12, "Muted");
            value.TextAlignment = TextAlignment.Right;
            value.Margin = new Thickness(4,0,8,0);
            foreach (var text in new[] { name, value })
            {
                text.TextWrapping = TextWrapping.NoWrap;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.VerticalAlignment = VerticalAlignment.Center;
            }
            var arrow = Text("›", 16, "Muted");
            Grid.SetColumn(name, 1); Grid.SetColumn(value, 2); Grid.SetColumn(arrow, 3);
            line.Children.Add(glyph); line.Children.Add(name); line.Children.Add(value); line.Children.Add(arrow);
            var item = new ListBoxItem { Tag = id, Content = line };
            System.Windows.Automation.AutomationProperties.SetName(item, Display(id));
            bindingRows.Add(id, (item, value));
            list.Items.Add(item);
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: Control id }) bindingSelection.Select(id);
        };
        panel.Children.Add(Surface(list, 6));
        Grid.SetColumn(panel, 2);
        grid.Children.Add(panel);
        workspace.Children.Add(grid);
        PresentSelection(Selected);
        return workspace;
    }

    private static Border StatusCell(string title, TextBlock value)
    {
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        var label = Text(title, 12);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 0, 0, 4);
        return new Border { Padding = new Thickness(7, 1, 5, 1), Child = new StackPanel { Children = { label, value } } };
    }

    private void PresentSelection(Control id)
    {
        if (rebinding) { CancelRebind(); Apply(); }
        if (bindingRows.TryGetValue(id, out var row))
        {
            if (bindingList!.SelectedItem != row.Item) bindingList.SelectedItem = row.Item;
            bindingList.ScrollIntoView(row.Item);
        }
        if (selectedBadge is not null) selectedBadge.Text = Glyph(id);
        if (selection is not null) selection.Text = Display(id);
        string current = DisplayInput(preferences.Bindings[id]);
        if (binding is not null) binding.Text = current;
        if (row.Value is not null) row.Value.Text = current;
        model.ShowState(runtime.Read().State, id);
    }

    private static string Glyph(Control id) => id switch
    {
        Control.LeftBumper => "LB", Control.RightBumper => "RB",
        Control.LeftTrigger => "LT", Control.RightTrigger => "RT",
        Control.LeftClick => "LS", Control.RightClick => "RS",
        Control.Up => "↑", Control.Down => "↓", Control.Left => "←", Control.Right => "→",
        Control.View => "▣", Control.Menu => "≡",
        Control.LeftUp => "LS ↑", Control.LeftDown => "LS ↓", Control.LeftLeft => "LS ←", Control.LeftRight => "LS →",
        Control.RightUp => "RS ↑", Control.RightDown => "RS ↓", Control.RightLeft => "RS ←", Control.RightRight => "RS →",
        _ => id.ToString()
    };

    private StackPanel? mouseChoices;

    private void BeginRebind()
    {
        if (rebinding) { CancelRebind(); Apply(); return; }
        rebinding = true;
        rebind!.Content = "Cancel";
        mouseChoices!.Visibility = Visibility.Visible;
        notice!.Text = "Press a keyboard key or choose a mouse button.";
        notice.Visibility = Visibility.Visible;
        Apply();
    }

    private void CancelRebind()
    {
        rebinding = false;
        if (rebind is not null) rebind.Content = "Rebind";
        if (mouseChoices is not null) mouseChoices.Visibility = Visibility.Collapsed;
        if (notice is not null)
        {
            notice.Text = persistenceError ?? "";
            notice.Visibility = string.IsNullOrEmpty(notice.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void BindMouse(int key)
    {
        if (rebinding) SetBinding(key);
        else if (notice is not null) notice.Text = "Select Rebind to choose a mouse input.";
    }

    private void SetBinding(int key)
    {
        var bindings = new Dictionary<Control,int>(preferences.Bindings) { [Selected] = key };
        preferences = preferences with { Bindings = bindings };
        SavePreferences();
        CancelRebind();
        PresentSelection(Selected);
        var duplicate = key == 0 ? default : bindings.FirstOrDefault(pair => pair.Key != Selected && pair.Value == key);
        if (notice is not null && persistenceError is null && duplicate.Value != 0)
        {
            notice.Text = $"Also assigned to {Display(duplicate.Key)}.";
            notice.Visibility = Visibility.Visible;
        }
        Apply();
    }

    private void HandleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (rebinding) { CancelRebind(); Apply(); }
            else { enabled = testing = false; if (viewerExpanded) ToggleFullscreen(); else Apply(); }
            e.Handled = true;
            return;
        }
        if (testing && !rebinding && e.Key is Key.Space or Key.Enter && Keyboard.FocusedElement is Button or ListBoxItem)
        {
            e.Handled = true;
            return;
        }
        if (!rebinding || e.Key is Key.Tab or Key.System || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ||
            ((e.Key is Key.Space or Key.Enter) && Keyboard.FocusedElement is Button button && button != rebind))
            return;
        int raw = KeyInterop.VirtualKeyFromKey(e.Key);
        raw = raw switch { 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => raw };
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
        if (viewerExpanded) { frame.Children.Add(Scene()); return frame; }
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

    private static string Display(Control control) => control switch
    {
        Control.A or Control.B or Control.X or Control.Y => $"{control} Button",
        Control.LeftClick => "Left Stick (Click)", Control.RightClick => "Right Stick (Click)",
        Control.Up or Control.Down or Control.Left or Control.Right => $"D-pad {control}",
        _ when (int)control >= (int)Control.LeftUp => System.Text.RegularExpressions.Regex.Replace(control.ToString(), "^(Left|Right)", "$1 Stick "),
        _ => System.Text.RegularExpressions.Regex.Replace(control.ToString(), "([a-z])([A-Z])", "$1 $2")
    };
    private static string DisplayInput(int value) => value == 0 ? "Unbound" : value >= 256
        ? new[] { "Left Mouse Button", "Right Mouse Button", "Middle Mouse Button", "Mouse 4", "Mouse 5" }[value - 256]
        : value switch { DefaultBindings.LeftShift => "Left Shift", DefaultBindings.RightShift => "Right Shift",
            0x10 => "Either Shift", _ => KeyInterop.KeyFromVirtualKey(value).ToString() };

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        // Windows 11 exposes both dark mode and an explicit caption colour.
        // Explicit colours keep the caption dark even with the system light theme.
        if (!SystemParameters.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
            int caption = 0x000C0B0B, text = 0x00F5F4F4;
            if (DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)) == 0)
            {
                DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
                return;
            }
        }
        if (SystemParameters.HighContrast) return;

        // WindowChrome retains native move, double-click, snap and resize hit regions.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 47, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });
        WindowStyle = WindowStyle.None;
        CaptionButtons.Visibility = Visibility.Visible;
        var minimize = ButtonFor("−", () => SystemCommands.MinimizeWindow(this));
        var maximize = ButtonFor("□", () =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        });
        var close = ButtonFor("×", () => SystemCommands.CloseWindow(this));
        foreach (var (button, name) in new[] { (minimize, "Minimize"), (maximize, "Maximize"), (close, "Close") })
        {
            button.Background = Brush("Sidebar");
            button.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            CaptionButtons.Children.Add(button);
        }
        WindowChrome.SetIsHitTestVisibleInChrome(Toggle, true);
        StateChanged += (_, _) =>
        {
            bool isMaximized = WindowState == WindowState.Maximized;
            maximize.Content = isMaximized ? "❐" : "□";
            maximize.ToolTip = isMaximized ? "Restore" : "Maximize";
            System.Windows.Automation.AutomationProperties.SetName(maximize, (string)maximize.ToolTip);
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
