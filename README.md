# MuMu Device Frames

Real iPhone, Pixel and Galaxy frames around running [MuMu Player](https://www.mumuplayer.com/) windows — live on your
desktop, for screen recordings and app demos. No injection, no root, no emulator changes: a tray app that attaches a
frame to each emulator and teaches the emulator's Android about it.

![Three MuMu emulators framed as iPhone 17 Pro Max, iPhone 17 Pro and Pixel 9 Pro XL](docs/hero.png)

Built to record ads for [JustGains](https://justgains.com) — the app in the screenshots — and released because there
was nothing like it.

## What it does

![Stock MuMu window next to the same emulator with a device frame](docs/before-after.png)

- **The frame belongs to the emulator, not to the top of the screen.** It sits directly above its emulator in the
  window stack, so anything you place over the emulator — captions, an overlay, a second app — covers the frame too.
  Minimize, restore, move: the frame follows.
- **The MuMu window disappears.** Title bar, toolbar, borders and window shadow are gone; only the phone's rounded screen
  is left. On a chroma-key desktop you get phone + key colour and nothing else.
- **Android knows about the frame.** A stock emulator lays itself out as a plain rectangle, so the clock slides under the
  rounded corners and app headers sit beneath the Dynamic Island. One click gives the emulator the frame's real display
  cutout, status-bar height and corner radius, so apps get the same safe-area insets as on the physical phone.
- **Pixel-perfect screenshots.** The **Save** tile at the top of the bezel menu takes the shot inside Android and composites it into the frame
  at native resolution (1520 × 3068 for a 17 Pro Max), whatever is covering the emulator window. **Copy**
  puts the same PNG, transparent corners included, straight on the clipboard for Figma, Photoshop, Slack
  or a chat window.
- **145 frames** from the open [device-frames-media](https://github.com/jonnyjackson26/device-frames-media) set —
  iPhone 6s to 17 Pro Max, iPhone Air, Pixel 8/9, Galaxy S21, iPads, Apple Watch — downloaded on first run.
- **iPhone Duo, folding live.** Closed, half open and open frames for Apple's foldable, drawn by the app itself, and
  a switch that changes the emulator between them in under a second, with no restart. Android itself is made a
  foldable, so apps get the fold through Jetpack WindowManager. See [iPhone Duo](#iphone-duo).

- **iOS-style status bar (optional).** Hides Android's clock and icons and draws an iPhone status bar in their place:
  bold time, cellular bars, Wi-Fi and battery, centred on the island and switching between black and white with
  whatever the app asks for. It is click-through, so the notification shade still works, and framed screenshots
  include it.

![Status bar before and after matching the cutout](docs/cutout.png)

## Install

In PowerShell (no admin rights needed):

```powershell
irm https://raw.githubusercontent.com/JustSuperHuman/MuMuDeviceFrames/main/install.ps1 | iex
```

That installs the latest release to `%LOCALAPPDATA%\Programs\MuMuDeviceFrames`, downloads the frame set (~35 MB, once),
adds a Start menu shortcut, turns on **Start with Windows** and starts the tray app. Run it again to update.
Then start a MuMu emulator. It gets the frame that best fits its picture — with MuMu's default phone resolution that is
an iPhone 16 Pro / 17 Pro.

Options (pass them through a script block):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/JustSuperHuman/MuMuDeviceFrames/main/install.ps1))) -NoStartup
```

| Option | |
| --- | --- |
| `-NoStartup` | Don't start with Windows (the tray menu can turn it on later) |
| `-Version v1.1.0` | Install a specific release instead of the latest |
| `-Zip <path>` | Install a local build (zip or `publish` folder), e.g. `.\install.ps1 -Zip publish` from a clone |
| `-NoLaunch` | Install without starting the app |
| `-InstallAndroidTools` | Run interactive WinGet setup for the optional Android safe-area and iOS status-bar tools |
| `-Uninstall` | Quit the app, restore the emulator windows, remove the startup entry, shortcut and program folder (keeps `%APPDATA%\MuMuDeviceFrames`) |

Manual install: download `MuMuDeviceFrames-win-x64.zip` from
[Releases](https://github.com/JustSuperHuman/MuMuDeviceFrames/releases), unzip anywhere and run `MuMuDeviceFrames.exe`
(self-contained; nothing else to install). Say yes to the frame download.

For the Android matching (status bar/cutout) the tool needs the Android SDK build tools and a Java runtime; see below.

## Using it

| Action | Result |
| --- | --- |
| Drag the bezel | Moves the emulator (its title bar is hidden, so the bezel is the handle) |
| Right-click the bezel | Menu for that emulator: Copy / Save / Reload / Dev gear tiles, recording when compatible ShareX is installed, then frame, Android display, minimize |
| Double-click the bezel | Peek: brings MuMu's title bar and toolbar back without dropping the frame; again to hide |
| `Ctrl+Alt+F` | All frames off / on (off = emulators look exactly like stock MuMu) |
| `Ctrl+Alt+R` | Reload the Expo app in the active or last active emulator |
| `Ctrl+Alt+G` | Show or hide Expo's floating gear in that emulator |
| `Ctrl+Alt+D` | Fold or unfold the iPhone Duo in that emulator (see [iPhone Duo](#iphone-duo)) |
| `Ctrl+Alt+B` | Half open that iPhone Duo like a book, or back to flat |
| Tray icon | Same menus for every emulator, default frame, frame download, Start with Windows, exit |

The Expo actions are also in each emulator's bezel and tray menus. They work when an Expo development app is in front
inside that emulator. With several emulators open, click the one you want before using a hotkey. External hotkey tools
can run `MuMuDeviceFrames.exe --expo-reload` or `MuMuDeviceFrames.exe --expo-gear` while the tray app is running. Add an
emulator name, such as `--expo-reload JG-2`, to target one directly.

Frame menus mark each model **Exact fit** / **Near fit** for that emulator's picture. A frame whose screen aspect is
within 1.5 % of the emulator's is stretched to fit exactly (invisible); anything further off keeps its proportions and
crops the picture under the bezel — or use **Android display ▸ Set resolution to W×H**, which writes the frame's exact
screen resolution into MuMu's settings (density scaled so the app layout keeps its size) and restarts that emulator.

Landscape: when MuMu rotates its window, the frame rotates with it (phone top to the left).

### Recording the full frame

Install a compatible ShareX build with device frame recording support to show **Record entire frame** in each emulator menu. The command captures
the visible frame and 15 pixels around it on every side. Change **Recording padding** in the tray settings, or set
`RecordingPadding` in the config to any value from 0 to 500. Keep the full frame and padding on one display before starting.
Use **Stop recording** in the same menu or ShareX's recording controls when you're done. ShareX then shows your video actions and compatible saved quick-task presets.
The running tray app also accepts `MuMuDeviceFrames.exe --record-frame [emulator]` and
`MuMuDeviceFrames.exe --stop-frame-recording` for hotkey tools. Omit the emulator name to use the active or last used one.

These recordings keep the Windows pointer out of the video and save click positions with the video. Open the recording
in ShareX's video editor and use **Phone tap** to show a brief touch and ripple at each click. The mode is selected
automatically for recordings started from this menu. Export the edit to put the taps into the finished video.
Use **Tap size**, **Brightness**, and **Impact** in the editor to make the ripple larger, brighter, or more dramatic.

### Making Android fit the frame

**Match Android status bar and cutout to frame** (per emulator) measures the island, notch or camera hole and the
corner size from the frame's own `mask.png` and installs them into the emulator as a display cutout, status-bar height,
corner radius and status-bar side padding. It follows frame changes automatically (rebuilds in a few seconds), survives
emulator restarts, and unticking it uninstalls everything. `StatusBarPaddingFactor` in the config moves the clock and
icons further in or out.

**iOS-style status bar** in the tray menu (or `IosStatusBar` in the config) applies to every emulator as it
starts; the bezel menu can override it for one emulator. It turns on the cutout matching above by itself, empties
Android's status bar through SystemUI's icon blocklist (`settings put secure icon_blacklist …`) and the SystemUI overlay
(no notification icons, overflow dot or clock — Android 15's clock ignores the blocklist), then draws
the iPhone bar in a click-through window over the picture. Light/dark follows the foreground app's
`LIGHT_STATUS_BARS` request, polled once a second. `StatusBarClock` sets the time (`auto`, `24h`, or fixed text such as
`9:41`); `StatusBarFont` picks the font (SF Pro if installed, otherwise Segoe UI Semibold). Works best together with the
cutout matching above. Unticking it, exiting, or `--restore` gives Android its own status bar back.

How this works without root: MuMu's Android images are signed with the public AOSP *platform test key*, and Android
lets an overlay signed like its target override any of the target's resources. The tool generates two tiny
resource-overlay APKs (`com.justsuperhuman.deviceframes.cutout` → `android`, `….systemui` → `com.android.systemui`),
signs them with that key ([`tools/aosp-platform-key`](tools/aosp-platform-key/README.md)) and enables them over adb.

When cutout matching or the iOS-style status bar needs missing tools, the app offers to install them. The prompt appears
once per app session, shared across emulators. Choose **No** to defer, then use **Install / repair Android display tools**
in the tray menu whenever you are ready. The iOS bar waits until Android's matching safe-area overlay is installed.

Setup reuses existing tools. When needed, it uses WinGet's exact `EclipseAdoptium.Temurin.21.JDK` and `Google.AndroidCLI`
packages, then Google's Android CLI installs only SDK build-tools 35.0.0 and platform 35. Android Studio and emulator
images are not required. A console shows progress and package/license prompts; Java's installer may request administrator
approval. Close the setup window to cancel. Failed or partial installations can be retried from the tray menu.
The setup log is `%APPDATA%\MuMuDeviceFrames\android-tools-install.log`.

After successful verification, the app saves the SDK path and retries failed overlays automatically, without a restart.
It detects newly installed Java through user/machine settings and standard install locations, even when the running
app has an older environment. It does not replace your global SDK environment settings.

For setup from PowerShell, run `tools/install-android-tools.ps1` (optionally `-SdkRoot 'D:\Android SDK'`), or pass
`-InstallAndroidTools` to `install.ps1`. If WinGet is missing, install or update
[App Installer](https://aka.ms/getwinget), then retry. You can also install dependencies manually: SDK build-tools
(`aapt2`, `zipalign`, `apksigner` including its JAR), a platform `android.jar`, and Java 17 or newer. The SDK is found
through `AndroidSdk` in config, `ANDROID_HOME`, `ANDROID_SDK_ROOT`, or `%LOCALAPPDATA%\Android\Sdk`.
Android Studio's SDK Manager can install the SDK components. To remove overlays by hand:
`adb uninstall com.justsuperhuman.deviceframes.systemui` and `…deviceframes.cutout`.

### iPhone Duo

Apple's foldable has two displays: closed, the outer 1398 × 2034 display (466 × 678 pt, portrait, hinge on the left);
open, the inner 2670 × 1878 display (890 × 626 pt, landscape). MuMu cannot change resolution without a restart, so the
emulator runs at the bounding box of both, **2670 × 2034 at 480 dpi**, and Android's `wm size` picks the display.
Each display shares one side with that surface, so Android draws it at exactly 1:1 (no scaling), centred, with black
bars where the other display would be: folded at x 636 of the surface, open at y 78. Apps see 466 × 678 dp folded and
890 × 626 dp open, and the switch is live, like folding a real phone: the app in front is resized, not restarted
(as long as it handles `screenSize|smallestScreenSize|screenLayout|orientation` changes, as Expo and React Native apps do).

The emulator is also a real Android foldable. Android has the device states CLOSED, HALF_OPENED and OPENED, a framework
overlay maps them to Jetpack WindowManager's postures, and a fold line runs down the middle of the inner display, so
apps get a `FoldingFeature` from Jetpack WindowManager (or `androidx.window.extensions`) the way they do on a Galaxy Z
Fold:

| Pose | Display apps see | Device state | FoldingFeature |
| --- | --- | --- | --- |
| Closed | 466 × 678 dp, portrait | 0 CLOSED | none |
| Half open (book) | 890 × 626 dp, landscape | 1 HALF_OPENED | vertical fold at x 1335 px, state HALF_OPENED |
| Open | 890 × 626 dp, landscape | 2 OPENED | vertical fold at x 1335 px, state FLAT |

**Setting an emulator up**: choose **Frame ▸ Apple iPhone ▸ iPhone Duo ▸ Closed**, then **Android display ▸ Set up as
iPhone Duo…** (or `MuMuDeviceFrames.exe --duo-setup <emulator>`). It does whatever is missing, about 30 seconds the
first time, and leaves the emulator closed:

1. MuMu settings: 2670 × 2034 at 480 dpi, and MuMu's window auto-rotate off (MuMu turns its window, and Android with
   it, to follow the app in front).
2. The device states, in `/data/system/devicestate/device_state_configuration.xml` (owner system, mode 0644,
   SELinux `system_data_file`). Only root can write there, and on MuMu's Android 15 image root is `adb root` (there is
   no `su`): the setup turns on MuMu's root permission, restarts, writes the file over `adb root`, puts the root
   permission back as it was, and restarts again so Android loads the states.
3. The postures overlay `com.justsuperhuman.deviceframes.foldable` (`config_device_state_postures` = `0:1`, `1:2`,
   `2:3`), signed with the AOSP platform test key like the cutout overlays. Apps read it once per process.

After that nothing needs root: every pose switch runs as the shell user.

| Fold / unfold | |
| --- | --- |
| Bezel or tray menu | **Fold (outer display)**, **Half open (book)**, **Unfold (inner display)**; picking Closed, Half open or Open in the Frame menu does the same |
| `Ctrl+Alt+D` | Fold or unfold the active or last used emulator; half open folds (`DuoHotkey` in the config) |
| `Ctrl+Alt+B` | Half open like a book, or back to flat (`DuoHalfHotkey`) |
| `MuMuDeviceFrames.exe --duo-fold [emulator]`, `--duo-half [emulator]`, `--duo-unfold [emulator]` | Through the running tray app, so the frame follows. Without it, a named emulator is switched over adb directly |

A switch runs the commands below over adb, swaps the frame (closed, half open or open), and moves the window clip and
the frame to the part of MuMu's window the new display uses. The tray app reads the display, rotation and device state
when it attaches and every few seconds after, and puts the pose back when something moved it: an Android restart
(which clears the rotation lock and the device state; half open comes back as open), MuMu turning the display for an
app it remembers in portrait (fixed within a second), or a script. **Save** and **Copy** take the screenshot inside
Android, and its size decides the display: the 1398 × 2034 or 2670 × 1878 picture goes into the matching frame pixel
for pixel.

The commands, if you want them in a script. MuMu reports a landscape resolution as a portrait panel turned 90°, and
`wm size` takes the panel's own orientation, so the sizes are swapped; `display_features` uses that orientation too.
Rotation is pinned so a portrait-only app cannot turn the display round. The order matters: Android only reports the
fold while the window is the inner display's size (its extensions throw in the app for a fold that does not span the
window), so folding drops the device state first and opening sets it last.

```
# fold
adb shell "cmd device_state state 0; settings delete global display_features; wm fixed-to-user-rotation enabled; wm user-rotation lock 1; wm size 2034x1398; wm density 480"
# half open (book)
adb shell "wm fixed-to-user-rotation enabled; wm user-rotation lock 1; wm size 1878x2670; wm density 480; settings put global display_features fold-[0,1335,1878,1335]; cmd device_state state 1"
# unfold
adb shell "wm fixed-to-user-rotation enabled; wm user-rotation lock 1; wm size 1878x2670; wm density 480; settings put global display_features fold-[0,1335,1878,1335]; cmd device_state state 2"
```

Without the foldable setup (an emulator only set to 2670 × 2034), folding and unfolding still work with the `wm`
commands alone; apps then see the sizes but no fold, and half open is not available.

The frames are drawn by the app on first run (`%APPDATA%\MuMuDeviceFrames\builtin-frames`, in the same
frame/mask/template format as the downloaded set) and need no download. Closed: titanium rim with a lit edge, thin
black border, small corners on the hinge side and large ones on the opening side, and the hinge barrel on the left.
Open: rounded on all four corners, with a faint crease down the middle of the screen; half open, the crease is wider
and deeper. The crease is a separate `glass.png` layer, shown in a click-through window, so taps along the middle
still reach the app. `MuMuDeviceFrames.exe --duo-frames <folder>` writes the frames to a folder.

The Duo leaves the status bar to the app: the JustGains app hides Android's status bar on these sizes and draws the
time, status and front camera in its own rail. So a Duo frame never installs the cutout overlays or draws the
iOS-style status bar, whatever those switches say. The camera is not part of the frame.

## Configuration

`%APPDATA%\MuMuDeviceFrames\config.json` (tray ▸ *Open config folder*, then *Reload frames and config*). Frame choices
and the Android-matching switch are stored per emulator name. `log.txt` is next to it.

| Key | Default | |
| --- | --- | --- |
| `FramesRoot` | `%APPDATA%\MuMuDeviceFrames\frames` | Frame set folder. A `device-frames-output` folder next to the exe is used if present. |
| `DefaultFrame` | `null` (best aspect match) | `"Category/Model/Variant"`, e.g. `"Apple iPhone/17 Pro Max/Cosmic Orange"` |
| `ClipToScreen` | `true` | Hide the MuMu window around the picture |
| `Overlap` | `1` | Pixels the bezel overlaps the picture, so its anti-aliased edge blends with app content |
| `BezelInteractive` | `true` | `false` makes the frame fully click-through |
| `Hotkey` | `Ctrl+Alt+F` | |
| `ExpoReloadHotkey` | `Ctrl+Alt+R` | Reload the Expo app in the active or last active emulator; empty string disables it. |
| `ExpoGearHotkey` | `Ctrl+Alt+G` | Show or hide Expo's floating gear there; empty string disables it. |
| `DuoHotkey` | `Ctrl+Alt+D` | Fold or unfold the iPhone Duo in the active or last active emulator; empty string disables it. |
| `DuoHalfHotkey` | `Ctrl+Alt+B` | Half open that iPhone Duo, or back to flat; empty string disables it. |
| `LastActiveEmulator` | `null` | Last emulator used for Expo hotkeys when another app is in front. Updated automatically. |
| `RecordingPadding` | `15` | Pixels to capture around every side of the full frame, from 0 to 500. |
| `ShareXExecutable` | `null` | Optional path to a compatible ShareX.exe outside the usual install folders. |
| `StatusBarPaddingFactor` | `0.5` | Status-bar side padding as a fraction of the frame's corner size |
| `IosStatusBar` | `false` | iOS-style status bar on every emulator (per-emulator overrides live under `Instances`) |
| `StatusBarClock` | `auto` | Time on the iOS-style bar: `auto`, `24h`, or fixed text like `9:41` |
| `StatusBarFont` | `null` | Font for that time (SF Pro if installed, else Segoe UI Semibold) |
| `AndroidSdk` | `null` (`ANDROID_HOME`) | SDK folder for the overlays |
| `ProcessNames` | `MuMuNxDevice, MuMuPlayer, NemuPlayer` | Emulator executables to attach to |

Command line: `--exit` (quit the running instance cleanly) · `--expo-reload [emulator]` / `--expo-gear [emulator]` (Expo actions; omit the name to use the running tray app) · `--startup on|off` (start with Windows) · `--restore` (un-clip all emulator windows; only needed if
the app was killed — starting it again does the same) · `--download [folder]` (fetch the frame set) ·
`--screenshot <emulator> <out.png> [frame] [transparent|checker|#RRGGBB]` (framed screenshot at native resolution) ·
`--snapshot <emulator> <out.png> [frame|-] [background]` (composite of the window and the frame, for checking
alignment) · `--render <frame> <w> <h> <out.png>` · `--cutout-spec <frame> <w> <h> <dpi>` ·
`--duo-fold [emulator]` / `--duo-half [emulator]` / `--duo-unfold [emulator]` (iPhone Duo closed, half open or open) ·
`--duo-setup <emulator>` (make it the iPhone Duo, an Android foldable) · `--duo-frames <folder>` (draw the iPhone Duo
frames into a folder).

## How it works

Three window-manager features, all from outside the MuMu process:

1. **Owned layered window.** Each frame is a per-pixel-alpha window (`UpdateLayeredWindow`) whose *owner* is the
   emulator's top-level window. Windows keeps an owned window immediately above its owner and minimizes, restores and
   destroys it with the owner — that is the "attached but not topmost" behaviour, done by the window manager itself.
   Transparent pixels are click-through; only the bezel takes mouse input, and dragging it moves both windows in one
   `DeferWindowPos` batch.
2. **Window region.** `SetWindowRgn` on the emulator, built from the frame's `mask.png`, hides everything but the
   screen. The region is grown 2 px so its hard edge lies under the opaque bezel, and the bezel overlaps the picture by
   1 px so the anti-aliased inner edge always blends with app pixels, never with the desktop.
3. **DWM attributes.** A region alone leaves DWM painting the window's shadow and 1 px border around the *unclipped*
   rectangle. `DWMWA_NCRENDERING_POLICY = DISABLED` + `DWMWA_BORDER_COLOR = NONE` remove that; the theming layer then
   installs a region of its own, which is why ours is verified and re-applied.

Tracking uses `SetWinEventHook` per emulator process plus a 1.5 s scan for new emulators. Frames are resampled once per
picture size with a separable Lanczos-3 filter in premultiplied alpha, with the screen hole's edges snapped to whole
pixels; about 70 ms, cached and shared between emulators of the same size. Everything is reverted on exit.

Why not a Windhawk mod or a DLL in MuMu: injected code can only draw inside MuMu's window, and the bezel lives outside
it. A second window is needed either way — and with one, nothing has to run inside the emulator process.

## Known limits

- OBS *Window Capture* of the emulator won't include the frame (it is a separate window). Use display or region capture.
- In landscape Android keeps its normal status bar and moves the cutout to the side, as a real phone does; only the
  portrait status-bar height is overridden.
- Frame variants with baked-in shadows take mouse input on the shadow too (any pixel with alpha).
- Windows only; MuMu Player 12 (`MuMuNxDevice.exe`) is what it was written against. Older MuMu builds are matched by
  process name and the same `nemuwin` render surface, untested.

## Build

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

.NET 10 SDK. Source is in `src/`; `tools/make-icon.ps1` regenerates the icon. Releases are built by
[GitHub Actions](.github/workflows/build.yml) on `v*` tags.

Dependency checks and installer regression checks (no package installation or emulator required):

```powershell
dotnet run --project tests/AndroidTools.Tests.csproj -c Release
./tests/install-android-tools.tests.ps1
```

## Credits

- Frame images: [device-frames-media](https://github.com/jonnyjackson26/device-frames-media) by Jonny Jackson. They
  are downloaded from that project at first run, not bundled here. The iPhone Duo frames are drawn by this app
  (`src/DuoFrames.cs`).
- Written for [JustGains](https://justgains.com), the workout and nutrition app in the screenshots.

MIT — see [LICENSE](LICENSE).
