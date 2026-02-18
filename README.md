# SPMResolver

`spm-resolver` is a .NET 10 global tool that resolves Swift packages and produces XCFramework outputs suitable for .NET MAUI / macios binding projects.

## Prerequisites

- macOS
- .NET SDK 10
- Swift toolchain available on `PATH`
- Git available on `PATH`
- Xcode command line tools (`xcodebuild`) available on `PATH`

## Install (NuGet)

Package: https://www.nuget.org/packages/SPMResolver/

```bash
dotnet tool install --global SPMResolver
dotnet tool update --global SPMResolver
```

## Install from local source (development)

```bash
dotnet pack src/SPMResolver/SPMResolver.csproj -c Release -o ./nupkg
dotnet tool install --tool-path ./tools SPMResolver --add-source ./nupkg
```

When installed with `--tool-path`, invoke it as `./tools/spm-resolver`.

## Usage

### Resolve from a local package

```bash
spm-resolver \
  --package-path /path/to/MyPackage \
  --output /path/to/exported-xcframeworks
```

`--package-path` can also point directly to `Package.swift`.

### Resolve from a remote package URL

```bash
spm-resolver \
  --package-url https://github.com/example/MyPackage.git \
  --tag 1.2.3 \
  --output /path/to/exported-xcframeworks
```

Supported remote ref selectors:
- `--tag`
- `--branch`
- `--revision`

Only one ref selector can be provided at a time.
All ref selectors are optional. When none is provided, the tool clones the repository's default branch and release lookup (if enabled) checks the latest GitHub release.
Remote ref selectors are valid only when `--package-url` is used.

Optional flags:
- `--keep-temporary-workspace`: preserves the temporary workspace instead of deleting it at process end (useful for debugging).
- `--disable-release-asset-lookup`: skips GitHub Releases artifact lookup and always builds from source. Only valid with `--package-url`.

## Build Behavior

For GitHub-hosted `--package-url` inputs, the tool first checks GitHub Releases for non-source archive assets (`.zip`, `.tar`, `.tar.gz`, `.tgz`) and extracts them locally.
GitHub default source assets (`Source code (zip)` / `Source code (tar.gz)`) are ignored.
If no XCFrameworks are found in release assets, the tool falls back to the normal source build flow below.
When an extracted release asset contains one or more `*.xcframework` directories anywhere in its uncompressed contents, that asset's extracted contents are exported to `--output` as-is (not flattened to only XCFramework directories).
When multiple matching release assets are found, their extracted contents are staged and exported together under asset-named top-level folders to avoid cross-asset overwrites.
This release lookup still runs when `--branch` or `--revision` is provided; without `--tag`, it checks the **latest** GitHub release, which may not match the requested ref. Use `--disable-release-asset-lookup` to avoid version mismatch in that scenario.
The lookup logs `Checking GitHub Release assets for <owner>/<repo>...` at the start, `Remote ref selection: ...` for selected `tag`/`branch`/`revision`, and `Resolved GitHub release page: ...` when a release is found.
When no prebuilt artifacts are found, it logs `No XCFrameworks found in release assets. Falling back to source build.`.

Each library product is compiled for up to four platform slices:

| Slice key        | Xcode destination                              |
|------------------|-------------------------------------------------|
| `ios`            | `generic/platform=iOS`                          |
| `ios-simulator`  | `generic/platform=iOS Simulator`                |
| `macos`          | `generic/platform=macOS`                        |
| `maccatalyst`    | `generic/platform=macOS,variant=Mac Catalyst`   |

Slices are skipped when the package's `platforms` declaration doesn't support them. Products with no platform restrictions are built for all four.

The tool resolves Xcode schemes in this order:
1. Exact matches for product/package scheme names (`<Product>-Package`, `<Product>`, `<Package>-Package`, `<Package>`).
2. Related non-test schemes that normalize to the same identity (including common platform-suffixed names like `Foo iOS` and `Foo-iOS`), scored by package platform compatibility.

For dynamic-preferred library products, the per-slice fallback chain is:
1. Dynamic linkage + `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`
2. Package-default linkage + `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`
3. Dynamic linkage + `BUILD_LIBRARY_FOR_DISTRIBUTION=NO`
4. Package-default linkage + `BUILD_LIBRARY_FOR_DISTRIBUTION=NO`

