# VS Director

[简体中文](README.md) | English

VS Director is a camera, replay, and scene creation mod for [Vintage Story](https://www.vintagestory.at/). It provides a dockable workspace for composing shots, editing camera paths and recorded scenes, creating visual effects, and exporting video through OBS Studio or FFmpeg.

The mod supports local client workflows and optional server features for permissions, remote camera access, and recording coordination. English and Simplified Chinese interfaces are available.

## Requirements and installation

| Component | Requirement |
| --- | --- |
| [Vintage Story](https://www.vintagestory.at/) | 1.22.3 minimum; the same package also targets 1.23.0-pre.1 |
| [VSImGui](https://mods.vintagestory.at/imgui) | 1.2.7, installed separately |
| [FFmpeg](https://ffmpeg.org/download.html) | Required for direct MP4 recording and GIF/APNG export |
| [OBS Studio](https://obsproject.com/download) | Required for OBS recording; the companion source plugin targets OBS Studio 32.0.4 |

The ImGui workspace uses an installed system Chinese font to supplement missing characters; no CJK font is bundled. Windows uses Microsoft YaHei or another available Chinese font, macOS uses its Chinese system fonts, and Linux uses Fontconfig to locate a font with Chinese coverage. Systems without Chinese fonts need one installed. FontSettings is optional; its selected font and size are preserved, and Director restores missing characters and editor icons after font rebuilds.

Install the VS Director mod ZIP and VSImGui in the client's Mods directory. Open the Director panel with **F9**; keyboard bindings can be changed in the game's controls settings. For the companion [OBS plugin](https://mods.vintagestory.at/vsdirectorobsplugins), see the [installation and build instructions](obs-plugin/README.md).

The 1.23 adaptation covers replay serialization and block entities, camera FOV, offscreen terrain visibility, native text inputs, and workspace picking. The [compatibility checks](#build-and-validation) exercise the same DLL against both game versions. Complete recording/export workflows and multiplayer sessions still require in-game verification. Later 1.23 builds require separate validation.

### Client and server operation

VS Director can run on the client without a server installation. Personal cameras, camera paths, following loaded entities, the creation workspace, replay recording/playback, and video export are available locally. Recordings contain only the chunks, entities, and events received by the client. Moving a local camera does not load distant terrain from the server.

World recording follows the player and director camera within the configured snapshot radius. Newly observed chunks are captured in bounded batches, with a snapshot before the first intercepted block write when needed. This preserves trees and other terrain encountered after leaving the initial recording area, including partially completed changes. Replays retain each chunk's first observation time; terrain before that observation remains unknown. Older recordings made with a fixed capture area cannot reconstruct changes outside their saved chunks. New recordings use replay schema 26 and require a compatible reader.

Installing VS Director on the server enables server-managed permissions, remote camera chunk loading and replay preloading, remote tracking and sound relay, and actor performance recording that temporarily changes the player's position or collision dimensions. When the server provides the Director channel, server permissions apply; a pending permission response does not grant unrestricted local access.

The package is universal and declares `requiredOnServer: false`. Servers that install VS Director require connecting clients to install it as well. Server installations retain the declared VSImGui dependency.

## Features

- **Camera composition:** saved camera positions, free cameras, and player or entity follow cameras, with personal camera libraries and optional remote chunk loading.
- **Camera animation:** keyframes, editable curves, trimming, easing, automatic Bezier interpolation, looping, reverse playback, and speed controls.
- **Replay capture and playback:** client recording of entities, animations, held items, particles, positional audio, loaded chunks, and block changes; playback supports pause, seeking, speed adjustment, and first-person or third-person actor views.
- **Replay editing and presets:** a searchable preset browser that creates editable tracks and keyframes for follow rigs, tracking shots, orbits, camera movement, shake, FOV, time effects, color grading, and title cards.
- **Scene creation:** actor, particle, and blueprint editing in a dockable workspace with a shared timeline and contextual property inspectors.
- **Recording and export:** replay video export and live offscreen recording from fixed, path, or follow cameras, using OBS or direct FFmpeg MP4 output. The companion OBS source receives offscreen video and spatial camera audio.
- **Voice integration:** a public [DirectorVoiceApi](src/Voice/DirectorVoiceApi.cs) for spatial PCM audio capture, replay, and export.

Each new replay preset creates one collapsible timeline group. Expand it to edit individual elements, or disable/delete the group to manage the entire preset. Weather, sky and composition effects are timeline elements that leave the scene defaults intact. Ctrl+Z and Ctrl+Shift+Z undo and redo replay edits throughout the workspace; text fields retain their own text undo. Existing ungrouped edits remain editable. Replay title cards and fade overlays currently appear in the on-screen preview and are not included in offscreen export.

## Creation workspace

The default layout places **Objects** and **Content** on the left, **Tools** and **Properties** on the right, the game viewport in the center, and the timeline below. Windows can be moved and docked. Optional View, Bones, IK, and Actions windows initially float.

Content navigation retains each object's section, selection, and scroll position. Search locates native and module parameters. Layout version 4 applies a one-time migration of the creation docks; subsequent opens preserve the user's arrangement.

### Particle editing

Select an emitter or child effect in Content to edit emission and timing, shape and binding, motion, color and size, material, geometry, environment, evolution, or child events. The inspector displays the selected parameter group. Effect settings, presets, and source resources have separate entries; curves, gradients, and JSON editors open on demand. Batch scope is explicit and resets when switching objects.

### Blueprint import and preview

**Add → Projection** imports Buildwright `.vsbp` files or native block-schematic `.json` files from the local library, game/mod templates, or a selected file. Imports copy the source into the project and do not place blocks in the world.

Select a projection and choose **Preview projection** from its context menu to open an isolated rotating view. Drag to orbit, middle-drag to pan, and scroll to zoom. The inspector controls lighting, background, and GIF/APNG export. Exit preview from Tools to restore the previous timeline position.

World region capture saves loaded blocks to the Buildwright library without placing blocks. In the capture tool, right-drag to define the corners, release to confirm, left-click to clear, and press Esc to leave.

### Blueprint editing and animation

Content organizes blocks, regions, growth, material layers, particles, physics, preview, and output. The block list supports name and coordinate filtering, Ctrl/Shift selection, viewport picking, and move/rotate/scale handles, including in isolated preview. Editing operations include copying, deleting, restoring, integer region export, and extracting or cutting selections into independent Director presets.

Structures support complete projection rotation and mirroring, editable particle attachments, and these animation controls:

- Appearance, seeded fill, fall/rain, and two waves with six selectable directions.
- Five glass replacement layers and four priority edge layers using native block resources.
- Edge particles on the twelve block edges, with count, speed, interval, lifetime, color, rainbow, glow, and outline controls.
- Grouped structure parameters, resource selection keys, and optional per-block transform, visibility, and override tracks.

Embedded particle schemes use the shared particle editor and simulator. Beam, lightning, and trail effects attach as child effects. Automatic torch, fire, and firepit particles follow edited or deleted source cells and use saved block variants; they do not reproduce live fuel or weather simulation. Disabling per-block keyframes preserves their data. Initial transform keys use absolute local values; additive mode is available explicitly.

### Physics, limits, and export formats

Visual physics uses a fixed 120 Hz simulation with terrain collision, separation, drag, spin, impact, gravity, bounce, seek snapshots, and cancellation. It does not simulate block-to-block stacking.

| Resource | Limit |
| --- | --- |
| Structure physics | 6,000 source/copy cells per structure |
| Particles | 6,000 per effect and 24,000 per project, including outlines |

Geometry rebuilds and world capture are processed across frames; the block list is virtualized and cached. Large animated structures can require substantially more resources than static structures.

Native VSBP/JSON exports contain static blocks at integer positions. Individual display rotation or scale is rejected rather than silently discarded. Director structure presets preserve source assets, cell overrides, clips, tracks, particle schemes, and textures. External particle binding targets must belong to the exported hierarchy, and performance clips must be baked before export. Existing version-1 projections require an explicit upgrade; opening a project does not migrate them automatically.

Embedded entities and surface decorations are retained but not displayed. Missing or unsupported meshes are reported and prevent block export. Native liquids use a static preview material without the world's refraction and wave shaders. Visual fidelity for specific game and mod resources requires in-game verification.

## Build and validation

Development requires the .NET 10 SDK, a Vintage Story installation, and extracted VSImGui 1.2.7 assemblies. Build release packages against the **Vintage Story 1.22.3 SDK** to retain a single binary for both supported game versions. The `game: 1.22.3` dependency in `modinfo.json` remains the minimum version.

```powershell
dotnet build VSDirector.csproj -c Release -p:VintageStoryPath="path/to/1.22.3" -p:VSImGuiPath="path/to/vsimgui_1.2.7"
```

Release packages are written to `artifacts/`. To build without packaging, add `-p:PackMod=false`. The `VINTAGE_STORY` and `VSIMGUI` environment variables can also provide the dependency paths.

Run the core tests from the repository root:

```powershell
dotnet run --project tests/VSDirector.CoreTests/VSDirector.CoreTests.csproj -c Release
```

Run the game compatibility checks with both installations:

```powershell
./tools/TestGameCompatibility.ps1 -BaselineGamePath 'path/to/1.22.3' -NewGamePath 'path/to/1.23.0-pre.1' -VSImGuiPath 'path/to/vsimgui_1.2.7'
```

The script builds once, checks native member references, and runs PatchProbe with each game's assemblies and assets in separate directories. Logs are written to `artifacts/compat*-probe.log`. The checks do not deploy the mod or replace manual gameplay validation.

To run the ImGui font regression checks independently, run PatchProbe with `--imgui-fonts` (pass the same `VintageStoryPath` and `VSImGuiPath` build properties). These checks cover Chinese glyphs, the selected default font, atlas rebuilds, and font scope restoration. In-game validation should also open the Chinese workspace without FontSettings, then repeat with FontSettings installed and change its font selection.

## Head tracking and head-motion elements

On Windows, select **FreeTrack 2.0 Enhanced** as OpenTrack's output. Open **Settings → Interface → Head tracking settings**, enable tracking, and apply. Tracking is disabled by default. Keypad **\*** opens settings, **5** recenters, and **0** enables or pauses tracking; bindings can be changed in the game's controls settings. Configuration is stored separately in `vsdirector-headtracking.json`.

The integrated controls include yaw, pitch and roll gains, translation gains and limits for each axis, baseline offsets, smoothing, first-person model hiding, head-controlled crouching (hold or toggle, with axis and hysteresis), and threshold or continuous lean-to-zoom with mouse sensitivity, smoothing and vignette controls. Crouching and first-person model options apply to ordinary gameplay. If Zoom Button is installed, its zoom configuration is used. Neither OpenTrack nor Zoom Button is bundled.

In a replay, choose **Add element → Head motion** to create a collapsible group with camera and head-motion children. Open the motion keyframe and select **Record / replace motion**. Activate a director camera and tracking first; use Keypad **5** to recenter. Recording seeks to the keyframe, pauses replay playback and captures movement in real time against a fixed camera baseline. **Stop and save** saves the motion and, if the camera child is empty, adds editable camera keys for the recorded interval. A retake preserves existing camera edits. **Cancel recording** keeps the previous take. Expand the group to edit its children, or mute/delete the entire group. A saved clip supports timeline placement, source in/out trimming, playback rate, strength, independent translation/rotation/zoom toggles, and undo/redo. Changing timing in the motion keyframe settings also resizes its matching two-key camera interval; more elaborate camera animation remains independent. A connection loss or game pause stops and saves the captured portion. Switching replay or starting export cancels an unfinished take.

Recorded motion uses the camera's local axes, including Vintage Story's native 180-degree pitch convention. Disable the camera child to layer motion over another camera, orbit or follow track. Older standalone recordings are wrapped in groups without changing their samples or inventing a camera baseline; their empty camera child can be populated using the camera row's add-key action. Replay, seek, preview and export evaluate stored samples without live tracker input, including on systems without Windows or OpenTrack. Live tracking applies to normal gameplay and director cameras outside replay, and while recording a head-motion element.

When the standalone VS-6DOF mod is installed, it retains ordinary first-person control and VS Director takes over director cameras to avoid double offsets. Director's settings shortcut becomes **Ctrl+Keypad \***. Standalone settings remain separate. Hardware tracking, mod combinations and gameplay should be checked in-game after installation.

This integration adapts [VS-6DOF](https://github.com/bjfishman/VS-6DOF), credited to **DF**, including the [TGU-HansJack fork](https://github.com/TGU-HansJack/VS-6DOF), under [CC0 1.0](licenses/VS-6DOF-CC0.txt). Source notices and the full license are included in the mod ZIP. VS Director's original code remains MIT-licensed.

## Authors and contributors

| Author | Contributions |
| --- | --- |
| [HansJack](https://github.com/TGU-HansJack) | Project author and maintainer; camera, replay, creation, and recording systems. |
| [HanosThePlacid](https://github.com/HanosThePlacid) | Replay camera preset library, preset browser, supporting timeline features, and camera/timeline usability improvements ([PR #2](https://github.com/vscn-studio/vs_director/pull/2)). |

See the [contributor history](https://github.com/vscn-studio/vs_director/graphs/contributors) for additional contributions. Pull requests, bug reports, and documentation improvements are welcome. Read the [contributing guide](CONTRIBUTING.md) for code style, localization, saved data compatibility, testing, and voice integration requirements.

## License and acknowledgments

Original VS Director code, interface, and documentation are licensed under the [MIT License](LICENSE). Copyright notices are retained in [COPYRIGHT.txt](COPYRIGHT.txt).

Third-party components and adapted resources retain their respective licenses and attribution. See the [third-party notices](licenses/THIRD-PARTY.txt) for details, including FontSettings, Tabler Icons, InGameDevTools, BBS-FS, Mine-imator, and Fieldwright via Buildwright.

The companion OBS plugin's original source is MIT-licensed. Binaries linked with libobs are distributed under GPL-2.0-or-later; see the [OBS distribution terms](licenses/OBS-THIRD-PARTY.txt) and [plugin documentation](obs-plugin/README.md).

Game and third-party models, textures, animations, and audio remain subject to their respective rights holders' terms. Importing or exporting them with VS Director does not place them under this project's MIT License.

Vintage Story is a trademark of Anego Studios. VS Director is an independent mod and is not affiliated with or endorsed by Anego Studios.
