# Pie Crust Analyser

Cross-platform desktop microscopy analysis tool for guided piecrust extraction, morphology quantification, and growth modelling.

## What It Does

- loads `.spm`, `.tif`, `.tiff`, `.png`, `.jpg`, `.jpeg`
- renders AFM height maps with a Gwyddion-style preview path
- supports colour mapping with auto/fixed display windows and histogram feedback
- extracts straight line profiles and guided centre-line measurements
- samples guided cross-sections at calibrated physical spacing
- measures height, width, peak separation, dip depth, roughness, and height-to-width ratio
- builds stage-ordered box plots for:
  - height
  - width
  - height-to-width ratio
- exports CSV files for:
  - guided results
  - line profile
  - stage box plots
  - growth model
  - growth quantification
- runs a guided growth simulation from ordered start/end references
- uses a persisted supervised learning layer to nudge the growth fit over time as more guided examples are added locally

## Calibration

- raw AFM files such as `.spm` carry physical scan calibration directly
- processed image files such as `.tiff` try to inherit calibration from a sibling raw AFM file with the same basename
- if no matching raw calibration source exists, the app falls back to image-based defaults

This matters because all profile distances, corridor widths, and guided measurements are reported in physical units, not just pixels.

## Growth Model

The current desktop workflow focuses the simulation on the guided piecrust region:

- choose ordered reference files
- choose a start reference
- choose an end reference
- run polynomial surface evolution across the ordered references
- blend that evolution with a supervised bimodal growth model trained from accumulated guided examples stored locally

The growth view is profile-first, so the tab focuses on the evolving guided morphology rather than unrelated full-image background.

## Stage Statistics

The stage export includes per-stage summary values:

- `height_mean_nm`
- `height_std_nm`
- `width_mean_nm`
- `width_std_nm`
- `height_to_width_ratio_mean`
- `height_to_width_ratio_std`

## Build

```bash
cd piecrust-analyser-csharp
DOTNET_CLI_HOME=/tmp dotnet restore
DOTNET_CLI_HOME=/tmp dotnet build
```

## Run

```bash
cd piecrust-analyser-csharp
DOTNET_CLI_HOME=/tmp dotnet run
```

## GitHub Releases

This repository contains a GitHub Actions workflow at:

- `.github/workflows/pie-crust-analyser-desktop.yml`

Tagging a release such as `v1.0.3` builds downloadable desktop artifacts for:

- macOS Apple Silicon
- Windows x64
- Linux x64

The workflow also attempts a macOS Intel build. If GitHub's Intel macOS runner is unavailable, the release still publishes the Apple Silicon, Windows, and Linux downloads instead of failing the whole release.

Release assets are published as ZIP files on GitHub Releases. The macOS ZIP contains `Pie Crust Analyser.app`, which users can move into `Applications`, launch from Spotlight, and pin to the Dock.

## macOS Note

The macOS app bundle is packaged correctly for download, but a completely seamless first-run experience for other users still requires Apple Developer notarization.

If macOS flags the app before notarization, that is usually Gatekeeper behavior rather than a corrupted build.

## Development Note

The React analyser remains untouched in `my-app`. This Avalonia/.NET desktop app is the actively packaged desktop version.
