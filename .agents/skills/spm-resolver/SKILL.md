---
name: spm-resolver
description: Use this skill when you need to run SPMResolver in this repository to resolve a Swift package (local path or git URL), build/export XCFramework outputs for MAUI/macios binding workflows, inspect manifest results, and troubleshoot package/build failures or timeouts.
---

# SPM Resolver Skill

Use this workflow to run the local tool reliably.

## 1) Verify prerequisites

Run:

```bash
swift --version && git --version && xcodebuild -version
```

Proceed only on macOS with all commands available.

## 2) Pick source mode

Use one of:

```bash
# local package path (directory or Package.swift)
dotnet run --project src/SPMResolver/SPMResolver.csproj --no-build -- \
  --package-path /absolute/path/to/PackageOrManifest \
  --output /absolute/path/to/output

# remote package URL
dotnet run --project src/SPMResolver/SPMResolver.csproj --no-build -- \
  --package-url https://github.com/org/repo.git \
  --tag 1.2.3 \
  --output /absolute/path/to/output
```

For remote mode, use at most one ref selector: `--tag`, `--branch`, or `--revision`.
Use `--keep-temporary-workspace` when you need to preserve intermediate files for debugging.
Use `--disable-release-asset-lookup` (only valid with `--package-url`) when you need to skip GitHub Releases prebuilt artifact lookup and force a source build.

## 3) Use a dedicated output directory

- Treat `--output` as disposable.
- The tool can remove/recreate prior managed output directories.
- Do not point to directories containing unrelated files.

## 4) Expect long-running builds

The tool builds up to 4 slices per product (`ios`, `ios-simulator`, `macos`, `maccatalyst`), so large packages can take significant time. Rely on product/slice progress logs during execution.
Per-slice builds are timeout-guarded at 30 minutes, and artifact discovery is timeout-guarded at 5 minutes.
Recent sampled validation runs reached 28/30 package success, so occasional package-specific outliers are expected.
The tool prints the workspace path at startup (`Temporary workspace: ...`).
For GitHub `--package-url` inputs, the tool first checks release archives for prebuilt `.xcframework` outputs and only falls back to source builds when none are found.
When using `--branch` or `--revision` without `--tag`, the lookup targets the latest release, which may not match the requested ref; pass `--disable-release-asset-lookup` to force a source build in that case.
If a release asset contains one or more `*.xcframework` directories anywhere after extraction, the tool exports that extracted asset content to output as-is under an asset-named folder (it does not flatten output to XCFramework directories only).
For remote URLs, confirm provenance in logs: the tool prints selected `tag`/`branch`/`revision` and the resolved GitHub release page URL used for asset discovery.

## 5) Parse results from manifest

After success, inspect:

```bash
python3 - <<'PY'
import json, pathlib
m = json.loads(pathlib.Path("/absolute/path/to/output/manifest.json").read_text())
print("generatedAtUtc:", m["generatedAtUtc"])
entries = m["dependencies"]
ok = [d for d in entries if d["kind"] != "build-failure"]
bad = [d for d in entries if d["kind"] == "build-failure"]
print("artifacts:", len(ok), "failures:", len(bad))
for d in bad:
    print("FAILED:", d["name"], "-", d["error"])
PY
```

Treat `build-failure` entries as partial failures when at least one artifact exists.

## 6) Handle outcomes

- Exit `0`: at least one XCFramework artifact exported.
- Exit `1`: validation, build, timeout, or zero-artifact failure.
- Exit `130`: canceled.

When troubleshooting:
- Check `manifest.json` failure entries for specific errors.
- Verify scheme selection (tool attempts exact then fuzzy matches).
- Note automatic retries for library evolution and macro validation in logs.
- Confirm the output path is disposable/managed; unmanaged non-empty output directories intentionally fail for safety.

## 7) Common failure patterns

| Symptom | Likely cause | What to do |
|---|---|---|
| `No matching Xcode scheme was found` | Product name differs from available Xcode scheme names | Check logged scheme behavior and ensure the package actually exposes a buildable library product. |
| `BUILD_LIBRARY_FOR_DISTRIBUTION`/interface errors | Package cannot emit stable Swift interfaces under current toolchain | The tool auto-retries with `BUILD_LIBRARY_FOR_DISTRIBUTION=NO`; if that fails, inspect compiler errors in logs. |
| `-skipMacroValidation` unsupported/unknown option | Older Xcode toolchain | The tool auto-retries without the flag; upgrade Xcode if macro-heavy packages still fail. |
| `Could not resolve package dependencies` during `xcodebuild -list` | Broken or non-standard package graph metadata | Treat as package-level outlier; confirm package resolves cleanly in native SwiftPM/Xcode first. |
| `Refusing to delete a non-empty output directory...` | Output directory contains unmanaged files | Use an empty/dedicated output directory or rerun against a prior managed output directory. |
| `No release found.` when a release is expected | GitHub API rate limit/auth issue or private release access missing | Set `GITHUB_TOKEN` and retry; for private repos ensure the token has repo access. |
| `Warning: Failed to extract release asset '...'` | Release archive is corrupt, unsupported format, or contains no XCFrameworks | Check stderr for the specific extraction error; the tool will fall back to a source build automatically. |
| Extra non-XCFramework files/directories appear in output after release lookup | Matching release assets are now exported as extracted payloads | This is expected behavior; use `--disable-release-asset-lookup` to force source-built-only outputs. |