For static library products, only steps 2 and 4 apply.
The first successful attempt is used.
The build command also passes `-skipMacroValidation`; if the installed Xcode does not support that flag, the tool automatically retries without it.
Static library `.o` files are auto-wrapped via `libtool`.
For large packages, progress is printed per product and per slice so long-running builds remain observable.
The temporary workspace path is printed at startup (`Temporary workspace: ...`).

## Output Behavior

- **Destructive Write**: If the output directory contains a `manifest.json` from a previous run, the directory is **completely deleted** and recreated.
- **Safety Check**: If the directory is non-empty but missing `manifest.json`, the tool fails to prevent accidental data loss.
- **Manifest**: A `manifest.json` file describes all exported artifacts (schema below).
- **Artifacts**:
  - **Source-based**: Buildable library products are compiled in Release mode to `.xcframework`.
  - **Binary**: Pre-compiled XCFrameworks referenced by the package are copied to the output. For qualifying GitHub release assets, the extracted asset contents are exported as-is.
  - **Symbols**: `.dSYM` bundles and `*.bcsymbolmap` files are copied alongside their XCFrameworks.
- **Failures**: If no valid artifacts are produced, the tool exits with an error code. Failed products are still recorded in `manifest.json` with `"kind": "build-failure"` and a non-null `error`.

### Manifest Schema (`manifest.json`)

The manifest is a JSON object with `generatedAtUtc` (ISO 8601) and a `dependencies` array. Each entry:

| Field          | Description |
| :---           | :--- |
| `name`         | The name of the product or artifact. |
| `identity`     | Filesystem-safe identity string. |
| `sourceUrl`    | Source repository URL when available; otherwise `null`. |
| `kind`         | `xcframework`, `binary-xcframework`, or `build-failure`. |
| `sourcePath`   | Absolute source location used for this entry. |
| `outputPath`   | Absolute path to the exported `.xcframework` (empty for failures). |
| `version`      | Resolved version when available; otherwise `null`. |
| `revision`     | Resolved git revision when available; otherwise `null`. |
| `branch`       | Resolved branch when available; otherwise `null`. |
| `builtSlices`  | Array of platform slices built (e.g., `["ios", "macos"]`). `null` for binary artifacts. |
| `symbolPaths`  | Array of paths to associated debug symbols. |
| `error`        | Error message for `build-failure` entries; `null` on success. |

## Timeouts

Individual operations have hard timeouts. If any timeout is hit, the operation fails (non-zero exit):

| Operation                          | Timeout   |
|------------------------------------|-----------|
| `git clone`                        | 30 min    |
| `git checkout`                     | 15 min    |
| `swift package resolve`            | 15 min    |
| `swift package dump-package`       | 2 min     |
| `xcodebuild -list` (schemes)      | 5 min     |
| Per-slice `xcodebuild build`      | 30 min    |
| Artifact discovery                 | 5 min     |
| `xcodebuild -create-xcframework`  | 5 min     |

## Exit Codes

| Code  | Meaning                                               |
|-------|-------------------------------------------------------|
| `0`   | Success — at least one XCFramework was exported.      |
| `1`   | Error (missing prereqs, build failure, no outputs).   |
| `130` | Operation canceled (Ctrl-C).                          |

## Notes

- **macOS Only**: This tool depends on `xcodebuild` and is macOS-only.
- **Private Repos**: Uses system git credentials. Ensure you can `git clone` the package URL before running.
- **GitHub API auth**: Set `GITHUB_TOKEN` when release lookups need higher rate limits or private-release access.
- **Release lookup troubleshooting**: If release lookup reports no release unexpectedly, verify `GITHUB_TOKEN` is set (especially for private repos) and retry.
- **Temp workspace**: Created under `$TMPDIR/spm-resolver/<guid>/` and printed at startup.
- **Temp cleanup**: Default behavior deletes the temp workspace on completion. Use `--keep-temporary-workspace` to retain it.
- **Swift tools version**: A warning is emitted if the installed Swift major version is lower than the package's `swift-tools-version`.
- **Batch reliability baseline**: Recent validation runs reached 28/30 successful package exports; remaining outliers were package-specific project/metadata issues.
- **Outlier behavior**: Some repositories are not framework-friendly in pure SwiftPM/Xcodebuild workflows; these are surfaced as `build-failure` manifest entries and non-zero exit when zero artifacts are produced.
