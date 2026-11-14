# InputForge 0.2.0-beta.1 engine and Windows safety audit

## A. Risks found

- `Mapper.ProcessMouse` scaled packet sums by the latest report interval. One count on an 8 ms tick was half-scale before shaping, and on a 2 ms tick it saturated. Scheduler jitter therefore changed sensitivity.
- `InputSession.Move` clipped each cumulative update at ±4096, so rapidly opposing large packets could leave a biased remainder. The prior stall guard discarded the entire update regardless of packet age.
- `InputRuntime.OnMessage` could submit a neutral driver report from a Raw Input callback. Driver work and Raw Input shared a dispatcher with a coarse timer and no deadline skipping.
- The backend retried forever after faults. The only emergency stop depended on the WPF UI. Shutdown saved the profile before stopping report production.
- Output encoding assumed valid finite axes. Profile JSON had no byte limit or schema field.

## B. Safeguards

The native worker uses a monotonic deadline and a process-local Windows waitable timer. It drains at most 128 messages before revisiting deadlines. It skips overdue reports instead of trying to catch up with a burst. Fallback uses a message wait with a finite timeout. The requested default is 125 Hz. 250 and 500 Hz are experimental requests. Neither actual output rate nor end-to-end latency is guaranteed.

Relative motion is accumulated in a bounded ring of 1 ms bins using monotonic receipt time. Windows message age is checked before accepting a packet. Motion queued for 32 ms or more is rejected. Old focus-scope messages are ignored and a backlog older than one second latches a stop. An unsigned tick difference handles the 32-bit Win32 timestamp wrap. Each report samples the preceding 16 ms and divides the sum by 0.016 s, regardless of report cadence. Motion older than the sampling window expires. After a long tick, smoothing state is cleared while fresh motion remains eligible.

Mouse transfer uses the following order. Sum signed counts, optionally subtract a radial noise threshold in counts per window, divide by the window length to obtain counts/s, divide by the beta reference speed of 800 counts/s, scale X/Y independently, and invert Windows mouse Y. Then apply the radial deadzone, acceleration, saturation, response curve, optional anti-deadzone and maximum output. Optional time-based filtering operates on the resulting vector. The directional unit vector preserves diagonals. With linear response and the default deadzone, one count remains neutral and two counts are about 8% output at unit sensitivity. DPI, the chosen curve, and the target application's own mapping affect the feel. The beta changes the reference scale while preserving all saved numeric settings.

Focus scoping requires a matching foreground executable and rechecks focus immediately before native submission. Opposite stick directions cancel. Scope changes clear held states, pending motion, and filter history. A disconnected, disabled, or faulted target is neutralised and disposed on the output worker. Native connect or write failure latches a stop. The worker does not automatically retry a faulted connection.

A process-registered Ctrl+Alt+Shift+F11 stop is handled by the input worker. Microsoft reserves F12 for debuggers. The single F11 key cannot be mapped in the beta, including through a legacy configuration. If registration fails, virtual output stays off and local testing remains available. Escape and Testing Lab's Emergency disable also latch the stop. A separate Timer watchdog detects a stalled active worker and latches a stop before the next successful return from native code. Session notifications, when registered, and long output pauses also request a stop. On close, the worker gets a two-second join budget before preferences are saved.

Xbox reports reject nonfinite values and clamp finite values before conversion. Profile files are limited to 4 MiB and libraries to 512 entries. Version 0 retains old associations and numeric settings. The first versioned save backs up a readable version-0 library, and writes use a same-directory temporary file. Legacy single-profile files remain on disk.

## C. Automated verification

Portable tests exercise signed motion accumulation, equivalent velocity at multiple input and output cadences, small and large counts, reversal, decay, pause recovery, held input, virtual-report bounds, the safety latch, profile migration and deadline skipping. The opted-in Windows test sends known reports to ViGEmBus and reads their Xbox values independently through XInput, then checks neutralisation and device removal.

```powershell
$env:INPUTFORGE_DRIVER_TEST = '1'
dotnet test InputForge.slnx -c Release --filter FullyQualifiedName~VirtualXboxIntegrationTests
```

Run the opt-in test only on a Windows test machine where ViGEmBus is already installed, with a free XInput slot and no application consuming controller input. It does not provision the driver. Normal CI skips it.

## D. Remaining risks and manual beta checks

**UNVERIFIED** on a driver-equipped desktop: actual ViGEmBus connection and compatibility, games receiving reports, global hotkey delivery and conflict handling, physical mapping feel, measured 125/250/500 Hz pacing, Windows lock and power transitions, rapid focus switching, system shutdown and native failure recovery. Check W+A+Space+Shift with both mouse buttons, a fast reversal, releasing one button while others stay held, focus loss, and emergency stop while the target is focused. Confirm that physical input still passes through.

The watchdog cannot preempt a thread inside a hung native driver API. Neutral submission and disconnect are best effort. Windows or driver failures, forced process termination and power loss can prevent orderly cleanup. UI stalls do not stop the independent scheduler. If the application stops responding, use Windows recovery controls or Task Manager. Raw Input is registered for background delivery while the app runs, but packets are decoded only for an allowed foreground executable or explicit local testing. The emergency hotkey reserves only its chord. Local replay stays in memory and no input is uploaded.

ViGEmBus and its managed client are retired and retained for the beta. VirtualPad is commercial. LuminalVGBus is an unsigned alpha fork with a different protocol. Neither was adopted. An active and signed replacement still needs evaluation. See [ViGEmBus end of life](https://docs.nefarius.at/projects/ViGEm/End-of-Life/), [VirtualPad](https://docs.nefarius.at/projects/VirtualPad/) and [LuminalVGBus](https://github.com/NortheBridge/LuminalVGBus).

## E. Permissions

InputForge declares asInvoker and uiAccess=false. Normal operations do not request elevation. Installing ViGEmBus requires a separate user-initiated driver installation and may require admin approval. The beta does not contain an installer for the driver, modify driver settings or install services.

## F. Display protection

The inspected first-party code only changes the application's own WindowStyle and WindowState for fullscreen. It contains no calls to change resolution, refresh rate, HDR, monitor arrangement, colour profile, scaling or GPU settings. The DPI manifest changes the app's coordinate handling. Existing rendering pauses on minimise, is event-driven during orbit and uses WPF's software fallback. No OS display setting was modified during verification.

## G. Emergency recovery

Press Ctrl+Alt+Shift+F11 even when the target application has focus, or press Esc in InputForge when not rebinding. In Testing Lab, Emergency disable does the same. This clears the mapped state on the next available output tick and keeps it disabled until manually enabled or local testing is explicitly started. If a driver call is hung, terminate InputForge via Task Manager. Normal keyboard and mouse input is passed through. The virtual Xbox device may be visible to other processes while connected. No controller-hiding or suppression driver is used.

[Microsoft hotkey API](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerhotkey) · [Microsoft waitable timer API](https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw)
