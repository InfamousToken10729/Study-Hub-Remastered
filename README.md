# Study Hub v3.0 — Remastered Native Edition

**Study Hub Remastered** is an all-in-one desktop study workspace built natively in **C# / .NET 9 WPF** for Windows (x64). Rebuilt from the ground up to replace the original single-file HTML/JavaScript (`v2`) edition, v3.0 delivers hardware-composited VSync animations, persistent SQLite + JSON storage with daily backups, a multi-channel gapless ambient audio engine, an embedded Edge WebView2 PDF reader, native PDF conversion, and a distraction-free **Zen Study Mode** with 17 animated worlds.

---

## Key Features

### Academic Workspace & Subject Management
- **Custom Course Dashboard**: Create, color-code, icon-tag, edit, and archive subjects (accessible anytime from the slide-out navigation drawer) with live file counts, deadline badges, and task progress bars.
- **Global Search (`Ctrl+K`)**: Quickly jump to any subject, PDF, note, task, link, or deadline across your entire workspace.
- **30-Day Activity Heatmap & Focus Analytics**: Track daily study activity, active session uptime, focus ratio, and your most-studied subject.
- **Full Per-Subject Toolkit (9 Dedicated Workspaces)**:
  - **Files & Folders**: Hierarchical collapsible course folders, file uploads, reading progress tracking, and one-click external or in-app opening.
  - **Split-Screen Study Mode**: Read a PDF side-by-side directly alongside your subject notebook with instant note saving.
  - **Videos**: Integrated hardware media player with play/pause/stop controls and seek bar for local lecture recordings.
  - **Recordings**: Organized subject library for audio lectures (`.mp3`, `.wav`, `.m4a`, `.flac`, `.aac`, `.wma`).
  - **Notes**: Persistent per-subject notebook with live character/word metrics.
  - **Tasks & Deadlines**: Subject checklists that feed your overall completion progress bar plus exam/assignment countdown tracking.
  - **Links**: Bookmark curated course URLs, docs, and playlists.
  - **Flashcards**: Build custom question/answer flip decks per subject.
  - **Whiteboard**: Native low-latency WPF `InkCanvas` supporting Pen, Highlighter, Stroke Eraser, color swatches, adjustable stroke width, and high-DPI PNG export.

### Embedded PDF Reader & Native PDF Converter
- **In-App PDF Viewer**: Powered by **Microsoft Edge WebView2** (`Microsoft.Web.WebView2`), automatically syncing with Light/Dark theme preferences, external launch, download copy, and Split-Screen notes.
- **Built-in File-to-PDF Converter**: Convert local **Images** (`.png`, `.jpg`, `.jpeg`, `.bmp`, `.gif`, `.webp`), **Microsoft Office Documents** (`.docx`, `.doc`, `.xlsx`, `.xls`, `.pptx`, `.ppt`), and **Text / Markdown / CSV** files into paginated PDFs stored in your workspace.

### Studio Ambient Sound Mixer (`Ctrl+M`)
- **Gapless Multi-Channel Mixer (NAudio)**: Simultaneous infinite-loop mixing with smooth gain ramping (no loop clicks or disk thrashing).
- **9 Built-In Ambient Channels**: *Zen Sanctuary Pad*, *Gentle Rain*, *Campfire Hearth*, *Bamboo Forest Wind*, *Ocean Waves & Surf*, *Airplane Cabin Drone*, *Night Sanctuary*, *Deep Brown Noise*, and *Soft Pink Noise*.
- **Custom Audio & Presets**: Import local `.mp3`, `.wav`, `.ogg`, or `.m4a` loops into limitless custom tracks, save/load custom mixes, apply built-in presets (*Rainy Study*, *Cozy Campfire*, *Zen Calm*, etc.), and control per-channel mute/volume or master volume.

### Zen Study Mode (`F10`) & 17 Animated Worlds
- **17 Native VSync Worlds (`ZenSceneCanvas`)**:
  - *Northern Lights*, *Far Space*, *Solar System*, *Moon Night*, *Cyber City*, *Nuclear Wasteland*, *Wasteland*, *City Wasteland*, *Desert Dunes*, *Lake Forest*, *Shipwreck Cove*, *Ocean Life*, *Snowy Mountain Tops*, *City Skyline*, *City Shoreline*, *Sunset*, and *Rainy Forest*.
- **Minimalist & Custom Video/Image Loops**: Switch to a distraction-free static dark backdrop (*None*) or point Zen Mode to a local folder of `.mp4`, `.mkv`, `.webm`, `.mov`, `.gif`, or image files with next/previous loop switching.
- **Zen HUD**: Monospace Digital or Analog clock, integrated Focus Timer indicator, optional floating Study View card, and quick Sound Mixer access.

### Dockable Pomodoro Timer, Themes & Particle Effects
- **Floating / Dockable Pomodoro Widget**: Adjustable Focus & Break durations, pause/resume/reset, audio completion chime, and drag-and-drop docking to **Bottom-Right**, **Bottom-Left**, or compacted into the **Top Navigation Bar**. Minimizes into a compact pill with a luminous shrinking progress bar.
- **5 Global Surface Themes + Dark/Daylight Mode**: *Dynamic Gradients*, *Deep Nebula*, *Frosted Acrylic*, *Day / Dusk Cycle*, and *Flat Minimalist*, cleanly styled across both Dark and Daylight modes.
- **8 Hardware-Composited Particle Overlays**: *Network Mesh*, *Cosmic Dust*, *Digital Rain*, *Floating Orbs*, *Lava Lamp*, *Meteor*, *Fading Stars*, and *Water Ripple* (plus *Disable* and an optional **Performance Throttle** mode in Control Center).

