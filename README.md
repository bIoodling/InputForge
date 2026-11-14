# InputForge

Precision Input. Complete Control.

InputForge is a Windows keyboard and mouse to virtual controller mapper in development.
The initial native interface includes an interactive Xbox Elite-inspired 3D controller,
binding selection, mouse tuning, an executable allowlist and a local Testing Lab.

## Build and test

Use the .NET 10 SDK on Windows x64.

```powershell
dotnet build InputForge.slnx -c Release
dotnet test InputForge.slnx -c Release --no-build
```

## Try the Windows interface

- In **Mouse**, drag either interior curve point, use the arrow keys with the graph focused,
  or select a point and enter X/Y coordinates. Curve edits are saved to the selected profile.
  The two interpolation points are ordered between fixed endpoints. The graph shows the
  normalized response stage before anti-deadzone and time-based smoothing.
- In **Profiles**, choose **Create from preset** for Default, Precision, Smooth, Responsive,
  Competitive, or Custom. This creates an editable copy. **Reset response** restores its starting
  mouse settings while retaining bindings and the application association.
- In **Testing Lab**, **Auto Camera** reveals trigger and bumper inputs, returns to the previous
  view after the configured quiet period, and yields to manual rotation. Independent rear-paddle
  inputs are unavailable through the Xbox 360 backend.

Mouse motion is scaled by elapsed time and per-axis sensitivity, then shaped by a radial
deadzone, acceleration, saturation, response curve, optional anti-deadzone and smoothing.
Presets are starting points. Games can apply their own deadzones, sensitivity, polling and
response curves, so a profile does not imply any particular aim-assist behaviour. See
[Microsoft's XInput documentation](https://learn.microsoft.com/en-us/windows/win32/xinput/getting-started-with-xinput).

- On Controller, drag the model to orbit, use the wheel to zoom, or click a control to edit its binding.
  Use **Choose control** for keyboard access and individual stick directions. The divider resizes the inspector.
  Double-click the background to reset the view.
- In Testing Lab, select **Test inputs** to observe live mappings without a virtual device.
  Click or move inside the viewport to preview mouse mappings. Stop testing to resume mouse orbit and selection.
  New profiles and individual resets use Left Shift for B, Space for A, right mouse for LT, and
  left mouse for RT. Existing saved assignments are retained.
- On Mouse, use the Sensitivity, Filtering and Response Curve tabs.
- For virtual Xbox 360 output, separately install [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0).
  Select the allowed executable under Profiles, enable mapping, and focus that executable.
  ViGEmBus is a retired driver. InputForge neither distributes nor installs it.

WPF renders Rusfort's supplied Xbox Elite Controller GLB using `Viewport3D`, including WPF's software
rendering fallback. The viewport consumes output snapshots from the Raw Input worker and pauses
UI polling while minimized. Mapping is disabled on startup, and capture is scoped to the chosen
foreground executable or the explicitly enabled local test. The application stores preferences in
`%LOCALAPPDATA%\InputForge` and makes no network requests during normal use.

The asset is © Rusfort under CC BY 4.0. See [asset attribution and rendering notes](src/InputForge.App/Assets/ATTRIBUTION.md).
The loader reassembles the unchanged binary from ordered segments and checks its SHA-256. The model
has no authored animation tracks. Live transforms use its existing geometry. Guide and rear controls
are not exposed as independent bindings by the Xbox 360 backend.

Enable **Show developer preview diagnostics** under Settings to view active camera frame callbacks,
mean and maximum frame intervals, process CPU, and working set. These readings are runtime measurements.
Rendering tier describes WPF acceleration capability, not GPU utilisation.

There are currently no GitHub release binaries. Test the native layout, hit targets and keyboard
navigation at different Windows scaling settings before relying on this interface. Driver
connection, real game output and focus-loss cleanup require a separate manual Windows test.

Successful Windows CI runs include an unsigned `InputForge-Windows-x64-Review` artifact.
Extract it and run `review/InputForge.App.exe` on Windows x64 with the .NET 10 Desktop Runtime.

## Licence

InputForge source is available under GPL-3.0-or-later. Rusfort's GLB retains its separate CC BY 4.0 licence.
The ViGEm managed client and its native library are third-party components distributed under MIT terms.
