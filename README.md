# MuMu Device Frames

Real phone frames around [MuMu Player](https://www.mumuplayer.com/) emulators, live on your desktop: for app demos,
screen recordings and screenshots.

![Three MuMu emulators framed as iPhone 17 Pro Max, iPhone 17 Pro and Pixel 9 Pro XL](docs/hero.png)

## The emulator becomes the phone

![Stock MuMu window next to the same emulator with a device frame](docs/before-after.png)

MuMu's window disappears and the frame goes wherever the emulator goes. 145 iPhone, Pixel, Galaxy and iPad frames.

## Android lays out like the phone

![Status bar before and after matching the cutout](docs/cutout.png)

Apps get the phone's real island, corners and safe areas. On iPhone frames, iOS's status bar and home indicator
replace Android's.

## iPhone Duo

Apple's foldable, folding live. Six poses, one click each, and the app keeps running.

![The iPhone Duo unfolding with JustGains on screen](docs/duo-fold.gif)

![JustGains on the iPhone Duo in all six poses](docs/duo-poses.png)

![The iPhone Duo with its pose buttons beside it](docs/pose-panel.png)

![iOS on the Duo: the status in the rail, in the corner when upright, and the home indicator](docs/duo-ios.png)

Android becomes a real foldable: apps see the fold, and the Duo's safe areas sit exactly where iOS puts them.
[How it works](docs/iphone-duo.md)

## Install

Download **MuMuDeviceFrames-Setup.exe** from [Releases](https://github.com/JustSuperHuman/MuMuDeviceFrames/releases)
and run it. No admin rights needed. Tick **Set up an iPhone Duo emulator** to get a Duo too.

Or in PowerShell:

```powershell
irm https://raw.githubusercontent.com/JustSuperHuman/MuMuDeviceFrames/main/install.ps1 | iex
```

## Use

<img src="docs/menu.png" alt="The bezel menu: Copy, Save, Reload, Dev gear and Close apps, the iPhone Duo pose selector, and Record" width="390">

| | |
| --- | --- |
| Drag the bezel | Move the phone |
| Right-click the bezel | Screenshot, record, change frame, iPhone Duo pose |
| `Ctrl+Alt+F` | All frames off or on |
| `Ctrl+Alt+1` to `Ctrl+Alt+6` | iPhone Duo poses (while an emulator is in front) |

Every option, hotkey and command is in the [reference](docs/reference.md).

---

Frames from [device-frames-media](https://github.com/jonnyjackson26/device-frames-media) by Jonny Jackson, downloaded
at first run. Built for [JustGains](https://justgains.com), the app in the screenshots. MIT, see [LICENSE](LICENSE).