### Reliable Storage & GitHub Self-Updater
- **Dual Local Persistence**: Stores structured state in local **SQLite** (`study_hub.db`) mirrored automatically to JSON (`study_hub_state.json`), alongside automatic daily snapshots in `StudyHub_Backups/`.
- **In-App GitHub Updater**: Check configured GitHub repositories/releases right from Control Center and install `.exe` / `.zip` updates cleanly.

---

## Improvements Over Original HTML (`v2`) Edition

Study Hub v3.0 replaces browser-hosted HTML/CSS/Canvas execution with a compiled native Windows architecture:

| Area | Original Single-File HTML (`v2`) | Study Hub v3.0 Remastered Native |
| :--- | :--- | :--- |
| **Rendering & Frame Pacing** | Browser DOM reflows, backdrop CSS blur compositing, and 2D HTML `<canvas>` draws subject to browser tab throttling | Native WPF DirectX/MIL composition (`ZenSceneCanvas`), pre-baked DPI-aware static sky/terrain `RenderTargetBitmap` layers, frozen LUT brushes/pens, transform-only workspace particles (`0` per-frame layout invalidations), and a unified `CompositionTarget.Rendering` VSync loop |
| **Multi-Layer Backdrops** | Fixed-rate timer ticks fighting CSS transitions | Smooth delta-time (`dt`) stepping across 60Hz/120Hz+ monitors, automatic loop suspension when minimized/hidden/static, and optional 1/2-rate Performance Throttle |
| **Data & File Storage** | Browser `localStorage` / IndexedDB quotas vulnerable to browser cache wipes and heavy base64 serialization | Dedicated portable folder layout with ACID **SQLite** (`study_hub.db`) + atomic JSON mirror, direct filesystem links (`StudyHub_Files/`, `StudyHub_Sounds/`), and automatic daily backups (`StudyHub_Backups/`) |
| **Audio Mixer Engine** | Browser Web Audio API autoplay locks, glitchy MP3 loop seams, and background tab suspension | Native **NAudio** `MixingSampleProvider` running gapless pre-decoded stereo float buffers and streaming audio with smooth ramp fades and reliable background playback |
| **PDF Reader & Converter** | External browser tabs or viewer limitations; no native Office-to-PDF conversion | Embedded **Edge WebView2** PDF viewer with Split-Screen side-by-side notes + built-in converter (`PdfConverterService`) for images, Word/Excel/PowerPoint files, and plain text |
| **Ink Whiteboard** | HTML `<canvas>` mouse polling & raster strokes | Hardware-backed WPF `InkCanvas` with smooth stylus/mouse vector strokes, highlighter blend, stroke eraser, and PNG export |
| **State Compatibility** | Exports JSON state backups | Imports/migrates `v2` JSON state schemas cleanly (`FlexibleStringConverter` & schema normalization) |
| **Distribution & Updates** | Manual single-HTML file replacement | Compiles to a standalone single-file `StudyHub.exe` via `build_release.bat` with built-in GitHub Releases update checking & installation |

---

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `F10` | Toggle **Zen Study Mode** on / off |
| `F11` | Toggle **Fullscreen** window mode |
| `Ctrl + M` | Open / close **Ambient Sound Mixer** drawer |
| `Ctrl + K` | Focus **Global Workspace Search** bar |
| `Esc` | Close active overlays (PDF viewer, modals, Zen scene menu, or exit Zen Mode) |

---

## Project Structure

```text
Study Hub Remastered/
├── App.xaml / App.xaml.cs          # WPF application entry & global exception logging
├── MainWindow.xaml / .xaml.cs      # Core shell, workspace views, VSync particle loop, Pomodoro, & Zen HUD
├── Controls/
│   └── ZenSceneCanvas.cs           # High-FPS native DrawingContext renderer for all 17 Zen worlds
├── Models/
│   └── AppStateModels.cs           # App state, course/file/task/sound models, & v2-compatible JSON normalization
├── Services/
│   ├── AudioStudioService.cs       # NAudio gapless multi-track mixer & procedural ambient buffer synthesizer
│   ├── StorageService.cs           # SQLite + JSON state persistence, portable paths, & daily auto-backups
│   ├── PdfConverterService.cs      # Native Office/Image/Text to PDF converter
│   └── GitHubUpdateService.cs      # GitHub API release checker & self-updater
├── StudyHub.csproj                 # .NET 9 WPF project (Microsoft.Data.Sqlite, WebView2, NAudio)
├── build_release.bat               # One-click standalone win-x64 single-file EXE builder
└── version.json                    # App version & release metadata
```

---

## Building & Running

### Requirements
- **OS**: Windows 10 or Windows 11 (x64)
- **SDK / Runtime**: [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (for building from source; .NET 9 Desktop Runtime & Microsoft Edge WebView2 Runtime for running the single-file binary)

### Option 1: One-Click Standalone EXE (`build_release.bat`)
Double-click `build_release.bat` or run from Command Prompt / PowerShell:
```bat
build_release.bat
```
Pass `--no-launch` to build `StudyHub.exe` without launching immediately:
```bat
build_release.bat --no-launch
```
`build_release.bat` restores NuGet packages, publishes a single-file `win-x64` Release binary (`StudyHub.exe`), places it in the project root, and cleans temporary `bin/`, `obj/`, and `publish_out/` folders.

### Option 2: Build / Run via .NET CLI
```powershell
dotnet restore StudyHub.csproj
dotnet run --project StudyHub.csproj -c Release
```
