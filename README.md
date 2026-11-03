# VR Session Recorder for GTA V Enhanced (REAL VR + Virtual Desktop)

A small Windows tool for people who play **GTA V Enhanced in VR with Luke Ross's REAL VR mod, streamed to a Quest over Virtual Desktop**.

It does two things while you play:

1. **Fixes the slow-down-over-time problem automatically.** After 15-60 minutes the Virtual Desktop stream can gradually lose frame rate (for example 120 fps sliding to 70). Restarting the stream with VD's *Shift+Win+D* shortcut (out of VR and back) brings it straight back. This tool notices the slowdown and does that refresh for you, **only when it is actually needed and only when you open the in-game map**, so it never interrupts gameplay.
2. **Records your session** (frame pacing, GPU load/memory/temperature, encoder, CPU, memory, network) and saves an offline report when you quit, so you can see what happened instead of guessing.

> Unofficial. Not affiliated with or endorsed by Virtual Desktop, Meta, Rockstar Games, or the REAL VR mod. Free and open source (MIT licence). Use at your own risk.

## Does it work?

Tested on one setup so far (Ryzen 7 9800X3D, RTX 4080 SUPER, Quest 3, VD Streamer 1.34.25, Windows 11), over several multi-hour sessions:

| Session | Slowdowns detected | Refreshes | Frame rate after refresh |
|---|---|---|---|
| 92 min | 2 | 2 | 98 -> 120 fps, 89 -> 120 fps |
| 142 min | 4 | 4 | each one back to 120 fps |

It never refreshed during healthy play, and it ignored 40+ ordinary map/pause presses when nothing was wrong. Other setups are untested: reports welcome.

## Requirements

- Windows 10/11, 64-bit (uses the .NET Framework 4.8 that ships with Windows; nothing to install).
- GTA V Enhanced with the REAL VR mod, streamed with **Virtual Desktop using its own OpenXR runtime (VDXR)**.
- An **NVIDIA GPU** for the automatic refresh (it reads GPU load from the NVIDIA driver). Recording works without one, but auto-refresh will not trigger.
- Virtual Desktop's default **Shift+Win+D** "toggle VR/desktop" shortcut.
- You open the map with **Start** on an Xbox-style controller, or **Esc** / **P** on the keyboard.

## Install and use

