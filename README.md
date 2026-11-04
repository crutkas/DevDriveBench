# Dev Drive Bench

A WinUI 3 showcase that races real developer workloads on the **system drive** and a **Dev Drive**
and shows, while they run, what the Dev Drive actually buys you.

![Dev Drive Bench](docs/screenshot.png)

One number up top (median Dev Drive advantage across real workloads), a live trace of disk I/O on
the drive under test, the whole run as a time strip, and one row per test: the system drive lane
over the Dev Drive lane on a shared scale, labeled directly. No legends, no paragraphs.

## Run it

Requirements: Windows 11 with a Dev Drive, .NET 10 SDK, [WinApp CLI](https://github.com/microsoft/winappcli) 0.7+, Developer Mode.

```powershell
cd src\DevDriveBench
winapp run .
```

For a Release build, run from the repository root (use `ARM64` / `arm64` instead of
`x64` on an ARM64 machine):

```powershell
dotnet build .\src\DevDriveBench\DevDriveBench.csproj -c Release -p:Platform=x64 -r win-x64
winapp run .\src\DevDriveBench\DevDriveBench.csproj -c Release --arch x64 --no-build --detach
```

The app is packaged: use `winapp run` to register its package identity and launch it,
not the raw build-output EXE. Once registered, **Dev Drive Bench** is also available
from Start. Release builds keep trimming disabled because WinRT collection
marshalling and recording JSON serialization require reflection metadata.

| Key | Action |
|-----|--------|
| `F6` | Prepare |
| `F5` | Run (Live) or play (Replay) |
| `Esc` | Stop |
| `F11` | Full screen (stage mode) |

- **Prepare** does every selected test's one-time setup now (seed clones, package downloads, the
  eShop restore), stages the package caches on both drives, and keeps it all, so **Run** goes
  straight to measuring. Rows show progress, then `ready`; the button becomes **✓ Ready**. Runs
  reuse the prepared fixtures until something that shapes them changes (tests, git fixture size, a
  tool version, the drives), and then Prepare shows again. Run without Prepare still works and
  prepares as it goes. Prepared fixtures are deleted when the app closes, or from the gear menu.
