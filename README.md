# VoidClip

Instant voice-channel clipper and soundboard for Windows.

VoidClip keeps a rolling buffer of whatever your speakers are playing. When a friend
says something worth keeping, you hit one key and the last N seconds become a
soundboard pad you can fire straight back into the call.

**The app is a single file:** `dist\VoidClip.exe` (~64 MB, no installer, no .NET needed).
Put it anywhere you like and double-click it.

---

## First run

VoidClip starts capturing your default playback device immediately. The strip at the
top shows what it's listening to and how much history it holds:

```
● Hearing Speakers (Realtek)          15s / 120s
```

Hit **Ctrl+Alt+C** (or click **CLIP LAST 15s**) and a pad appears. Click the pad to
play it. That much works with zero setup — but the sound only comes out of your own
speakers. To make clips audible *to the people in the call*, do the next bit.

---

## Playing clips into Discord

Discord can only hear a microphone, so you need a virtual cable to act as one.

1. Install a virtual audio cable — [VB-CABLE](https://vb-audio.com/Cable/) is the
   usual free choice. It adds a playback device (`CABLE Input`) and a matching
   recording device (`CABLE Output`).
2. In VoidClip: **Settings ▸ Playback ▸ Main output** → `CABLE Input`.
3. Tick **Also play to a monitor device** and set it to your headphones, so you hear
   what you fire. (VoidClip skips the monitor automatically if it resolves to the same
   device as the main output — otherwise you'd just get double volume.)
4. In Discord: **Settings ▸ Voice & Video ▸ Input Device** → `CABLE Output`.

Discord now hears only VoidClip. To be heard *and* fire clips, either use Discord's
input as normal on a second machine, or route your mic and `CABLE Input` together with
a mixer app such as [Voicemeeter](https://vb-audio.com/Voicemeeter/).

Leave **Capture source ▸ Playback device to record** pointed at your *headphones*, not
the cable — that's where your friends' voices come out.

---

## Hotkeys

All of these work while Discord or a game has focus, and all are rebindable in
**Settings ▸ Hotkeys**.

| Key | Action |
| --- | --- |
| `Ctrl+Alt+C` | Clip the default length |
| `Ctrl+Alt+1` … `5` | Clip one of the five quick lengths |
| `Ctrl+Alt+Z` | Replay the last clip |
| `Ctrl+Alt+X` | Stop every playing sound |
| `Ctrl+Alt+R` | Pause / resume capture |
| `Ctrl+Alt+V` | Show / hide the window |

Individual pads get their own hotkeys: right-click a pad ▸ **Hotkey ▸ Assign**.

In-window shortcuts: `Ctrl+,` settings · `Ctrl+F` search · `Ctrl+N` new category
· `Esc` clear search.

---

## Working with clips

- **Click** a pad to fire it. **Double-click** to open the editor. **Right-click** for
  everything else.
- **Editor** — drag the red handles to set in/out points, preview the selection, crop,
  normalise, trim silence, set fades, per-clip gain, loop, category and hotkey.
- **Categories** — create them with `+` in the sidebar, then right-click a pad ▸ *Move
  to category*, or right-click a category to rename, recolour, set it as the default for
  new clips, or delete it (with or without its clips).
- **Rename / delete** — right-click a pad, or use the editor.
- **Export** — right-click a pad ▸ *Export as* ▸ **MP3 / OGG / WAV**, or the buttons in
  the editor. MP3 bitrate and OGG quality live in **Settings ▸ Export**.

---

## Settings worth knowing

| Setting | Why you'd change it |
| --- | --- |
| **Capture ▸ How far back you can clip** | The rolling buffer, 15 s – 15 min. ~11 MB per minute of RAM. |
| **Clipping ▸ Reaction offset** | Drops the last 250 ms of every clip, because you always press the key a moment *after* the funny part. Raise it if your clips end on your own laughter. |
| **Clipping ▸ Normalise / Trim silence** | Automatic clean-up on capture, so pads are all the same loudness. |
| **Playback ▸ Output latency** | Lower fires faster; too low can crackle. 60 ms suits most setups. |
| **Playback ▸ Let clips overlap** | Off means one pad at a time. |
| **Interface ▸ Background glow** | Turns the purple/red bloom up, or off entirely. |
| **Storage ▸ Run audio self-test** | Checks capture, resampling, both encoders and the output path, and plays a short beep. Run this first if something isn't working. |

---

## Where things are kept

```
%AppData%\VoidClip\
├── settings.json     all settings
├── library.json      clip names, categories, hotkeys, play counts
├── voidclip.log      diagnostics
└── Clips\            one 48 kHz stereo WAV per clip
```

Nothing leaves your machine. The rolling buffer is memory-only and is never written to
disk unless you clip it.

---

## Building from source

Needs the .NET 8 (or newer) SDK.

```sh
dotnet build                                    # debug
dotnet run                                      # debug + run
VoidClip.exe --selftest report.txt              # headless audio check

dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o ./dist
```

### Layout

| Path | What's in it |
| --- | --- |
| `Audio/AudioCore.cs` | Ring buffer, device enumeration, DSP (resample, normalise, fades, silence trim, peaks) |
| `Audio/CaptureService.cs` | WASAPI loopback/input capture. Pads wall-clock silence, because WASAPI sends nothing while a device is idle — without this, "the last 15 seconds" would skip the quiet parts |
| `Audio/PlaybackEngine.cs` | One WASAPI output per device, each with its own mixer, so a clip can hit the cable and your headphones at once |
| `Audio/AudioFiles.cs` | WAV I/O, LAME MP3 encoder, Vorbis OGG encoder |
| `Controls/WaveformView.cs` | Custom-drawn waveform — pad thumbnails and the interactive trim editor |
| `Services/` | JSON persistence, global hotkeys (`RegisterHotKey`), run-at-login, audio self-test |
| `Themes/Obsidian.xaml` | The whole palette and every control style |

Everything internal runs at 48 kHz stereo float; clips are stored as 16-bit WAV and
converted only on export.

### Dependencies

[NAudio](https://github.com/naudio/NAudio) · [NAudio.Lame](https://github.com/Corey-M/NAudio.Lame)
· [OggVorbisEncoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder)
