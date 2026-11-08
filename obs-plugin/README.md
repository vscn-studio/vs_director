# VS Director OBS source

This OBS input consumes the `VSDirector.Live` shared-memory protocol published
by VS Director. It outputs bottom-up BGRA video as an asynchronous source and
48 kHz interleaved stereo PCM as source audio.

## Download packages

The `Build OBS Plugin` GitHub Actions workflow builds against OBS Studio
`32.0.4`. It runs only when manually started from **Actions → Build OBS
Plugin → Run workflow**, and publishes these installable artifacts:

- `vsdirector_obs_plugin_<version>_windows_x64.zip`
- `vsdirector_obs_plugin_<version>_linux_x86_64.tar.gz`
- `vsdirector_obs_plugin_<version>_macos_universal.zip`

Git commits, pull requests, and published releases do not start this packaging
workflow automatically. The macOS package contains both Apple Silicon and
Intel code.

## Install

Windows: extract the ZIP into the OBS Studio installation directory. The
result must contain `obs-plugins/64bit/vsdirector-obs.dll` and
`data/obs-plugins/vsdirector-obs/locale`.

Linux: extract the archive into `~/.config/obs-studio/plugins`. It contains the
per-user OBS plugin directory `vsdirector-obs/bin/64bit` and its `data`
directory. The package targets native x86_64 OBS installations. Flatpak OBS
also needs explicit access to the host temporary directory used by Vintage
Story before it can consume the shared file.

macOS: extract `vsdirector-obs.plugin` into
`~/Library/Application Support/obs-studio/plugins`. The workflow applies an
ad-hoc signature; release notarization is not required for a locally installed
OBS plugin.

Restart OBS after installation and check its log for:

```text
[VS Director] Offscreen video/audio source loaded
```

## Local build

The standalone CMake project accepts the `libobs` package from an OBS build or
SDK installation:

```bash
cmake -S obs-plugin -B obs-plugin/build -Dlibobs_DIR=/path/to/libobs/cmake
cmake --build obs-plugin/build --config Release
cmake --install obs-plugin/build --config Release --prefix /install/prefix
```

Original plugin source is licensed under the [MIT License](../LICENSE) by
HansJack (VSCN-Studio). See [third-party notices](../licenses/OBS-THIRD-PARTY.txt)
for the OBS/libobs dependency. Binaries linked with libobs are distributed
under GPL-2.0-or-later. CI packages include the plugin source, build scripts,
and interface headers in `corresponding-source/`. Keep those materials and
the included GPL text with redistributed binaries.
