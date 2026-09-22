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
- **Pixel-perfect screenshots.** "Save framed screenshot" takes the shot inside Android and composites it into the frame
  at native resolution (1520 × 3068 for a 17 Pro Max), whatever is covering the emulator window.
- **145 frames** from the open [device-frames-media](https://github.com/jonnyjackson26/device-frames-media) set —
  iPhone 6s to 17 Pro Max, iPhone Air, Pixel 8/9, Galaxy S21, iPads, Apple Watch — downloaded on first run.

![Status bar before and after matching the cutout](docs/cutout.png)

## Install

1. Download `MuMuDeviceFrames-win-x64.zip` from [Releases](https://github.com/JustSuperHuman/MuMuDeviceFrames/releases),
   unzip anywhere, run `MuMuDeviceFrames.exe`. It lives in the tray. (Self-contained; nothing else to install.)
2. Say yes to the frame download (~35 MB, once).
3. Start a MuMu emulator. It gets the frame that best fits its picture — with MuMu's default phone resolution that is
   an iPhone 16 Pro / 17 Pro.

For the Android matching (status bar/cutout) the tool needs the Android SDK build tools and a Java runtime; see below.

## Using it

| Action | Result |
| --- | --- |
| Drag the bezel | Moves the emulator (its title bar is hidden, so the bezel is the handle) |
| Right-click the bezel | Menu for that emulator: frame, Android matching, screenshot, minimize |
| Double-click the bezel | Peek: brings MuMu's title bar and toolbar back without dropping the frame; again to hide |
| `Ctrl+Alt+F` | All frames off / on (off = emulators look exactly like stock MuMu) |
| Tray icon | Same menus for every emulator, default frame, frame download, exit |

Frame menus mark each model `● exact fit` / `○ near fit` for that emulator's picture. A frame whose screen aspect is
within 1.5 % of the emulator's is stretched to fit exactly (invisible); anything further off keeps its proportions and
crops the picture under the bezel — or use **Set display to W×H for this frame**, which writes the frame's exact
screen resolution into MuMu's settings (density scaled so the app layout keeps its size) and restarts that emulator.

Landscape: when MuMu rotates its window, the frame rotates with it (phone top to the left).

### Making Android fit the frame

**Match Android status bar and cutout to frame** (per emulator) measures the island, notch or camera hole and the
corner size from the frame's own `mask.png` and installs them into the emulator as a display cutout, status-bar height,
corner radius and status-bar side padding. It follows frame changes automatically (rebuilds in a few seconds), survives
emulator restarts, and unticking it uninstalls everything. `StatusBarPaddingFactor` in the config moves the clock and
icons further in or out.

How this works without root: MuMu's Android images are signed with the public AOSP *platform test key*, and Android
lets an overlay signed like its target override any of the target's resources. The tool generates two tiny
resource-overlay APKs (`com.justsuperhuman.deviceframes.cutout` → `android`, `….systemui` → `com.android.systemui`),
signs them with that key ([`tools/aosp-platform-key`](tools/aosp-platform-key/README.md)) and enables them over adb.

Requirements: Android SDK build-tools (`aapt2`, `zipalign`, `apksigner`) and a platform `android.jar`, found through
`ANDROID_HOME` or the `AndroidSdk` config entry, plus Java for `apksigner`. Android Studio's SDK Manager installs all of
it. To remove by hand: `adb uninstall com.justsuperhuman.deviceframes.systemui` and `…deviceframes.cutout`.

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
| `StatusBarPaddingFactor` | `0.5` | Status-bar side padding as a fraction of the frame's corner size |
| `AndroidSdk` | `null` (`ANDROID_HOME`) | SDK folder for the overlays |
| `ProcessNames` | `MuMuNxDevice, MuMuPlayer, NemuPlayer` | Emulator executables to attach to |

Command line: `--exit` (quit the running instance cleanly) · `--restore` (un-clip all emulator windows; only needed if
the app was killed — starting it again does the same) · `--download [folder]` (fetch the frame set) ·
`--screenshot <emulator> <out.png> [frame] [transparent|checker|#RRGGBB]` (framed screenshot at native resolution) ·
`--snapshot <emulator> <out.png> [frame|-] [background]` (composite of the window and the frame, for checking
alignment) · `--render <frame> <w> <h> <out.png>` · `--cutout-spec <frame> <w> <h> <dpi>`.

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

## Credits

- Frame images: [device-frames-media](https://github.com/jonnyjackson26/device-frames-media) by Jonny Jackson. They
  are downloaded from that project at first run, not bundled here.
- Written for [JustGains](https://justgains.com), the workout and nutrition app in the screenshots.

MIT — see [LICENSE](LICENSE).
