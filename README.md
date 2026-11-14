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

- On Controller, drag the model to orbit, use the wheel to zoom, or click a control to select its binding.
  Double-click the background to reset the view. Test inputs on Dashboard or Controller
  to visualise real mappings without a virtual device.
- On Mouse, use the Sensitivity, Filtering and Response Curve tabs.
- In Testing Lab, choose **Test inputs** to use real keyboard and mouse input without creating a virtual device.
- For virtual Xbox 360 output, separately install [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0).
  Select the allowed executable under Profiles, enable mapping, and focus that executable.
  ViGEmBus is a retired driver. InputForge neither distributes nor installs it.

WPF renders the original procedural controller using `Viewport3D`, including WPF's software
rendering fallback. The viewport consumes output snapshots from the Raw Input worker and pauses
UI polling while minimized. Mapping is disabled on startup, and capture is scoped to the chosen
foreground executable or the explicitly enabled local test. The application stores preferences in
`%LOCALAPPDATA%\InputForge` and makes no network requests during normal use.

The geometry is an original Elite-inspired model with a contoured case, shell seam, faceted D-pad
and rear paddles. It is an interpretation of the hardware, with no manufacturer mesh or textures.

There are currently no GitHub release binaries. Test the native layout, hit targets and keyboard
navigation at different Windows scaling settings before relying on this interface. Driver
connection, real game output and focus-loss cleanup require a separate manual Windows test.

Successful Windows CI runs include an unsigned `InputForge-Windows-x64-Review` artifact.
Extract it and run `review/InputForge.App.exe` on Windows x64 with the .NET 10 Desktop Runtime.

## Licence

InputForge source and original model geometry are available under GPL-3.0-or-later.
The ViGEm managed client and its native library are third-party components distributed under MIT terms.
