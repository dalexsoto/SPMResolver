# AGENTS.md

Guidance for AI agents working in this repository.

## Project purpose

`spm-resolver` is a .NET 10 CLI that takes a Swift package (local or remote) and exports XCFramework outputs suitable for .NET MAUI / macios binding workflows.

## Repository map

- `SPMResolver.Tool/Program.cs` - CLI entrypoint and error/exit-code handling
- `SPMResolver.Tool/Cli/` - request model + argument validation
- `SPMResolver.Tool/Services/ResolverOrchestrator.cs` - end-to-end pipeline
- `SPMResolver.Tool/Services/FrameworkBuilder.cs` - product discovery, scheme resolution fallback, slice build retries, XCFramework creation
- `SPMResolver.Tool/Services/DependencyExporter.cs` - output copy + `manifest.json`
- `SPMResolver.Tool/Services/ProcessRunner.cs` - subprocess execution/cancellation
- `SPMResolver.Tool.Tests/` - unit and macOS-gated integration tests
- `.github/workflows/ci.yml` - CI/CD pipeline (build, test, release publish)

## Standard workflow

1. Understand current behavior by reading `README.md` and relevant service/test files.
2. Make focused changes only in files required for the requested task.
3. Validate with:
   - `dotnet test --nologo`
4. For runtime checks, use:
   - `dotnet run --project SPMResolver.Tool/SPMResolver.Tool.csproj --no-build -- <args>`

## CI workflow notes

- CI runs on pull requests, pushes to `main`, and published releases.
- Build job packs `SPMResolver.Tool/SPMResolver.Tool.csproj` and uploads NuGet artifacts.
- Test job runs `dotnet test SPMResolver.slnx` with TRX output.
- Publish job runs only for releases and pushes artifacts to NuGet using `NUGET_ORG_API_KEY`.
- Release package version is derived from release tag (leading `v` stripped) to avoid re-publishing default `0.1.0`.

## Running the tool safely

- Always pass a dedicated `--output` directory.
- The tool may fully delete and recreate prior managed output directories (those containing `manifest.json`).
- If output is non-empty and unmanaged, the tool intentionally fails.
- For remote sources, only one of `--tag`, `--branch`, `--revision` may be used.
- Use `--keep-temporary-workspace` only when debugging; default behavior should remain delete-on-completion.
- For GitHub remote URLs, release assets are checked first by default; use `--disable-release-asset-lookup` to force source builds.
- `--disable-release-asset-lookup` is only valid with `--package-url`; using it with `--package-path` is a validation error.

## Expected outputs

- `*.xcframework` directories (source-built and/or binary artifacts)
- Optional symbol files under `*.symbols/`
- `manifest.json` with success and possible `build-failure` entries

## Operational notes

- macOS is required (`xcodebuild`, Swift toolchain, git).
- The tool prints `Temporary workspace: <path>` at startup for traceability.
- GitHub release lookup ignores default source artifacts (`Source code (zip/tar.gz)`) and only accepts archive assets that yield `.xcframework` directories.
- Release-asset staging is asset-first: when a downloaded release archive contains any `*.xcframework`, the tool stages that asset's extracted contents and exports them as-is to output under asset-named folders.
- Remote package runs log selected `tag`/`branch`/`revision` and the resolved GitHub release page URL for release-asset provenance.
- Large packages (e.g., Firebase) can run for a long time; progress is printed per product/slice.
- Known success rate: ~93% (28/30 sampled packages). Failures are typically due to complex dependency graphs or unsupported build settings.
- Build process includes automatic retries for library evolution (strict -> compatibility mode) and macro validation (strict -> skipped).
- Current long-operation guards: `git checkout` 15m, per-slice `xcodebuild build` 30m, artifact discovery 5m.
- Multiple subprocess steps are timeout-guarded; timeout messages are expected failure surfaces, not crashes.

## Retry and fallback chain (per slice)

Each attempt includes `-skipMacroValidation`; if unsupported by the installed Xcode, that same attempt is retried without the flag.
1. Attempt build with `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`.
2. For automatic/dynamic products, retry with package-default linkage if forced dynamic linkage fails.
3. If strict module stability still fails, retry with `BUILD_LIBRARY_FOR_DISTRIBUTION=NO` (dynamic first, then package-default linkage).
4. Continue across products/slices; command failure is only terminal when no artifacts are exported.

## Exit codes

- `0` success
- `1` operational/validation/build failure
- `130` canceled
