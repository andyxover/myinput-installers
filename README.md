# myinput-installers
Installers for MyInput (Traditional Chinese pinyin input) and Three-Finger Drag, Windows x64

## Three-Finger Drag: which download

| Download (see Releases) | Optimized for |
|---|---|
| `ThreeFingerDrag-Setup-x64.exe` (the original app, with setup wizard) | ASUS ProArt 14 Spark |
| `ThreeFingerDrag-rebuilt-x64.exe` (rebuilt, single exe) | ASUS ExpertBook A7 |

The original app was designed and tuned on the ASUS ProArt 14 Spark. On the ASUS ExpertBook A7
its drag was far too slow, so it was rebuilt there.

## Three-Finger Drag source (ASUS ExpertBook A7)

`ThreeFingerDrag.cs` is the rebuild of Three-Finger Drag, designed and tuned on, and for, the
ASUS ExpertBook A7 (model A7406CMA). The original `ThreeFingerDrag-Setup-x64.exe` is not built
from this file.

Tuned against:

| | |
|---|---|
| Touchpad | HID `ASCO1D82` (VID 093A, PID 4F09), 20629 x 13007 counts, about 140 reports per second |
| Display | 2880 x 1800 at 200% scaling |
| Windows touchpad speed | `CursorSpeed` 6 |

Settings chosen on that machine. They are the built-in defaults, and are written to
`%APPDATA%\TrackpadGestures\settings.ini` the first time the program runs:

```ini
[drag]
enabled=1
release_delay_ms=100
speed=2.5
acceleration=1.0
start_delay_ms=40
start_distance_mm=1.0
```

On another computer, start from these and change `speed` (allowed range 0.1 to 10) from the
tray icon: Edit settings, then Reload settings.

Build, with the C# compiler that ships with Windows (no SDK needed):

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:ThreeFingerDrag.exe ThreeFingerDrag.cs
```

Windows setting it needs: Settings > Bluetooth & devices > Touchpad > Three-finger gestures,
set Swipes to Nothing.