1. Download the latest `VR-Session-Recorder-<version>.zip` from [Releases](https://github.com/thevrpill/vr-session-recorder/releases) and extract it anywhere (for example `Documents\VR Session Recorder`).
2. Run `VR Session Recorder.exe` **before** starting GTA. Windows asks for administrator permission. That is needed to read Virtual Desktop's built-in performance events (Windows "ETW" tracing).
   - The program is not code-signed, so Windows SmartScreen may say *"Windows protected your PC"*. Click **More info -> Run anyway** if you trust it, or [build it yourself](#build-it-yourself).
   - The first launch runs a short self-check of event capture.
3. Click **Keep in background**. It sits in the system tray and starts recording when GTA starts.
4. Play normally. When a slowdown is detected the window says *"slowdown detected - will refresh when you open the map"*. Next time you press **Start / Esc / P**, it briefly takes you out of VR and back (about 3 seconds) while the game is paused. That's the refresh.
5. Quit GTA. A report opens from **Latest report**, and everything is in the `Recordings` folder.

Untick **"Auto-refresh the VD stream when it slows"** in the window to turn the automatic refresh off. Recording continues either way.

### Quick test (recommended once)

With GTA running in VR, double-click **`Test VD toggle.cmd`**, approve the administrator prompt, click OK and put the headset on. Within about 10 seconds you should drop out of VR and come back. If you don't, see [Troubleshooting](#troubleshooting).

## How the auto-refresh decides

- **Detect:** over the last 90 seconds, the median frame rate is below 90% of your headset refresh rate (108 fps at 120 Hz) **while** the GPU is less than 90% busy. That combination means the PC is being *held back*, not overloaded. A heavy scene that maxes out the GPU does not count. The first two minutes, and any time the headset is off your head, are ignored.
- **Wait for the map:** nothing happens during gameplay. On your next Start/Esc/P press it waits ~2.5 s and checks that GPU load dropped the way it does when GTA pauses. A second press (closing the menu) cancels it.
- **Refresh:** Virtual Desktop only accepts Shift+Win+D while its Streamer window has focus, so the tool brings the VD Streamer window forward, presses Shift+Win+D, waits 3 s, presses it again, gives focus back to GTA, and re-minimizes the Streamer if it was minimized.
- **Check:** 40 s later it confirms the frame rate recovered. At most one refresh per 5 minutes. If two refreshes in a row don't help, it stops for that session.

Every decision is logged to `auto-refresh.jsonl` in the session's folder.

**Background:** Virtual Desktop's developer has [attributed this gradual slowdown to a Quest 3/3S video-decoder problem](https://www.reddit.com/r/OculusQuest/comments/1uvbhkj/comment/oxb9nxr/), with restarting the stream or switching codec as the workaround. This tool automates that workaround; it does not fix the underlying cause.

## What it does and doesn't do to your PC

- **Doesn't:** install anything, add services, drivers, overlays or OpenXR layers, modify GTA, REAL VR or Virtual Desktop files, start with Windows, or use the network. No telemetry; nothing is uploaded.
- **Does:** write only inside its own folder (`Recordings`, `settings.json`, `capture-verification.json`). It reads (and copies into the session folder) `RealVR64.log` from your GTA folder and Virtual Desktop's `OpenXR.log` / `StreamerLog.txt`.
- **The only input it ever sends** is the Shift+Win+D shortcut (plus bringing the VD Streamer window to the front and back), and only when auto-refresh is on, a slowdown was detected, and you opened the map.

To remove it: right-click the tray icon -> **Stop, save and exit**, then delete the folder.

## Verify your download

Each release includes `SHA256SUMS.txt`. In PowerShell:

```powershell
Get-FileHash .\VR-Session-Recorder-v0.1.0.zip -Algorithm SHA256
```

The value should match the one in `SHA256SUMS.txt`. Releases are built by GitHub Actions from this repository's source (see `.github/workflows/build.yml`) and carry a signed build-provenance attestation. With the GitHub CLI you can confirm the zip was built here:

```powershell
gh attestation verify .\VR-Session-Recorder-v0.1.0.zip --repo thevrpill/vr-session-recorder
```

## Troubleshooting

- **Test toggle does nothing:** check that VD Streamer is running and its shortcut is still Shift+Win+D. `toggle-test-log.txt` (next to the exe) shows each step, e.g. `streamer focused; press 1 sent; press 2 sent; focus returned to previous window`.
- **It never refreshes:** open the latest `auto-refresh.jsonl`. If there are no `"press"` events, your map button wasn't seen (only Start, Esc and P are watched). If there are no `"armed"` events, no slowdown was detected. Auto-refresh needs an NVIDIA GPU.
- **"Hardware recording - VR timing unavailable":** VR frame timing needs Virtual Desktop's OpenXR runtime (VDXR) and administrator permission.
- **Antivirus warning:** the tool reads system performance events and simulates a key press, which some scanners dislike. The source is all here; you can build it yourself.

## Limitations

- Tested on one PC and one headset (Quest 3). GTA V Enhanced only.
- "VR application FPS" is the rate GTA submits frames to Virtual Desktop, not the headset's displayed frame rate.
- Short, sharp stutters (as opposed to the gradual slowdown) are recorded but not fixed. They can come from Wi-Fi or headset issues the PC can't see.

## Build it yourself

No SDK needed. Windows' built-in C# compiler is used. In PowerShell from the repository folder:

```powershell
.\tools\restore-libs.ps1   # downloads the Microsoft TraceEvent and .NET libraries from nuget.org and checks their fingerprints
.\build.ps1                # builds "VR Session Recorder.exe"
.\test.ps1 -CI             # runs the tests that don't need a GPU or headset (.\test.ps1 runs all of them)
```

Copy `VR Session Recorder.exe`, `VR Session Recorder.exe.config`, `report-template.html` and the `lib` folder together to run it.

## Credits

Made by [u/thevrpill](https://www.reddit.com/user/thevrpill/), built with help from AI (Anthropic's Claude). Uses Microsoft's TraceEvent library (MIT); see `THIRD-PARTY-NOTICES.txt`.
