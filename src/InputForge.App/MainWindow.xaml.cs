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
    private static readonly string LibraryPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SettingsPath)!, "profiles.json");
    private static readonly string OptionsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SettingsPath)!, "interface.json");
    private readonly ControllerViewport model = new();
    private readonly InputRuntime runtime;
    private readonly DispatcherTimer refresh;
    private readonly Dictionary<string, Button> nav = new();
    private Preferences preferences;
    private ProfileLibrary library;
    private sealed record InterfaceOptions(bool ReduceMotion = false, bool Capture = true, string StartPage = "Dashboard",
        bool PreviewDiagnostics = false, bool AutoCamera = true, int CameraIdle = 2,
        bool StartMinimized = false, bool ConfirmExit = true, bool RememberWindow = false,
        double? WindowLeft = null, double? WindowTop = null);
    private InterfaceOptions options;
    private bool enabled, testing;
    private readonly BindingSelection bindingSelection = new();
    private Control Selected => bindingSelection.Current;
    private bool rebinding;
    private TextBlock? applicationStatus, signals, binding, selection, notice;
    private Button? rebind;
    private Button? localTest;
    private TextBlock? localTestLabel;

    private TextBlock? selectedBadge, controllerStatus, controllerMapping;

    private bool viewerExpanded;
    private WindowState previousWindowState;
    private WindowStyle previousWindowStyle;
    private string page = "Dashboard";

    private RuntimeStatus? last;
    private string? persistenceError;
    private bool updatingProfileSelector;

    public MainWindow()
    {
        InitializeComponent();
        SidebarVersion.Text = "InputForge " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        library = LoadLibrary();
        preferences = library.Profiles.Single(p => p.Id == library.Selected).Settings;
        options = LoadInterfaceOptions();
        runtime = new InputRuntime(Config());
        foreach (var (name, icon) in new[] { ("Dashboard", "\uE80F"), ("Controller", "\uE7FC"), ("Mouse", "\uE962"),
            ("Profiles", "\uE8B7"), ("Testing Lab", "\uE9D9"), ("Settings", "\uE713") })
        {
            var button = ButtonFor(name, () => Navigate(name));
            button.Content = Row(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), Width = 30, FontSize = 20 }, new TextBlock { Text = name, FontSize = 15 });
            button.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.MinHeight = 50;
            button.Margin = new Thickness(0,0,0,8);
            button.MouseEnter += (_, _) => button.Foreground = Brush("Text");
            button.MouseLeave += (_, _) => button.Foreground = Brush(name == page ? "Text" : "Muted");
            nav.Add(name, button);
            Navigation.Children.Add(button);
        }
        bindingSelection.Changed += PresentSelection;
        model.ControlSelected += bindingSelection.Select;
        model.CameraManuallyMoved += () =>
        {
            cameraBeforeShoulder = null;
            testCamera.Manual(ClockSeconds());
        };
        Toggle.Click += (_, _) => { enabled = !enabled; Apply(); };
        ProfileSelector.SelectionChanged += (_, _) =>
        {
            if (!updatingProfileSelector && ProfileSelector.SelectedItem is NamedProfile profile)
                SelectProfile(profile);
        };
        PreviewKeyDown += HandleKey;
        Deactivated += (_, _) => { if (rebinding) { CancelRebind(); Apply(); } };
        refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        refresh.Tick += (_, _) => RefreshOutput();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) { refresh.Stop(); model.PauseOrbit(); }
            else { model.ResumeOrbit(); RefreshOutput(); refresh.Start(); }
        };
        model.ReduceMotion = options.ReduceMotion;
        model.MeasureFrames = options.PreviewDiagnostics;
        Navigate(options.StartPage is { } start && nav.ContainsKey(start) ? start : "Dashboard");
        if (options.RememberWindow && options.WindowLeft is double left && options.WindowTop is double top &&
            SystemParameters.VirtualScreenLeft <= left && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
            SystemParameters.VirtualScreenTop <= top && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (options.StartMinimized) WindowState = WindowState.Minimized;
        SizeChanged += (_, _) =>
        {
            bool compact = ActualWidth < 1100;
            SidebarContents.Width = compact ? 72 : 258;
            SidebarStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var item in nav.Values)
                ((StackPanel)item.Content).Children[1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            PageHost.Margin = new Thickness(ActualWidth < 1000 ? 20 : 30);
        };
        refresh.Start();
    }

    private ProfileLibrary LoadLibrary()
    {
        try
        {
            if (File.Exists(LibraryPath)) return ProfileStore.ReadLibrary(LibraryPath);
            if (File.Exists(SettingsPath)) return ProfileLibrary.FromLegacy(ProfileStore.Read(SettingsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            persistenceError = "Preferences could not be read. Defaults are loaded.";
        }
        return ProfileLibrary.FromLegacy(Preferences.Create());
    }

    private static InterfaceOptions LoadInterfaceOptions()
    {
        try
        {
            return File.Exists(OptionsPath)
                ? JsonSerializer.Deserialize<InterfaceOptions>(File.ReadAllText(OptionsPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private void SavePreferences()
    {
        try
        {
            ProfileStore.WriteLibrary(LibraryPath, library);
            persistenceError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            persistenceError = "InputForge could not save preferences.";
            if (notice is not null) { notice.Text = persistenceError; notice.Visibility = Visibility.Visible; }
        }
    }

    private RuntimeConfig Config() => new(!rebinding && enabled, !rebinding && testing, options.Capture, library, preferences);

    private void CommitProfileSettings()
    {
        library = library with
        {
            Profiles = library.Profiles.Select(p => p.Id == library.Selected ? p with { Settings = preferences } : p).ToList()
        };
    }

    private void Apply()
    {
        model.TestingInput = testing || rebinding;
        runtime.Configure(Config());
        var track = new Border { Width = 30, Height = 16, CornerRadius = new CornerRadius(8),
            Background = Brush(enabled ? "Accent" : "Disabled"), Padding = new Thickness(2),
            Child = new Ellipse { Width = 12, Height = 12, Fill = Brush("Text"),
                HorizontalAlignment = enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left } };
        Toggle.Content = Row(Text(enabled ? "Mapping on" : "Mapping off"), track);
        System.Windows.Automation.AutomationProperties.SetName(Toggle, enabled ? "Disable mapping" : "Enable mapping");
        Toggle.Background = Brush(enabled ? "Active" : "Elevated");
        if (localTest is not null)
        {
            localTest.Content = testing ? "Stop input test" : "Test inputs";
            localTest.IsEnabled = options.Capture;
        }
        Toggle.IsEnabled = options.Capture;
        if (dashboardToggle is not null)
        {
            dashboardToggle.IsEnabled = options.Capture;
            dashboardToggle.Content = enabled ? "Disable mapping" : "Enable mapping";
        }
        if (localTestLabel is not null) localTestLabel.Text = testing ? "Testing Mode" : "";
        if (!testing) ResetTestCamera();
        updatingProfileSelector = true;
        if (!ReferenceEquals(ProfileSelector.ItemsSource, library.Profiles)) ProfileSelector.ItemsSource = library.Profiles;
        var editing = library.Profiles.Single(p => p.Id == library.Selected);
        ProfileSelector.SelectedItem = editing;
        updatingProfileSelector = false;
        if (dashboardProfile is not null) dashboardProfile.Text = runtime.Read().Profile ?? editing.Name;
        if (dashboardTarget is not null) dashboardTarget.Text = preferences.Application.Length == 0
            ? "No executable associated with this profile" : System.IO.Path.GetFileName(preferences.Application);
        if (dashboardMapping is not null) dashboardMapping.Text = testing ? "Testing Mode" : enabled ? "Enabled" : "Disabled";
        if (controllerMapping is not null) controllerMapping.Text = testing ? "Testing Mode" : enabled ? "Mapping enabled" : "Mapping disabled";
        last = null;
    }

    private void RefreshOutput()
    {
        RefreshSignals();
        UpdatePreviewDiagnostics();
        var current = runtime.Read();
        if (page == "Testing Lab" && testing && options.AutoCamera) UpdateTestCamera(current.State);
        if (current == last) return;
        RecordActivity(current.State, last?.State ?? default);
        last = current;
        if (dashboardProfile is not null) dashboardProfile.Text = current.Profile ?? library.Profiles.Single(p => p.Id == library.Selected).Name;
        ConnectionLabel.Text = current.Connection;
        SidebarConnection.Text = current.Connection;
        ConnectionDot.Fill = Brush(current.Connection == "Connected" ? "Accent" : "Muted");
        if (applicationStatus is not null) applicationStatus.Text = current.Application;
        if (controllerStatus is not null) controllerStatus.Text = current.Connection;
        if (dashboardActivity is not null) dashboardActivity.Text = ActivityText();
        if (labHistory is not null) labHistory.Text = ActivityText();
        labLeft?.Update(current.State.Left);
        labRight?.Update(current.State.Right);
        if (labTriggers is not null) labTriggers.Text = $"LT {current.State.LT:P0}   ·   RT {current.State.RT:P0}";
        if (labInput is not null) labInput.Text = current.Input.Length == 0 ? "No current event" : current.Input;
        if (signals is not null)
            signals.Text = $"Left    {current.State.Left.X,6:F2}  {current.State.Left.Y,6:F2}\n" +
                $"Right   {current.State.Right.X,6:F2}  {current.State.Right.Y,6:F2}\n" +
                $"LT      {current.State.LT:F2}    RT    {current.State.RT:F2}\n" +
                $"Input events   {current.InputEventsPerSecond}/s    Reports sent   {current.ReportsPerSecond}/s\n" +
                $"Latest event   {(current.Input.Length == 0 ? "—" : current.Input)}\n" +
                (current.MeanMappingMicroseconds > 0
                    ? $"Mapping computation   {current.MeanMappingMicroseconds:F1} µs mean · {current.PeakMappingMicroseconds:F1} µs peak"
                    : "Mapping computation   —");
        if (model.IsVisible) model.ShowState(current.State, page == "Controller" ? Selected : null);
        if (mouseOutput is not null)
            mouseOutput.Text = $"Right stick  X {current.State.Right.X:+0.00;-0.00;0.00}  Y {current.State.Right.Y:+0.00;-0.00;0.00}";
        mouseStickPreview?.Update(current.State.Right);
    }

    private void Navigate(string name)
    {
        if (name != "Testing Lab") ResetTestCamera();
        if (testing && name is not ("Dashboard" or "Controller" or "Testing Lab" or "Mouse")) testing = false;
        CancelRebind();
        if (model.Parent is Panel oldParent) oldParent.Children.Remove(model);
        page = name;
        applicationStatus = signals = binding = selection = notice = null;


        selectedBadge = controllerStatus = controllerMapping = null;
        dashboardProfile = dashboardMapping = dashboardActivity = mouseOutput = null;
        dashboardToggle = null;
        mouseStickPreview = null;
        labLeft = labRight = null;
        labTriggers = labInput = labHistory = dashboardTarget = null;
        signalMonitor = null;
        if (name != "Testing Lab") recording = false;
        rebind = null;
        mouseChoices = null;
        localTest = null;
        localTestLabel = null;
        diagnosticsText = null;
        foreach (var (key, item) in nav)
        {
            item.Tag = key == page ? "SelectedNavigation" : null;
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
        var heading = Text(title, 26);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0,0,0,20);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        return layout;
    }

    private Border Surface(UIElement child, double inset = 22) => new()
    {
        Background = Brush("Panel"), Padding = new Thickness(inset),
        CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = Brush("Border"), Child = child
    };

    private Border Scene()
    {
        var scene = new Grid();
        scene.RowDefinitions.Add(new RowDefinition());
        scene.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        scene.Children.Add(model);
        var fullscreen = ButtonFor(viewerExpanded ? "Exit fullscreen" : "Fullscreen", ToggleFullscreen);
        var views = page == "Testing Lab"
            ? Buttons(fullscreen, localTest = ButtonFor("Test inputs", () => { testing = !testing; Apply(); }))
            : Buttons(fullscreen);
        views.HorizontalAlignment = HorizontalAlignment.Right;
        localTestLabel = Text("", 11, "Muted");
        localTestLabel.HorizontalAlignment = HorizontalAlignment.Center;
        var bottom = new StackPanel();
        bottom.Children.Add(views);
        bottom.Children.Add(localTestLabel);
        if (options.PreviewDiagnostics)
        {
            diagnosticsText = Text("", 12, "Muted");
            diagnosticsText.HorizontalAlignment = HorizontalAlignment.Center;
            bottom.Children.Add(diagnosticsText);
        }
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

    private GridLength inspectorWidth = new(430);

    private DockPanel Dashboard()
    {
        var frame = Frame("Dashboard");
        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0,0,0,18) };
        cards.Children.Add(DashboardTile("Controller", controllerStatus = Text("",14,"Muted")));
        cards.Children.Add(DashboardTile("Mapping", dashboardMapping = Text("",14,"Muted")));
        cards.Children.Add(DashboardTile("Active profile", dashboardProfile = Text("",14,"Muted")));
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dashboardToggle = ButtonFor("Enable mapping", () => { enabled = !enabled; Apply(); });
        dashboardToggle.Background = Brush("Accent");
        var shortcuts = Buttons(ButtonFor("Controller", () => Navigate("Controller")),
            ButtonFor("Mouse", () => Navigate("Mouse")), ButtonFor("Profiles", () => Navigate("Profiles")));
        shortcuts.Margin = new Thickness(0,5,0,0);
        shortcuts.Children.Add(ButtonFor("Testing Lab", () => Navigate("Testing Lab")));
        dashboardToggle.HorizontalAlignment = HorizontalAlignment.Left;
        var main = Column(Surface(Column(Section("Controller mapping"), dashboardTarget = Text("", 14, "Muted"),
                Text("Output is limited to the associated foreground application. Esc disables mapping in InputForge.", 13, "Muted"),
                dashboardToggle),24),
            Surface(Column(Section("Quick access"), shortcuts),24));
        main.Margin = new Thickness(0,0,18,0);
        layout.Children.Add(main);
        var activity = Surface(Column(Section("Recent input activity"),
            dashboardActivity = Text("No mapped input observed",14,"Muted")),24);
        activity.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(activity,1);
        layout.Children.Add(activity);
        var footer = Surface(StatusCell("Active application", applicationStatus = Text("Inactive",14,"Muted")),17);
        var content = new StackPanel { Children = { cards, layout, footer } };
        frame.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return frame;
    }

    private Border DashboardTile(string title, TextBlock value)
    {
        var card = Surface(Column(Section(title), value),22);
        card.Margin = new Thickness(0,0,10,0);
        return card;
    }

    private Button? dashboardToggle;

    private TextBlock? dashboardProfile, dashboardMapping, dashboardActivity, mouseOutput;
    private TextBlock? dashboardTarget;
    private readonly Queue<string> activity = new(6);
    private static readonly Control[] observedControls = Enum.GetValues<Control>().Where(id => (int)id <= (int)Control.RightClick).ToArray();
    private string ActivityText() => activity.Count == 0 ? "No mapped input observed" : string.Join("\n", activity.Reverse());
    private void RecordActivity(ControllerState state, ControllerState previous)
    {
        uint press = state.Buttons & ~previous.Buttons;
        if (press == 0 && state.Left == previous.Left && state.Right == previous.Right) return;
        foreach (var id in observedControls)
            if ((press & (1u << (int)id)) != 0) Add($"{DateTime.Now:HH:mm:ss}  ·  {Display(id)}");
        if (state.Left.LengthSquared() > .04f && previous.Left.LengthSquared() <= .04f) Add($"{DateTime.Now:HH:mm:ss}  ·  Left stick");
        if (state.Right.LengthSquared() > .04f && previous.Right.LengthSquared() <= .04f) Add($"{DateTime.Now:HH:mm:ss}  ·  Right stick");
        void Add(string value) { if (activity.Count == 6) activity.Dequeue(); activity.Enqueue(value); }
    }
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 235 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        var inspector = new ColumnDefinition { Width = inspectorWidth, MinWidth = 280 };
        grid.ColumnDefinitions.Add(inspector);
        grid.SizeChanged += (_, _) =>
        {
            inspector.MaxWidth = Math.Max(280, grid.ActualWidth - 247);
            inspector.Width = inspectorWidth;
        };

        var viewer = Frame(title);
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
            Buttons(ButtonFor("Left mouse", () => BindMouse(256)), ButtonFor("Right mouse", () => BindMouse(257)),
                ButtonFor("Middle", () => BindMouse(258)), ButtonFor("Mouse 4", () => BindMouse(259)), ButtonFor("Mouse 5", () => BindMouse(260))));
        mouseChoices.Visibility = Visibility.Collapsed;
        notice = Text(persistenceError ?? "", 11, "Muted");
        notice.Visibility = string.IsNullOrEmpty(notice.Text) ? Visibility.Collapsed : Visibility.Visible;
        var card = Surface(Column(Section("Selected button"), heading, Text("Current binding", 13, "Muted"), currentBinding,
            Buttons(rebind, clear, reset),
            ButtonFor("Choose control…", () => ChooseControl()), mouseChoices, notice), 22);


        var views = Buttons(ButtonFor("Front view", model.Front), ButtonFor("Rear view", model.Rear),
            ButtonFor("Reset view", model.Reset));
        var inspectorBody = new StackPanel();
        foreach (var section in new[] {
            Surface(Column(Section("Controller status"), Text("Virtual Xbox 360 controller", 14),
                controllerStatus = Text(runtime.Read().Connection, 13, "Muted"),
                controllerMapping = Text("", 13, "Muted")), 22),
            card,
            Surface(Column(Section("View controls"), views), 22) })
        {
            section.Margin = new Thickness(0, 0, 0, 18);
            inspectorBody.Children.Add(section);
        }
        var scroll = new ScrollViewer { Content = inspectorBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);
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
        if (selectedBadge is not null) selectedBadge.Text = Glyph(id);
        if (selection is not null) selection.Text = Display(id);
        string current = DisplayInput(preferences.Bindings[id]);
        if (binding is not null) binding.Text = current;

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

    private void ChooseControl()
    {
        var picker = new ListBox { ItemsSource = Enum.GetValues<Control>().Select(Display).ToArray(),
            SelectedIndex = (int)Selected, MinHeight = 170, MaxHeight = 260 };
        if (ShowDarkDialog("Choose controller input", picker, "Select") && picker.SelectedIndex >= 0)
            bindingSelection.Select((Control)picker.SelectedIndex);
    }

    private static TextBlock Section(string title)
    {
        var label = Text(title, 17);
        label.FontWeight = FontWeights.SemiBold;
        return label;
    }

    private bool ShowDarkDialog(string title, UIElement body, string acceptLabel, Func<bool>? canAccept = null)
    {
        var dialog = new Window
        {
            Owner = this, Title = title, Width = 390, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = Brush("Panel"), Foreground = Brush("Text"), FontSize = 14
        };
        var accept = ButtonFor(acceptLabel, () =>
        {
            if (canAccept?.Invoke() != false) dialog.DialogResult = true;
        });
        accept.IsDefault = true;
        accept.Background = Brush("Accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var heading = Section(title);
        heading.MouseLeftButtonDown += (_, _) => dialog.DragMove();
        dialog.Content = Surface(Column(heading, body, Buttons(accept, cancel)), 24);
        return dialog.ShowDialog() == true;
    }

    private static WrapPanel Buttons(params Button[] buttons)
    {
        var panel = new WrapPanel();
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, 7, 6);
            panel.Children.Add(button);
        }
        return panel;
    }

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
        CommitProfileSettings();
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
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && !rebinding)
        {
            e.Handled = true;
            ShowCommandPalette();
            return;
        }
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

    private void ShowCommandPalette()
    {
        var query = new TextBox();
        var results = new ListBox { Height = 220, DisplayMemberPath = "Label" };
        var commands = nav.Keys.Select(name => new PaletteCommand(name, () => Navigate(name)))
            .Concat(library.Profiles.Select(profile => new PaletteCommand("Profile · " + profile.Name, () => SelectProfile(profile))))
            .Append(new PaletteCommand("Input diagnostics", ShowConfigurationDiagnostics))
            .Append(new PaletteCommand("Mouse calibration", CalibrateMouse)).ToArray();
        void Filter()
        {
            results.ItemsSource = commands.Where(command => command.Label.Contains(query.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            results.SelectedIndex = results.Items.Count > 0 ? 0 : -1;
        }
        query.TextChanged += (_, _) => Filter();
        query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Down || results.SelectedIndex < 0) return;
            (results.ItemContainerGenerator.ContainerFromIndex(results.SelectedIndex) as ListBoxItem)?.Focus();
            e.Handled = true;
        };
        var body = Column(query, results);
        body.Loaded += (_, _) => query.Focus();
        Filter();
        if (ShowDarkDialog("Commands", body, "Open") && results.SelectedItem is PaletteCommand command) command.Run();
    }

    private sealed record PaletteCommand(string Label, Action Run);

    private void ShowConfigurationDiagnostics()
    {
        var findings = new List<string>();
        if (!options.Capture) findings.Add("Scoped input capture is disabled in Settings.");
        if (preferences.Application.Length == 0) findings.Add("Associate an executable in Profiles to enable virtual output.");
        else if (!File.Exists(preferences.Application)) findings.Add("The associated executable no longer exists at its saved path.");
        if (runtime.Read().Connection == "ViGEmBus unavailable") findings.Add("ViGEmBus connection failed during a virtual output attempt.");
        foreach (var shared in preferences.Bindings.Where(pair => pair.Value != 0).GroupBy(pair => pair.Value).Where(group => group.Count() > 1))
            findings.Add($"{DisplayInput(shared.Key)} is shared by {string.Join(", ", shared.Select(pair => Display(pair.Key)))}.");
        if (observedSamples >= 30 && saturatedSamples > observedSamples / 3)
            findings.Add($"{saturatedSamples} of {observedSamples} moving samples reached near-full stick output. Review sensitivity or raise the saturation threshold.");
        if (peakRaw > 0) findings.Add($"Largest observed local mouse sample: {peakRaw:F0} counts.");
        var message = Text(findings.Count == 0 ? "No issue found in the available observations." : string.Join("\n\n", findings), 13);
        ShowDarkDialog("Configuration diagnostics", new ScrollViewer { Content = message, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, "Done");
    }

    private void CompareProfiles()
    {
        var left = new ComboBox { ItemsSource = library.Profiles, DisplayMemberPath = "Name", SelectedItem = library.Profiles.Single(p => p.Id == library.Selected) };
        var right = new ComboBox { ItemsSource = library.Profiles, DisplayMemberPath = "Name", SelectedIndex = library.Profiles.Count - 1 };
        var differences = Text("", 13);
        var apply = new ComboBox { ItemsSource = new[] { "Keep current", "Use first profile", "Use second profile" }, SelectedIndex = 0 };
        void Compare()
        {
            if (left.SelectedItem is not NamedProfile first || right.SelectedItem is not NamedProfile second) return;
            MouseSettings a = first.Settings.Mouse, b = second.Settings.Mouse;
            differences.Text = $"Horizontal   {a.Horizontal:F2} → {b.Horizontal:F2}\nVertical   {a.Vertical:F2} → {b.Vertical:F2}\n" +
                $"Deadzone   {a.Deadzone:P0} → {b.Deadzone:P0}\nSmoothing   {a.Filter} → {b.Filter}\n" +
                $"Acceleration   {a.Acceleration:F2} → {b.Acceleration:F2}\nCurve   {a.Curve} → {b.Curve}\n" +
                $"Saturation threshold   {a.Saturation:P0} → {b.Saturation:P0}\nMaximum mouse output   {a.MaxOutput:P0} → {b.MaxOutput:P0}";
        }
        left.SelectionChanged += (_, _) => Compare();
        right.SelectionChanged += (_, _) => Compare();
        Compare();
        if (!ShowDarkDialog("Compare profiles", Column(left, right, differences, apply), "Confirm")) return;
        if (apply.SelectedIndex == 1 && left.SelectedItem is NamedProfile p) SelectProfile(p);
        if (apply.SelectedIndex == 2 && right.SelectedItem is NamedProfile q) SelectProfile(q);
    }

    private DockPanel MousePage()
    {
        var frame = Frame("Mouse");
        var tools = Buttons(ButtonFor("Calibrate…", CalibrateMouse), ButtonFor("Diagnostics…", ShowConfigurationDiagnostics));
        DockPanel.SetDock(tools, Dock.Top);
        frame.Children.Add(tools);
        var current = preferences.Mouse;
        var editor = Surface(Column(Section("Sensitivity"),
            SliderFor("Horizontal sensitivity", .1, 4, current.Horizontal, value => ChangeMouse(preferences.Mouse with { Horizontal = value })),
            SliderFor("Vertical sensitivity", .1, 4, current.Vertical, value => ChangeMouse(preferences.Mouse with { Vertical = value })),
            Section("Motion shaping"), Combo("Smoothing", Enum.GetValues<Smoothing>(), current.Filter, value =>
                ChangeMouse(preferences.Mouse with { Filter = value })),
            SliderFor("Acceleration", 0, 3, current.Acceleration, value => ChangeMouse(preferences.Mouse with { Acceleration = value })),
            Section("Deadzones"),
            SliderFor("Input deadzone", 0, .35, current.Deadzone, value => ChangeMouse(preferences.Mouse with { Deadzone = value })),
            SliderFor("Anti-deadzone", 0, .35, current.AntiDeadzone, value => ChangeMouse(preferences.Mouse with { AntiDeadzone = value })),
            Section("Output limits"),
            SliderFor("Saturation threshold", .5, 1, current.Saturation, value => ChangeMouse(preferences.Mouse with { Saturation = value })),
            SliderFor("Maximum mouse output", .1, 1, current.MaxOutput, value => ChangeMouse(preferences.Mouse with { MaxOutput = value }))),24);
        var input = new TextBox { Width = 65 };
        var outputField = new TextBox { Width = 65 };
        System.Windows.Automation.AutomationProperties.SetName(input, "Point input coordinate");
        System.Windows.Automation.AutomationProperties.SetName(outputField, "Point output coordinate");
        var pointLabel = Text("Point 1", 13, "Muted");
        var mode = new ComboBox { ItemsSource = Enum.GetValues<ResponseCurve>(), SelectedItem = current.Curve, MinWidth = 124 };
        mode.ItemTemplate = CurveNames.Template;
        var chart = new CurveView(current) { Height = 220, Focusable = true };
        chart.Changed += value =>
        {
            ChangeMouse(preferences.Mouse with { Curve = value.Curve, Point1 = value.Point1, Point2 = value.Point2,
                Input1 = value.Input1, Input2 = value.Input2, InterpolatedKnots = value.InterpolatedKnots });
            mode.SelectedItem = ResponseCurve.Custom;
            FillCoordinates(value);
        };
        void FillCoordinates(MouseSettings value)
        {
            var custom = CurveKnots.AsCustom(value);
            input.Text = (chart.Selected == 0 ? custom.Input1 : custom.Input2).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            outputField.Text = (chart.Selected == 0 ? custom.Point1 : custom.Point2).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            pointLabel.Text = $"Point {chart.Selected + 1}";
        }
        void ChangeCurve(ResponseCurve value)
        {
            MouseSettings next = value == ResponseCurve.Custom ? CurveKnots.AsCustom(preferences.Mouse) : preferences.Mouse with { Curve = value };
            if (next != preferences.Mouse) ChangeMouse(next);
            chart.UpdateSettings(next);
            FillCoordinates(next);
        }
        mode.SelectionChanged += (_, _) => { if (mode.SelectedItem is ResponseCurve value) ChangeCurve(value); };
        chart.PointSelected += _ => FillCoordinates(chart.Settings);
        FillCoordinates(current);
        var applyPoint = ButtonFor("Apply", () =>
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (float.TryParse(input.Text, System.Globalization.NumberStyles.Float, culture, out float x) &&
                float.TryParse(outputField.Text, System.Globalization.NumberStyles.Float, culture, out float y))
                chart.MoveSelected(x, y);
            FillCoordinates(chart.Settings);
        });
        var coordinates = new WrapPanel
        {
            Children = { ButtonFor("Point 1", () => chart.Select(0)), ButtonFor("Point 2", () => chart.Select(1)),
                Row(Text("X"), input), Row(Text("Y"), outputField), applyPoint,
                ButtonFor("Reset", () =>
                {
                    var reset = new MouseSettings();
                    ChangeMouse(preferences.Mouse with { Curve = reset.Curve, Point1 = reset.Point1, Point2 = reset.Point2,
                        Input1 = reset.Input1, Input2 = reset.Input2, InterpolatedKnots = false });
                    mode.SelectedItem = ResponseCurve.Linear;
                    chart.UpdateSettings(preferences.Mouse);
                    FillCoordinates(preferences.Mouse);
                }) }
        };
        foreach (FrameworkElement child in coordinates.Children) child.Margin = new Thickness(0,0,6,6);
        var curveHeader = new DockPanel { Children = { mode, Section("Response curve") } };
        DockPanel.SetDock(mode, Dock.Right);
        var preview = new StickPreview { Width = 118, Height = 118 };
        mouseStickPreview = preview;
        var output = Row(preview, mouseOutput = Text("", 13, "Muted"));
        var right = Column(Surface(Column(curveHeader, chart, coordinates,
                Text("X: normalized input · Y: stick response before smoothing", 12, "Muted")),22),
            Surface(Column(Section("Live preview"), output, MouseTestButton()),22));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        editor.Margin = new Thickness(0,0,18,0);
        grid.Children.Add(editor);
        Grid.SetColumn(right,1);
        grid.Children.Add(right);
        frame.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return frame;
    }

    private StickPreview? mouseStickPreview;
    private SignalMonitor? signalMonitor;
    private const int MaxRecording = 1000;
    private readonly List<MouseSample> recordingSamples = new(MaxRecording);
    private bool recording;
    private Button? recordButton;
    private int saturatedSamples, observedSamples;
    private float peakRaw;

    private void RefreshSignals()
    {
        bool dirty = false;
        while (runtime.ReadSample(out var sample))
        {
            if (!testing) continue;
            signalMonitor?.Append(sample);
            UpdateCalibration(sample);
            dirty = true;
            if (sample.Raw != default)
            {
                observedSamples++;
                peakRaw = Math.Max(peakRaw, sample.Raw.Length());
                if (sample.Output.LengthSquared() >= .99f) saturatedSamples++;
            }
            if (!recording || page != "Testing Lab") continue;
            recordingSamples.Add(sample);
            if (recordingSamples.Count == MaxRecording) StopRecording();
        }
        if (!testing && recording) StopRecording();
        if (dirty) signalMonitor?.InvalidateVisual();
        if (dirty) calibrationMonitor?.InvalidateVisual();
    }

    private void StopRecording()
    {
        recording = false;
        if (recordButton is not null) recordButton.Content = "Record motion";
    }

    private void ShowReplay()
    {
        if (recordingSamples.Count == 0)
        {
            ShowDarkDialog("Local replay", Text("Record mouse movement in Testing Lab first."), "OK");
            return;
        }
        var baseline = ReplayStats.Measure(recordingSamples, preferences.Mouse);
        var choices = new ComboBox { ItemsSource = library.Profiles, DisplayMemberPath = "Name", SelectedIndex = 0 };
        var result = Text("");
        void Format()
        {
            if (choices.SelectedItem is not NamedProfile second) return;
            var compared = ReplayStats.Measure(recordingSamples, second.Settings.Mouse);
            result.Text = $"Edited profile: peak {baseline.Peak:P0} · mean {baseline.Mean:P0} · {baseline.Saturated} saturated\n" +
                $"{second.Name}: peak {compared.Peak:P0} · mean {compared.Mean:P0} · {compared.Saturated} saturated";
        }
        choices.SelectionChanged += (_, _) => Format();
        Format();
        ShowDarkDialog("Local replay", Column(Text($"{recordingSamples.Count} mouse samples · processed locally", 13, "Muted"), choices, result), "Done");
    }

    private void ChangeMouse(MouseSettings mouse)
    {
        preferences = preferences with { Mouse = mouse };
        CommitProfileSettings();
        Apply();
        delayedSave.Stop();
        delayedSave.Start();
    }

    private readonly DispatcherTimer delayedSave = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private Button MouseTestButton()
    {
        var button = ButtonFor(testing ? "Stop input test" : "Test inputs", () =>
        {
            testing = !testing;
            Apply();
        });
        localTest = button;
        return button;
    }

    private DockPanel Profiles()
    {
        var frame = Frame("Profiles");
        var active = library.Profiles.Single(p => p.Id == library.Selected);
        var list = new ListBox { Background = Brush("Panel"), Foreground = Brush("Text"),
            BorderThickness = new Thickness(0), DisplayMemberPath = nameof(NamedProfile.Name), ItemsSource = library.Profiles,
            ItemContainerStyle = (Style)FindResource("BindingRow"), SelectedItem = active, MinWidth = 150 };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is NamedProfile profile) SelectProfile(profile);
        };
        var name = new TextBox { Text = active.Name, MaxLength = 64, Background = Brush("Elevated"),
            Foreground = Brush("Text"), BorderBrush = Brush("Border"), Padding = new Thickness(8) };
        System.Windows.Automation.AutomationProperties.SetName(name, "Profile name");
        var path = Text(preferences.Application.Length == 0 ? "No executable associated" : preferences.Application, 13, "Muted");
        var browse = ButtonFor("Choose executable", () =>
        {
            var picker = new OpenFileDialog { Filter = "Windows applications (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            if (library.ForApplication(picker.FileName) is { } conflict && conflict.Id != active.Id)
            {
                ProfileError($"Already associated with {conflict.Name}.");
                return;
            }
            preferences = preferences with { Application = picker.FileName };
            CommitProfileSettings();
            UpdateProfiles(library);
        });
        var actions = new WrapPanel();
        actions.Children.Add(ButtonFor("Create from preset", () =>
        {
            var presets = new ComboBox { ItemsSource = Enum.GetValues<InputPreset>(), SelectedItem = InputPreset.Default };
            if (ShowDarkDialog("Create profile", presets, "Create") && presets.SelectedItem is InputPreset preset)
                AddProfile(preset.ToString(), Preferences.Create() with { Mouse = InputPresets.Create(preset) }, preset);
        }));
        actions.Children.Add(ButtonFor("Duplicate", () => AddProfile((active.Name.Length > 59 ? active.Name[..59] : active.Name) + " copy",
            preferences with { Application = "", Bindings = new(preferences.Bindings) }, active.Preset)));
        actions.Children.Add(ButtonFor("Reset response", () =>
        {
            if (!ShowDarkDialog("Reset mouse response", Text($"Restore the {active.Preset} starting settings?"), "Reset")) return;
            preferences = preferences with { Mouse = InputPresets.Create(active.Preset) };
            CommitProfileSettings();
            UpdateProfiles(library);
        }));
        actions.Children.Add(ButtonFor("Delete", () =>
        {
            if (library.Profiles.Count == 1) { ProfileError("Keep at least one profile."); return; }
            if (!ShowDarkDialog("Delete profile", Text($"Delete {active.Name}?"), "Delete"))
                return;
            var remaining = library.Profiles.Where(p => p.Id != active.Id).ToList();
            UpdateProfiles(new(remaining[0].Id, remaining));
        }));
        actions.Children.Add(ButtonFor("Import", ImportProfile));
        actions.Children.Add(ButtonFor("Export", ExportProfile));
        actions.Children.Add(ButtonFor("Compare", CompareProfiles));
        foreach (FrameworkElement item in actions.Children) item.Margin = new Thickness(0,0,7,7);

        var editor = Surface(Column(Section("Profile details"), Text("Name", 13, "Muted"), name, ButtonFor("Rename", () =>
        {
            string next = name.Text.Trim();
            if (next.Length == 0) { ProfileError("Enter a profile name."); return; }
            UpdateProfiles(library with { Profiles = library.Profiles.Select(p => p.Id == active.Id ? p with { Name = next } : p).ToList() });
        }), Text("Application association", 16), path,
            Row(browse, ButtonFor("Clear", () =>
            {
                preferences = preferences with { Application = "" };
                CommitProfileSettings();
                UpdateProfiles(library);
            })),
            Text("Mapping switches profiles when an associated executable gains focus.", 12, "Muted"),
            Section("Input response"), Text($"{active.Preset} starting point · {preferences.Mouse.Filter} smoothing · {preferences.Mouse.Deadzone:P0} deadzone", 13, "Muted"),
            new CurveView(preferences.Mouse) { Height = 145 },
            notice = Text(persistenceError ?? "", 12, "Muted")), 18);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var sidebar = new DockPanel { Margin = new Thickness(0,0,18,0) };
        sidebar.Children.Add(Surface(list, 6));
        grid.Children.Add(sidebar);
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        actions.Margin = new Thickness(0,18,0,0);
        DockPanel.SetDock(actions, Dock.Bottom);
        frame.Children.Add(actions);
        frame.Children.Add(grid);
        return frame;
    }

    private void ProfileError(string message)
    {
        if (notice is null) return;
        notice.Text = message;
        notice.Visibility = Visibility.Visible;
    }

    private void SelectProfile(NamedProfile profile)
    {
        if (profile.Id != library.Selected) UpdateProfiles(library with { Selected = profile.Id });
    }

    private void UpdateProfiles(ProfileLibrary value)
    {
        library = value;
        preferences = value.Profiles.Single(p => p.Id == value.Selected).Settings;
        SavePreferences();
        Navigate(page);
    }

    private void AddProfile(string name, Preferences settings, InputPreset preset = InputPreset.Custom)
    {
        var profile = new NamedProfile(Guid.NewGuid(), name, settings, preset);
        UpdateProfiles(new(profile.Id, [..library.Profiles, profile]));
    }

    private void ImportProfile()
    {
        var dialog = new OpenFileDialog { Filter = "InputForge profile (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = ProfileStore.Read(dialog.FileName);
            if (library.ForApplication(imported.Application) is not null)
            {
                ProfileError("That executable already has a profile. Clear its association before importing.");
                return;
            }
            string name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName).Trim();
            AddProfile(name.Length == 0 ? "Imported profile" : name[..Math.Min(64,name.Length)], imported);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ProfileError("The profile could not be imported.");
        }
    }

    private void ExportProfile()
    {
        var dialog = new SaveFileDialog { Filter = "InputForge profile (*.json)|*.json", AddExtension = true, DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return;
        if (string.Equals(dialog.FileName, LibraryPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dialog.FileName, OptionsPath, StringComparison.OrdinalIgnoreCase))
        {
            ProfileError("Choose a separate file for the export.");
            return;
        }
        try { ProfileStore.Write(dialog.FileName, preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ProfileError("The profile could not be exported."); }
    }

    private readonly TestCamera testCamera = new();
    private OrbitPose? cameraBeforeShoulder;
    private StickPreview? labLeft, labRight;
    private TextBlock? labTriggers, labInput, labHistory;

    private static double ClockSeconds() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    private void ResetTestCamera()
    {
        testCamera.Reset();
        if (cameraBeforeShoulder is { } previous) model.RestoreView(previous);
        cameraBeforeShoulder = null;
    }

    private void UpdateTestCamera(ControllerState state)
    {
        var change = testCamera.Update(state, ClockSeconds(), Math.Clamp(options.CameraIdle, 1, 5));
        switch (change)
        {
            case CameraView.Shoulder:
                cameraBeforeShoulder ??= model.CameraPose;
                model.Shoulder();
                break;
            case CameraView.Front:
                model.Front();
                cameraBeforeShoulder = null;
                break;
            case CameraView.Manual:
                if (cameraBeforeShoulder is { } previous) model.RestoreView(previous);
                cameraBeforeShoulder = null;
                break;
        }
    }

    private DockPanel TestingLab()
    {
        var frame = Frame("Testing Lab");
        if (viewerExpanded) { frame.Children.Add(Scene()); return frame; }
        var auto = new CheckBox { Content = "Auto Camera", IsChecked = options.AutoCamera };
        auto.Click += (_, _) =>
        {
            if (auto.IsChecked != true) ResetTestCamera();
            SetInterfaceOptions(options with { AutoCamera = auto.IsChecked == true });
        };
        var timeout = Combo("Return delay in seconds", new[] { 1, 2, 3, 5 }, options.CameraIdle,
            seconds => SetInterfaceOptions(options with { CameraIdle = seconds }));
        var emergency = ButtonFor("Emergency disable", () => { enabled = testing = false; Apply(); });
        emergency.Background = Brush("Accent");
        var toolbar = new WrapPanel { Children = { emergency, auto, Row(Text("Return delay", 13, "Muted"), timeout) } };
        foreach (FrameworkElement item in toolbar.Children) item.Margin = new Thickness(0,0,12,8);
        DockPanel.SetDock(toolbar, Dock.Top);
        frame.Children.Add(toolbar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 238 });
        var scene = Scene();
        scene.Margin = new Thickness(0,0,18,0);
        grid.Children.Add(scene);
        labLeft = new StickPreview { Width = 73, Height = 73 };
        labRight = new StickPreview { Width = 73, Height = 73 };
        var axes = new UniformGrid { Columns = 2, Children = { Column(Text("Left",13), labLeft), Column(Text("Right",13), labRight) } };
        var data = Surface(Column(Section("Controller output"), axes,
            labTriggers = Text("", 14), Section("Current input"), labInput = Text("", 13, "Muted"),
            Section("Recent activity"), labHistory = Text("", 13, "Muted"),
            Section("Input signals"),
            signalMonitor = new SignalMonitor { Height = 100 },
            Text("Grey: raw magnitude (relative) · white: target · red: output", 12, "Muted"),
            Section("Local mouse replay"),
            Buttons(recordButton = ButtonFor("Record motion", () =>
            {
                if (recording) { StopRecording(); return; }
                if (!testing)
                {
                    ShowDarkDialog("Local replay", Text("Start local testing before recording mouse movement."), "OK");
                    return;
                }
                recordingSamples.Clear();
                recording = true;
                recordButton!.Content = "Stop recording";
            }), ButtonFor("Replay", ShowReplay), ButtonFor("Clear", () => { StopRecording(); recordingSamples.Clear(); })),
            new Expander { Header = "Input diagnostics", Foreground = Brush("Text"),
                Content = signals = Text("", 12, "Muted") }), 18);
        data.VerticalAlignment = VerticalAlignment.Top;
        var scroll = new ScrollViewer { Content = data, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll,1);
        grid.Children.Add(scroll);
        frame.Children.Add(grid);
        return frame;
    }

    private DockPanel Settings()
    {
        var frame = Frame("Settings");
        var motion = new CheckBox { Content = "Reduce camera animation", Foreground = Brush("Text"), IsChecked = options.ReduceMotion };
        var diagnostics = new CheckBox { Content = "Show developer preview diagnostics", Foreground = Brush("Text"),
            IsChecked = options.PreviewDiagnostics };
        diagnostics.Click += (_, _) =>
        {
            model.MeasureFrames = diagnostics.IsChecked == true;
            SetInterfaceOptions(options with { PreviewDiagnostics = model.MeasureFrames });
        };
        motion.Click += (_, _) =>
        {
            model.ReduceMotion = motion.IsChecked == true;
            SetInterfaceOptions(options with { ReduceMotion = model.ReduceMotion });
        };
        var capture = new CheckBox { Content = "Allow scoped keyboard and mouse capture", Foreground = Brush("Text"), IsChecked = options.Capture };
        capture.Click += (_, _) =>
        {
            if (capture.IsChecked != true) enabled = testing = false;
            SetInterfaceOptions(options with { Capture = capture.IsChecked == true });
        };
        var startup = new ComboBox { ItemsSource = nav.Keys.ToArray(), SelectedItem = options.StartPage, MinWidth = 130 };
        System.Windows.Automation.AutomationProperties.SetName(startup, "Page to open on launch");
        startup.SelectionChanged += (_, _) =>
        {
            if (startup.SelectedItem is string name) SetInterfaceOptions(options with { StartPage = name });
        };
        CheckBox Option(string name, bool initial, Action<bool> change)
        {
            var value = new CheckBox { Content = name, IsChecked = initial };
            value.Click += (_, _) => change(value.IsChecked == true);
            return value;
        }
        var general = Surface(Column(Section("General"), Row(Text("Open on launch"), startup),
            Option("Start minimised", options.StartMinimized, value => SetInterfaceOptions(options with { StartMinimized = value })),
            Option("Remember window position", options.RememberWindow, value => SetInterfaceOptions(options with { RememberWindow = value })),
            Option("Confirm exit while mapping is armed", options.ConfirmExit, value => SetInterfaceOptions(options with { ConfirmExit = value }))), 22);
        var appearance = Surface(Column(Section("Appearance and performance"), motion, diagnostics,
            Text("Preview pauses while minimised. Frame timing is available in developer diagnostics.", 13, "Muted")),22);
        var safety = Surface(Column(Section("Input and safety"), capture,
            Text("Input always passes through. Virtual output releases when the associated application loses focus. Esc disables mapping in this window.", 13, "Muted")),22);
        var backend = Surface(Column(Section("Controller"), controllerStatus = Text(runtime.Read().Connection, 14),
            Text("ViGEmBus · Xbox 360 output", 13, "Muted"), ButtonFor("Open Testing Lab", () => Navigate("Testing Lab"))),22);
        var data = Surface(Column(Section("Profiles and data"), Buttons(ButtonFor("Manage profiles", () => Navigate("Profiles")),
            ButtonFor("Export profile library", () => TransferLibrary(false)),
            ButtonFor("Import profile library", () => TransferLibrary(true)),
            ButtonFor("Reset application preferences", () =>
            {
                if (!ShowDarkDialog("Reset application preferences", Text("Restore interface and capture defaults?"), "Reset")) return;
                enabled = testing = false;
                model.ReduceMotion = model.MeasureFrames = false;
                SetInterfaceOptions(new());
                Navigate("Settings");
            })), notice = Text(persistenceError ?? "", 13, "Muted")),22);
        var about = Surface(Column(Section("About"),
            Text("InputForge " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? ""), 14),
            Text("GPL-3.0-or-later · Xbox Elite Controller model by Rusfort (CC BY 4.0)", 13, "Muted"),
            Text("github.com/bIoodling/InputForge", 13, "Muted")),22);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = Column(general, appearance, safety);
        left.Margin = new Thickness(0,0,18,0);
        var right = Column(backend, data, about);
        Grid.SetColumn(right,1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        frame.Children.Add(new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return frame;
    }

    private void TransferLibrary(bool import)
    {
        FileDialog picker = import ? new OpenFileDialog { CheckFileExists = true } : new SaveFileDialog { AddExtension = true };
        picker.Filter = "InputForge profile library (*.json)|*.json";
        if (picker.ShowDialog(this) != true) return;
        if (!import && (System.IO.Path.GetDirectoryName(picker.FileName)?.Equals(System.IO.Path.GetDirectoryName(LibraryPath), StringComparison.OrdinalIgnoreCase) ?? false))
        {
            ProfileError("Choose a location outside the configuration directory.");
            return;
        }
        try
        {
            if (!import) { ProfileStore.WriteLibrary(picker.FileName, library); ProfileError("Profile library exported."); return; }
            var next = ProfileStore.ReadLibrary(picker.FileName);
            if (!ShowDarkDialog("Replace profile library", Text("Replace the current library with the imported profiles?"), "Replace")) return;
            enabled = testing = false;
            UpdateProfiles(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ProfileError(import ? "The profile library could not be imported." : "The profile library could not be exported.");
        }
    }

    private readonly System.Diagnostics.Process currentProcess = System.Diagnostics.Process.GetCurrentProcess();
    private TextBlock? diagnosticsText;
    private TimeSpan lastCpu;
    private long diagnosticsAt;

    private void UpdatePreviewDiagnostics()
    {
        if (diagnosticsText is null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(diagnosticsAt, now).TotalSeconds;
        if (elapsed < 1) return;
        currentProcess.Refresh();
        TimeSpan cpu = currentProcess.TotalProcessorTime;
        string load = diagnosticsAt == 0 ? "—" :
            $"{100 * (cpu-lastCpu).TotalSeconds / (elapsed*Environment.ProcessorCount):F1}%";
        lastCpu = cpu;
        diagnosticsAt = now;
        diagnosticsText.Text = $"{model.ReadFrameStats()}  |  CPU {load}  ·  RAM {currentProcess.WorkingSet64 / 1048576:F0} MiB  ·  Rendering tier {RenderCapability.Tier >> 16}";
    }

    private void SetInterfaceOptions(InterfaceOptions value)
    {
        options = value;
        Apply();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OptionsPath)!);
            File.WriteAllText(OptionsPath + ".tmp", JsonSerializer.Serialize(value));
            File.Move(OptionsPath + ".tmp", OptionsPath, true);
            ProfileError("");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProfileError("Interface settings could not be saved.");
        }
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
        var number = new TextBox { Text = value.ToString("0.###"), Width = 78, Margin = new Thickness(12, 0, 0, 0) };
        var caption = new DockPanel();
        DockPanel.SetDock(number,Dock.Right);
        caption.Children.Add(number);
        caption.Children.Add(Text(label,14));
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = .01, LargeChange = .1 };
        System.Windows.Automation.AutomationProperties.SetName(slider,label);
        slider.ValueChanged += (_, _) =>
        {
            number.Text = slider.Value.ToString("0.###");
            changed((float)slider.Value);
        };
        void Commit()
        {
            if (double.TryParse(number.Text, out double parsed) && double.IsFinite(parsed))
                slider.Value = Math.Clamp(parsed, min, max);
            number.Text = slider.Value.ToString("0.###");
        }
        number.LostKeyboardFocus += (_, _) => Commit();
        number.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(number, label + " value");
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (enabled && options.ConfirmExit &&
            !ShowDarkDialog("Exit InputForge", Text("Mapping is armed. Exit and release controller output?"), "Exit"))
        {
            e.Cancel = true;
            return;
        }
        if (options.RememberWindow && !viewerExpanded)
        {
            var bounds = RestoreBounds;
            if (!bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
                SetInterfaceOptions(options with { WindowLeft = bounds.Left, WindowTop = bounds.Top });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        refresh.Stop();
        currentProcess.Dispose();
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
            int caption = 0x00100D0D, text = 0x00F7F5F5;
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
        WindowChrome.SetIsHitTestVisibleInChrome(ProfileSelector, true);
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