- **Packages** (Optimal drive | C drive) picks where package caches live. **Optimal drive** (default)
  just happens: each drive's run uses package caches on that drive, so C: is at its best on C: and
  the Dev Drive at its best on the Dev Drive. **C drive** keeps the Dev Drive run's packages on C:
  ("moved the code, not the packages"). The toggle sets `npm_config_cache`, `NUGET_PACKAGES` (plus
  NuGet's HTTP, plugins and scratch paths) and `CARGO_HOME` for the benchmark's own child processes
  only; your account and machine environment variables are never changed. Each row names the variable
  and drive next to its lane. Run once on **C drive**, flip to **Optimal drive**, and run again: the
  rows then show all three scenarios top to bottom (everything on C:, code on the Dev Drive with
  packages on C:, everything on the Dev Drive), and the headline and each row add the other run's
  advantage.
- **Live** runs the benchmark on this machine. **Runs** (default 1) is iterations per drive per test;
  raise it to gather steadier data.
- **Replay** plays a recorded run back at its original pace (1×, or 2–8× to skip ahead). Use it
  if the stage machine has no Dev Drive, no network, or no time. The app ships a real recorded
  sample; every Live run you make is added to the list. A run recorded after Prepare carries its
  preparation, so Replay has the same Prepare → Run flow, and a run recorded after one with the
  packages on the other drive carries that comparison.
- The gear menu picks tests, the git fixture size, and the Dev Drive (when there are several).

## What is measured

| Test | What is timed | Needs |
|------|---------------|-------|
| git clone | `git clone --no-hardlinks` of a local 15,000-file repo (1,200 with Quick) | git |
| npm ci | offline `npm ci` of microsoft/vscode-eslint; `node_modules` on the code drive, `npm_config_cache` on the packages drive | npm, network for first prep |
| dotnet build | `dotnet build` of dotnet/eShop on .NET 10 (`eShop.Web.slnf`: web app, services, Aspire AppHost and tests; pinned commit); output on the code drive, `NUGET_PACKAGES` on the packages drive, build servers cold each run | dotnet, network for first prep |
| cargo build | offline `cargo build` of microsoft/edit; `target/` on the code drive, `CARGO_HOME` (registry, crate unpacking) on the packages drive | cargo, network for first prep |

Tests whose tool is not installed are unchecked; the gear menu has an **Install** button per test
that runs `winget install` for what it needs (Git; Node.js LTS; .NET 10 SDK; for cargo, VS 2022
Build Tools with the C++ workload — plus the ARM64 toolset on ARM64 — then rustup and
`rustup default stable`). Installs request the OS architecture (`--architecture arm64` on ARM64),
so an ARM64 machine never gets an emulated toolchain by default. Installers may ask for elevation.
The app re-reads PATH afterwards, so the test is ready without a restart. A test that fails (no
network, low disk) shows as a skipped row with the reason. Hover the reason for full
failure details; saved recordings retain the command output for diagnosis.

The .NET fixture inherits the machine's and user's configured NuGet sources instead
of eShop's checked-in source overrides. Corporate feeds work when configured; a normal
public-feed setup works unchanged. Preparation does not probe nuget.org directly or
change your NuGet configuration. Measured restores remain offline against staged caches.

**Methodology.** Each test prepares its fixture once (Prepare, or the start of a Run), then runs N
iterations on the system drive and N on the Dev Drive. Each iteration copies the source into a fresh
folder on the drive under test right before it is timed; only the tool invocation itself is timed.
Package caches are staged at Prepare, like a machine's long-lived caches, with a separate copy for
each scenario (C: for the system-drive run, and C: and D: for the Dev Drive run), so no scenario
reads a cache another one has already warmed. Caches a run can change (cargo unpacks crates into
`CARGO_HOME`; npm writes logs into its cache) are re-staged from the seed after every run, once the
result is on screen, so every run starts from the same contents. The eShop NuGet cache is only
read (the untimed offline restore finds every package already there, and the SDK's background
workload-update check is turned off for the benchmark's processes), so it stays as staged.
With N ≥ 2 the first iteration is a discarded warm-up and the median of the rest
is reported (ticks on each lane show every raw run; the dim tick is the warm-up). With N = 1 that
single run is reported. Build servers are shut down before every timed `dotnet build` (untimed,
while that iteration's files are copied), so the second drive never inherits warm MSBuild/compiler
processes from the first. Iterations' files are deleted once the run's result is on screen (or
sooner if a drive runs low), not between timed steps. The headline is the median advantage
across workloads. Only developer workloads are run; the live disk I/O chart observes their
activity rather than running a separate synthetic disk benchmark. Older recordings remain readable,
but their removed synthetic result rows are not displayed.

The git fixture is written with
`git fast-import` (one packed bare repo, no working tree), which keeps preparation to about a second
even where on-open antivirus scanning of thousands of new files is slow.

## Data

Every Live run is saved to `Documents\DevDriveBench\Runs`:

- `*.json` — the complete recording: machine (including OS architecture), drives (file system, Dev
  Drive/trusted, bus, VHDX backing file), `fsutil devdrv query` output, plan, each tool's version
  and the architecture of the binary that really runs (git's own `git.exe`, `node.exe` behind
  `npm.cmd`, the toolchain's `cargo.exe` behind rustup's proxy), every timestamped event (phases,
  raw per-iteration values, live disk samples, 4 Hz disk/CPU activity), where the Dev Drive run's
  packages lived, a results summary, and, for a run made after Prepare, the preparation's own events
  (plus, after a run with the packages on the other drive, that run's results). This
  is what Replay plays. A tool whose architecture doesn't match the OS (e.g. x64 node on ARM64) is
  flagged next to its row.
- `*.csv` — one row per raw iteration (`test_id, drive_role, iteration, value, discarded,
  reported_value, advantage, tool, tool_version, tool_arch, os_arch, package_var, packages_drive`,
  plus machine and drive context) for spreadsheets.

Headless data gathering uses the same engine and produces the same files:

```powershell
dotnet run --project src\DevDriveBench.Cli -- --runs 5 --tests git-clone,npm-ci
dotnet run --project src\DevDriveBench.Cli -- --prepare   # stage flow: prepare, then run; prints dead time between timed steps
dotnet run --project src\DevDriveBench.Cli -- --prepare --packages both   # C drive, then optimal drive; prints all three scenarios
dotnet run --project src\DevDriveBench.Cli -- --timing <recording.json>   # same dead-time report for any saved run
dotnet run --project src\DevDriveBench.Cli -- --list      # drives, Dev Drive status, tools + architecture
```

## Layout

```
src/DevDriveBench.Engine   benchmarks, event stream, recordings, replay, stats (no UI)
src/DevDriveBench          WinUI 3 app
src/DevDriveBench.Cli      headless runner
tests/                     engine tests (dotnet test)
```

The benchmark engine is ported from [crutkas/devdrive](https://github.com/crutkas/devdrive)
(`crutkas-drive-space-analyzer` branch), MIT License.
