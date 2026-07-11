# MasterSplinter

A SourceTree-style Git client: a **portable C++ core** behind a flat C ABI, with a **native WinUI 3**
front-end on Windows (macOS is the next target — each platform keeps its own UI). Git data comes from
shelling out to `git.exe` and parsing its output (TortoiseGit-style, no library binding), so behavior is
predictable and command-building lives in one place.

- **Phase 1** — read-only viewer: repositories, history, branches, file changes.
- **Phase 2** — diff viewer: unified/side-by-side, syntax highlighting, whitespace options, binary/image previews, compare commits & refs.
- **Phase 3** — working tree: staged/unstaged/untracked status (grouped view), working-tree diffs, manual refresh (F5/Ctrl+R) + file-watcher auto-refresh, open-in-editor / reveal-in-Explorer. Still strictly read-only — nothing writes to the repo.

## Architecture

```
git.exe → C++ core (flat C ABI) → P/Invoke → C# parsing → MVVM → WinUI 3 UI
```

| Layer | Where | Role |
|---|---|---|
| Native core | `MasterSplinter.Logic` (C++20 DLL) | Builds git commands behind `extern "C"` `MsGit*`/`MsLogic*`; process execution abstracted per OS (below). |
| Interop | `Interop/NativeLogic.cs` | Only P/Invoke site; marshals UTF-8, frees native strings via `MsGitFree`. |
| Git service | `Git/GitRepository.cs` | Parses the delimited streams into models. |
| View models | `ViewModels/` (MVVM) | Selection, async loading, search; native calls on `Task.Run`. |
| UI | `Controls/`, `MainWindow.xaml`, `Themes/` | WinUI 3 shell, history + graph, diff panels, light/dark. |

**Wire format:** delimited UTF-8 — fields `0x1F`, records `0x1E`, **NUL-free** (the marshaller stops at the
first NUL). Raw binary (image previews) is the exception: bytes + explicit length. `git status -z` output is
NUL-separated, so the core translates each NUL to `0x1E` before returning it across the ABI.

**Working-tree reads never lock the repo:** `status` and worktree `diff` run with `--no-optional-locks` —
without it git opportunistically rewrites `.git/index`, which would re-trigger the app's own file watcher in
an endless refresh loop.

### C++ core design

The core splits *what git command to run* (portable) from *how to run a process on this OS* (per-platform),
using four GoF patterns, so the same sources target **Windows and macOS** (any other platform is a compile error):

```
GitApi.cpp → GitBackend (Bridge: builds git args) → IProcessRunner (Bridge/Adapter seam)
                                                       ├── WindowsProcessRunner  (Win32 + C++/WinRT + WIL)
                                                       └── MacProcessRunner      (Foundation NSTask + POSIX)
              chosen by IPlatformFactory / CreatePlatformFactory()  (Abstract Factory + Factory Method)
```

| Pattern | Role |
|---|---|
| Bridge | `GitBackend` delegates process launch to `IProcessRunner` — command-building and OS execution vary independently. |
| Adapter | `WindowsProcessRunner` / `MacProcessRunner` wrap the OS API behind `IProcessRunner`. |
| Abstract Factory | `IPlatformFactory` builds the platform's services. |
| Factory Method | `CreateProcessRunner()`; `CreatePlatformFactory()` picks per OS (`_WIN32` / `__APPLE__`). |

Adapters are the OS seam and use the **full, mixed** platform API — Windows mixes classic Win32
(`CreateProcessW`) with C++/WinRT (`winrt::to_hstring`) and WIL (RAII handles); macOS mixes Foundation
(`NSTask`) with POSIX (signal-aware exit codes). `GitBackend` itself is pure logic (no OS calls) and is
unit-tested in isolation.

## Tech stack

- .NET 8 (`net8.0-windows10.0.19041`), WinUI 3 / Windows App SDK, MSIX-packaged
- Native C++20 DLL; Windows adapter = Win32 + C++/WinRT + WIL, macOS adapter = NSTask + POSIX
- Google Test — native, hermetic unit tests for the C++ core
- Platforms: `x64`, `ARM64`
- NuGet: CommunityToolkit Sizers/TabbedCommandBar, ColorCode; (native) CppWinRT + WIL

## Build & run

`git` on `PATH`. Build with **VS MSBuild** (the .NET SDK's MSBuild lacks the C++ targets):

```powershell
msbuild MasterSplinter.Entrypoint\MasterSplinter.Entrypoint.csproj -restore -t:Build -p:Configuration=Debug -p:Platform=ARM64
```

The app needs package identity, so run the registered package, not the bare `.exe`. Visual Studio's Deploy
does this for you; from the command line, build with a runtime identifier and stage the loose layout from
the generated recipe (a plain build does **not** produce a registrable folder — `Assets\` is missing next to
the manifest):

```powershell
msbuild MasterSplinter.Entrypoint\MasterSplinter.Entrypoint.csproj -restore -t:Build -p:Configuration=Debug -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64

# Stage win-arm64\AppX\ from the build recipe (each AppxPackagedFile: Include → PackagePath), then:
Add-AppxPackage -Register "MasterSplinter.Entrypoint\bin\ARM64\Debug\net8.0-windows10.0.19041.0\win-arm64\AppX\AppxManifest.xml" -ForceUpdateFromAnyVersion
Start-Process "shell:AppsFolder\<PackageFamilyName>!App"
```

> Match the build architecture to the machine. If changes don't take effect, delete `bin/` + `obj/` and
> rebuild. If registration fails with 0x80070003 and `Get-AppxPackage` shows the package with an empty
> `InstallLocation` (its old folder was deleted), `Remove-AppxPackage` it first. For an incremental loop,
> re-copy the recipe files into `AppX\` and just relaunch — no re-register needed.

## Testing

`MasterSplinter.Logic.Tests` (Google Test) unit-tests the core **hermetically**: it injects a fake
`IProcessRunner` into `GitBackend` and asserts the git command each op builds — no `git.exe`, no repo.
Because `GitBackend` is pure logic, the suite is OS-independent (same tests on Windows and macOS); the
adapters (`CreateProcessW` / `NSTask`) are verified by integration/manual testing.

```powershell
msbuild MasterSplinter.Logic.Tests\MasterSplinter.Logic.Tests.vcxproj -t:Build -p:Configuration=Debug -p:Platform=ARM64
MasterSplinter.Logic.Tests\ARM64\Debug\MasterSplinter.Logic.Tests.exe
```

## CI

GitHub Actions (`.github/workflows/ci.yml`) on every push: a Windows `x64` + `ARM64` matrix (VS 2026
runners) restores packages, builds the app and unit tests, runs the gtest suite, and publishes a JUnit
test report.

## Project layout

```
MasterSplinter.Logic/            C++ core (flat C ABI over git.exe)
  MasterSplinter.Logic.h         ABI declarations (MsLogic* / MsGit*)
  GitApi.cpp                     extern "C" shims — own the char* heap
  Git/GitBackend.cpp             Bridge abstraction — builds git commands
  Platform/IProcessRunner.h      Bridge/Adapter interface
  Platform/IPlatformFactory.h, PlatformFactory.cpp   Abstract Factory + OS selector
  Platform/Windows/              Windows adapter (Win32 + C++/WinRT + WIL)
  Platform/Mac/                  macOS adapter (NSTask + POSIX, .mm)
MasterSplinter.Logic.Tests/      Google Test unit tests (fake IProcessRunner)
MasterSplinter.Entrypoint/       WinUI 3 app (Interop, Git, Models, ViewModels, Controls, Themes)
```
