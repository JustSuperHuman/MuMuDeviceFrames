# iPhone Duo

Apple's foldable, folding live in a MuMu emulator: six poses, a fold animation, iOS's status and home indicator,
and the Duo's safe areas as real Android insets. Back to the [README](../README.md); everything else is in the
[reference](reference.md).

![The iPhone Duo unfolding with JustGains on screen](duo-fold.gif)

![JustGains on the iPhone Duo in all six poses](duo-poses.png)

## Getting one

- **New emulator:** tray ▸ **Create iPhone Duo emulator…**, `MuMuDeviceFrames.exe --duo-create [name]`, or tick
  **Set up an iPhone Duo emulator** in the installer. It creates the MuMu instance and does all of the setup below, in
  about a minute.
- **Existing emulator:** see *Setting an emulator up* below, or `--duo-setup <emulator>`.
- **Something off:** bezel menu ▸ **Check iPhone Duo…** or `--duo-doctor [emulator]` checks every piece and repairs
  what it can (`--check` only reports).

## How it works

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
| Open portrait | 626 × 890 dp, portrait | 2 OPENED | horizontal fold at y 1335 px, state FLAT |
| Tabletop | 626 × 890 dp, portrait | 1 HALF_OPENED | horizontal fold at y 1335 px, state HALF_OPENED |
| Tent | 678 × 466 dp, landscape (the outer display, standing folded, hinge on top) | 0 CLOSED | none |

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

![The iPhone Duo with its pose buttons beside it](pose-panel.png)

| Fold / unfold | |
| --- | --- |
| Pose buttons | Beside the device, one per pose (`DuoPoseControls` turns them off) |
| `Ctrl+Alt+1` to `Ctrl+Alt+6` | Closed, Open, Book, Open portrait, Tabletop, Tent (`DuoPoseHotkeys` in the config) |
| Bezel or tray menu | **Fold (outer display)**, **Half open (book)**, **Unfold (inner display)**; picking a pose in the Frame menu does the same |
| `Ctrl+Alt+D` | Fold or unfold the active or last used emulator; half open folds (`DuoHotkey` in the config) |
| `Ctrl+Alt+B` | Half open like a book, or back to flat (`DuoHalfHotkey`) |
| `MuMuDeviceFrames.exe --duo-pose <closed\|open\|book\|portrait\|tabletop\|tent> [emulator]`, `--duo-fold`, `--duo-half`, `--duo-unfold` | Through the running tray app, so the frame follows. Without it, a named emulator is switched over adb directly |

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
# tent: the outer display at rotation 0, which shows it landscape on MuMu's turned panel
adb shell "cmd device_state state 0; settings delete global display_features; wm fixed-to-user-rotation enabled; wm user-rotation lock 0; wm size 2034x1398; wm density 480"
```

Open portrait and tabletop are open and book at rotation 0. MuMu turns its window upright for those and for tent
(at rotation 1 it would stretch tent's narrower landscape picture to its window's width), and the frame follows it.

Without the foldable setup (an emulator only set to 2670 × 2034), folding and unfolding still work with the `wm`
commands alone; apps then see the sizes but no fold, and half open is not available.

The frames are drawn by the app on first run (`%APPDATA%\MuMuDeviceFrames\builtin-frames`, in the same
frame/mask/template format as the downloaded set) and need no download. Closed: titanium rim with a lit edge, thin
black border, small corners on the hinge side and large ones on the opening side, and the hinge barrel on the left.
Open: rounded on all four corners, with a faint crease down the middle of the screen; half open, the crease is wider
and deeper. Tent is the closed frame turned a quarter, hinge on top and outer camera at the bottom right. The crease is a separate `glass.png` layer, shown in a click-through window, so taps along the middle
still reach the app. `MuMuDeviceFrames.exe --duo-frames <folder>` writes the frames to a folder.

![The iOS status in the rail, in the corner when upright, and the home indicator](duo-ios.png)

**iOS on the Duo.** There is no iPhone status bar on the Duo: as in Apple's renders, the status is one element, the
time and a ring (battery as the arc over the top, Wi-Fi inside, cellular dots along its open bottom). It runs down the
trailing rail in Closed (under the outer camera), Open and Book, and in Tent (above the camera, which is at the bottom
there), and sits in the top-trailing corner in Open portrait and Tabletop. Its positions and sizes are measured on Apple's HIG renders and match them within 1 pt. The home
indicator is drawn in every pose. Light or dark comes from the pixels behind each.

Every app gets the Duo's safe areas as real Android insets: in Closed, Open, Book and Tent 84 pt on the right and 34
pt at the bottom, in Open portrait and Tabletop 68 pt at the top and 34 pt at the bottom. They are display cutouts of
the "waterfall" kind, in three sets (Closed, Open and Book; the open display upright; Tent), each in resources for that
configuration. Android reads the cutout when the display turns on, for the configuration it is in, so a switch from one
set to another turns the display off and on for half a second under the fold animation; those switches take about 1.5
to 2 seconds, the others about one. No app is relaunched by a pose switch.
Apps that are not edge to edge are kept clear of them (letterboxed). Installing them the first time relaunches open
apps once.

iOS's navigation gestures work too: swipe up from the bottom edge for home, in from a side edge for back. That is
Android's gesture navigation, which MuMu turns off at every boot; the tray app turns it back on (a restart of Android's
framework, not of the emulator) and needs MuMu's root permission for it, which new iPhone Duos keep on. Android 15 hands
the navigation bar to the launcher and MuMu's launcher draws none, so the gestures add no inset of their own.
