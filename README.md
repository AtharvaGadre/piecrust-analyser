# Pie Crust Analyser

Cross-platform desktop Pie Crust Analyser built with Avalonia and .NET.

## Current status

This desktop port currently includes:

- loading `.spm`, `.tif`, `.tiff`, `.png`, `.jpg`, `.jpeg`
- Gwyddion-style height-map preview rendering
- straight line-profile marking and plotting
- curved centre-line marking workflow
- guided perpendicular sampling every 1 nm
- guided width/height extraction with stage-ordered box plots
- numbered reference ordering for imported files
- full 2D surface growth simulation with selectable start/end files
- polynomial gap-filling across numbered intermediate references
- multi-image stage evolution overlay
- growth quantification based on addition/removal balance
- per-section CSV export:
  - guided results
  - line profile
  - stage box plots
  - growth model simulation
  - growth quantification

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

## Notes

- The current desktop version is designed to be faster by moving the core numeric analysis to C# arrays and cached bitmap rendering.
- The React analyser remains untouched in `my-app` while this C# port is developed in parallel.

## GitHub distribution

When this folder is pushed as its own GitHub repository, the workflow in:

- `.github/workflows/pie-crust-analyser-desktop.yml`

builds downloadable desktop releases for:

- macOS Apple Silicon
- macOS Intel
- Windows
- Linux

It produces:

- a macOS `.app` bundle named `Pie Crust Analyser.app`
- self-contained Windows and Linux desktop bundles
- downloadable ZIP files on GitHub Releases for tags such as `v1.0.0`

## macOS note

The macOS release is distributed as a `.zip` containing `Pie Crust Analyser.app`.
If macOS reports that the app is damaged or cannot be opened, that is usually a
Gatekeeper/notarization issue rather than a corrupted download. A fully seamless
first-run experience on macOS requires Apple Developer signing and notarization.

For non-coders, the simplest flow is:

1. open the repository `Releases` page
2. download the ZIP that matches the operating system
3. unzip it
4. launch `Pie Crust Analyser`

No local .NET SDK install is required for these self-contained release builds.
