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
· `Ctrl+I` import audio files as pads · `Esc` clear search.

---

## Working with clips

- **Click** a pad to fire it. **Double-click** to open the editor. **Right-click** for
  everything else.
- **Editor** — a multi-track timeline where you splice clips together, drop in audio
  files from disk, and put auto-tune, pitch shifting and voice changers on any of it.
  See [The clip editor](#the-clip-editor) below.
- **Categories** — create them with `+` in the sidebar, then right-click a pad ▸ *Move
  to category*, or right-click a category to rename, recolour, set it as the default for
  new clips, or delete it (with or without its clips).
- **Rename / delete** — right-click a pad, or use the editor.
- **Several at once** — `Ctrl`-click pads to pick them out, `Shift`-click to take a
  run of them, or `Ctrl+A` to take everything on screen. A bar appears at the bottom
  with the count and what you can do to the lot: move them to a category, export them
  all into one folder, or delete them. `Delete` does it from the keyboard, `Esc` drops
  the selection. Right-clicking inside a selection acts on the whole selection.
  A plain click still just fires the pad, so nothing gets slower to trigger.
- **Import** — **Import audio as pads** at the bottom of the sidebar (or `Ctrl+I`) brings
  WAV, MP3, OGG, FLAC, M4A and WMA files in as clips. Anything Windows can decode is
  converted to 48 kHz stereo on the way in, so an imported file behaves exactly like
  something you clipped.
- **Export** — right-click a pad ▸ *Export as* ▸ **MP3 / OGG / WAV**, or the buttons in
  the editor. MP3 bitrate and OGG quality live in **Settings ▸ Export**.

---

## The clip editor

Double-click a pad to open it. What used to be a pair of trim handles is now a small
multi-track editor: the clip arrives as a **block** on lane 1, and everything you do is
arranging blocks and hanging effects off them. **Save clip** mixes the whole thing back
down onto the pad; **Save as new clip** leaves the original alone and adds the result as
another pad.

```
┌──────────┬────────────────────────────────────────┬───────────────┐
│ SOURCES  │  0:00      0:05      0:10      0:15    │ EFFECTS FOR   │
│          │ ┌──────────┐    ┌───────────┐          │ Block│Lane│Mix│
│ this clip│ │ Voice ▁▃█▅│    │ Reply ▂█▄ │          │               │
│ Reply    │ └──────────┘    └───────────┘          │ Auto-tune   ⌄ │
│ air-horn ├────────────────────────────────────────┤ Key       C   │
│ …        │      ┌────────────────┐                │ Scale     Major│
│ + import │      │ air-horn ▃▅▂   │                │ Strength  90% │
└──────────┴────────────────────────────────────────┴───────────────┘
```

### Splicing

- **Sources** on the left lists every clip in your library plus anything you have imported
  this session. Double-click one (or **Add at playhead**) to drop it on the selected lane
  at the playhead.
- **Import audio file…** pulls in a WAV, MP3, OGG, FLAC, M4A or WMA from disk. It becomes
  a source you can use as many times as you like. **Grab from the live buffer** takes the
  last few seconds of capture straight onto the timeline without making a pad first.
- **Drag** a block to move it; drop it on another lane to layer it under what is there.
  **Drag its edges** to trim. Edges snap to other blocks, to the playhead and to zero —
  turn that off with the **Snap** tick box.
- **Split** cuts the selected block at the playhead, **Duplicate** copies it, **Remove**
  takes it off the timeline. `Ctrl+Z` and `Ctrl+Y` walk back and forward through all of it.
- **+ Track** adds a lane. Lanes have their own **M**ute, **S**olo and level, so you can put
  music under a voice and balance the two.
- Each block has its own level and fade in/out in the inspector, and the fades are drawn on
  the block itself.

### Effects

The **EFFECTS FOR** switch decides what the rack underneath is editing:

| Target | What it covers |
| --- | --- |
| **Block** | Just the selected block. Where voice changing normally goes. |
| **Lane** | Everything on that lane, after the blocks are laid down — reverb over a whole verse. |
| **Master** | The finished mix, after the lanes are summed. |

Effects run top to bottom and can be reordered, bypassed with their tick box, or removed.
Every parameter is a slider you can double-click to put back to its default.

- **Voice** — Pitch shift (formants held still or dragged along), Auto-tune, Formant,
  Harmoniser, Speed
- **Tone** — EQ, Filter, Stereo
- **Character** — Distortion, Bit crush, Chorus, Flanger, Phaser, Vibrato, Tremolo,
  Ring mod, Robotise, Whisperise, Reverse
- **Space** — Delay, Reverb
- **Dynamics** — Compressor, Noise gate, Gain

### Auto-tune

Auto-tune tracks the pitch of the voice frame by frame and bends it onto the scale you
choose:

| Control | What it does |
| --- | --- |
| **Key / Scale** | Which notes it is allowed to land on — 14 scales, chromatic down to root-and-fifth. |
| **Strength** | How far towards the note it pulls. 100% is all the way. |
| **Retune speed** | How quickly it gets there. **0 ms is the hard, obvious sound**; 100 ms or so just tidies up. |
| **Transpose** | Moves everything by whole semitones, in key or not. |
| **Vibrato** | Puts a wobble back on top, for when hard tuning has flattened the life out of it. |
| **Note detection** | How sure it has to be before it treats a frame as a note. Lower it for a quiet or noisy clip. |
| **Keep formants** | Leaves the voice sounding like the same person while the note moves. Turn it off for chipmunk. |

Pitch tracking is YIN on a 12 kHz decimation of the mono sum, and the bend itself is done
by the phase vocoder in `Audio/FxProcessor.cs`, one pitch ratio per STFT frame.

### Presets

The preset box drops a whole chain onto whatever the rack is pointed at:

| Group | Presets |
| --- | --- |
| **Voice** | Chipmunk · Helium · Demon · Giant · Gremlin · Robot · Dalek · Alien · Ghost |
| **Tuned** | Hard tune · Gentle tune · Choir |
| **Broadcast** | Telephone · Walkie-talkie · Megaphone · Announcer |
| **Space** | Underwater · Cave · Stadium |
| **Speed** | Nightcore · Slowed + reverb |
| **Character** | Cursed · Old film |
| **Repair** | Clean up |

Applying one replaces what is in that chain, so you can then take it apart — most of them
are only three or four effects.

### Playing it back

**Play mix** renders the arrangement and plays it from the playhead through your monitor
device, never the virtual cable, so you can work while a call is going on. Click the ruler
to move the playhead. `Space` plays, `S` splits, `Delete` removes, `Ctrl+I` imports, and
`Ctrl+scroll` zooms.

Rendering happens in the background and every block is cached, so only what you actually
changed is recomputed. Long clips with auto-tune or a harmoniser on them take a moment the
first time.

### What gets saved

Saving bakes the arrangement down to one pad-sized piece of audio — the timeline itself is
not kept, so reopening the editor starts again from the saved result. If you want to keep
tweaking, use **Save as new clip** and keep the original as your master.

The mix is turned down as a whole if it would clip, rather than being clipped, so stacking
a harmoniser and a reverb costs you level rather than distortion.

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
| **Storage ▸ Run audio self-test** | Checks capture, resampling, both encoders, the output path, the pitch tracker, auto-tune and the mixdown, and plays a short beep. Run this first if something isn't working. |

---

## Staying up to date

VoidClip watches [its own repository](https://github.com/Frosty2904/Voidclips) and can
update itself. **Settings ▸ Updates** has the controls; both are on by default.

- It checks shortly after launch and every 6 hours after that (adjustable, 1 h – 2 days).
- If a release carries a `VoidClip.exe`, the app downloads it and swaps it in beside the
  running copy. **It never restarts underneath you** — a banner offers a restart, and
  the new build takes effect on its own next time you open VoidClip.
- If a release has no exe attached, it says so and links you to the release instead.
- If there are new commits on `main` with no release cut for them, it tells you that too
  — it knows the commit it was built from, so it can count exactly how far behind it is.
- Dismissing a release banner skips that version. **Stop skipping** in the Updates tab
  brings it back.

Every build knows its own version, commit and build time, shown at the top of the
Updates tab and in the self-test report.

### Cutting a release (for whoever maintains the repo)

The in-app updater downloads a release asset, so a release needs the exe attached to it.
[`.github/workflows/release.yml`](.github/workflows/release.yml) does that for you:

```sh
git tag v1.1.0
git push origin v1.1.0
```

That builds the single-file exe on a Windows runner, smoke-tests it, and creates the
release with `VoidClip.exe` attached. You can also run the workflow by hand from the
Actions tab and give it a tag.

The tag must contain a version number (`v1.1.0`, `release-1.2`). The workflow stamps that
version into the exe so that, once installed, the app correctly reports itself as current
— a release tagged with a name that has no version in it (like the existing `audio` tag)
can't be compared against, and won't be offered as an automatic update.

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

The build stamps the current git commit and a timestamp into the assembly, which is how
the updater works out how far behind the repo a build is. CI passes the commit explicitly
with `-p:GitCommitOverride=<sha>`.

### Layout

| Path | What's in it |
| --- | --- |
| `Audio/AudioCore.cs` | Ring buffer, device enumeration, DSP (resample, normalise, fades, silence trim, peaks) |
| `Audio/CaptureService.cs` | WASAPI loopback/input capture. Pads wall-clock silence, because WASAPI sends nothing while a device is idle — without this, "the last 15 seconds" would skip the quiet parts |
| `Audio/PlaybackEngine.cs` | One WASAPI output per device, each with its own mixer, so a clip can hit the cable and your headphones at once |
| `Audio/AudioFiles.cs` | WAV I/O, LAME MP3 encoder, Vorbis OGG encoder |
| `Audio/VoiceFx.cs` | The effect catalogue: every effect, its parameters and their ranges, plus the scales and the preset chains. The editor builds its whole UI from this |
| `Audio/FxProcessor.cs` | The DSP itself — phase vocoder (pitch, formants, robot, whisper), YIN pitch tracking, auto-tune, and one method per effect |
| `Audio/Mixdown.cs` | Renders an arrangement: block → its chain → its lane → the master, with a per-block cache |
| `Models/EditorProject.cs` | Tracks, blocks, sources and effect chains |
| `Controls/TimelineView.cs` | The arrangement view — lanes, blocks, dragging, trimming, snapping |
| `Controls/WaveformView.cs` | Custom-drawn waveform — the thumbnails on the pads |
| `Services/` | JSON persistence, global hotkeys (`RegisterHotKey`), run-at-login, audio self-test |
| `Services/UpdateService.cs` | GitHub release/commit checks, download, and the exe swap (Windows won't overwrite a running exe, but it will let it be renamed, so the live file is moved aside and deleted on the next launch) |
| `Themes/Obsidian.xaml` | The whole palette and every control style |

Everything internal runs at 48 kHz stereo float; clips are stored as 16-bit WAV and
converted only on export.

### Dependencies

[NAudio](https://github.com/naudio/NAudio) · [NAudio.Lame](https://github.com/Corey-M/NAudio.Lame)
· [OggVorbisEncoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder)
