# MasterSplinter

A SourceTree-style Git client with a **portable C++ core** and a **native WinUI 3** front-end on
Windows. Domain logic lives in a cross-platform C++ library behind a flat C ABI, so the same core can
be reused on other operating systems while each platform keeps its own native UI.

## Goal

Build a fast, readable Git history and review tool. Git data is read by shelling out to `git.exe`
and parsing its output rather than depending on a library binding, so behavior stays predictable and
the command-building knowledge lives in one place behind the C++ core.

- **Phase 1 — read-only viewer:** browse repositories, history, branches, and file changes.
- **Phase 2 — diff viewer:** inspect changes before commit/merge/review.

## Architecture

A strict, layered boundary keeps business logic portable and the UI thin:

```
git.exe ──> C++ core (flat C ABI) ──> P/Invoke ──> C# parsing ──> MVVM ──> WinUI 3 UI
```

| Layer | Project / file | Responsibility |
|-------|----------------|----------------|
| Native core | `MasterSplinter.Logic` (C++20 DLL) | Portable git-command building behind a flat `extern "C"` `MsGit*` / `MsLogic*` ABI. Process execution is abstracted per OS (see [C++ core design](#c-core-design)), so the same sources target Windows and macOS. No C++/WinRT; `<windows.h>` is confined to the Windows adapter. |
| Interop | `Interop/NativeLogic.cs` | The **only** file that touches P/Invoke. Marshals UTF-8 in/out; frees native strings via `MsGitFree`. |
| Git service | `Git/GitRepository.cs` | Parses the delimited streams into view models. |
| View models | `ViewModels/` (MVVM, `ObservableObject`) | `MainViewModel` drives selection, loading, search; native calls run on `Task.Run`, continuations resume on the UI thread. |
| UI | `Controls/`, `MainWindow.xaml`, `Themes/AppResources.xaml` | WinUI 3 shell: custom title bar, repository tab strip, navigation sidebar, commit history table + graph, detail and diff panels, light/dark theme dictionaries. |

**Wire format:** all native string returns are delimited UTF-8 — fields separated by `0x1F`, records
by `0x1E` — and must be **NUL-free** (the managed marshaller stops at the first NUL). Raw binary
(e.g. image blobs for previews) is the sole exception: it is returned as bytes + an explicit length.

### C++ core design

The core separates *what git command to run* (portable) from *how to run a process on this OS*
(platform-specific), using four GoF patterns so the same sources build on **Windows and macOS**
(the two supported targets — any other platform is a hard compile error):

```
GitApi.cpp              extern "C" MsGit* shims — own the char* heap, hold the backend
    │
GitBackend              Bridge abstraction — builds git args, returns raw UTF-8 (no OS calls)
    │  delegates process launch to ↓
IProcessRunner          Bridge implementor / Adapter target
    ├── WindowsProcessRunner   Adapter over CreateProcessW + pipes
    └── MacProcessRunner       Adapter over Foundation NSTask (Objective-C++)
        ↑ built by
IPlatformFactory / CreatePlatformFactory()   Abstract Factory + Factory Method (#ifdef per OS)
```

| Pattern | Role |
|---------|------|
| **Bridge** | `GitBackend` (git operations) delegates process launch to `IProcessRunner`, so command-building and OS execution vary independently. |
| **Adapter** | `WindowsProcessRunner` / `MacProcessRunner` wrap `CreateProcessW` / `NSTask` behind the common `IProcessRunner` interface. |
| **Abstract Factory** | `IPlatformFactory` creates the platform's services (today, the process runner). |
| **Factory Method** | `CreateProcessRunner()` on each concrete factory; `CreatePlatformFactory()` selects the factory per OS (`_WIN32` / `__APPLE__`, `#error` otherwise). |

Because `GitBackend` is pure logic with no OS calls, it is unit-tested in isolation — see [Testing](#testing).
The MVVM/UI layer above the C ABI is unchanged by this split.

## Features

**Phase 1 — read-only viewer**
- Open a repository; recent-repositories list
- Branch / tag / remote sidebar
- Commit history with a single-lane graph, ref decorations (HEAD / branch / tag badges)
- Search filter and commit ordering (date / topological / reverse / author-date)
- Copy short / full SHA; open a file's contents at a given commit
- Changed-files list with a unified diff

**Phase 2 — diff viewer**
- **Commit summary** — files changed, insertions, deletions
- **Unified & side-by-side** diff, toggled per file
- **Syntax highlighting** for common languages, theme-aware
- **Whitespace options** — show all / ignore space changes / ignore all whitespace
- **Binary & image diff** — binary-file card, with before/after image previews
- **Compare two commits** — mark a commit, then compare another against it
- **Compare refs** — diff any branch / tag / HEAD against another

## Tech stack

- .NET 8 (`net8.0-windows10.0.19041`), WinUI 3 / Windows App SDK, packaged as MSIX
- Native C++20 DLL (`ConfigurationType=DynamicLibrary`, `TargetName=MasterSplinterLogic`); process execution via `CreateProcessW` (Windows) / Foundation `NSTask` (macOS)
- Google Test — native, hermetic unit tests for the C++ core
- Platforms: `x64`, `ARM64` (64-bit only)
- NuGet: CommunityToolkit Sizers / TabbedCommandBar, ColorCode (syntax highlighting)

## Build & run

`git` must be on `PATH`. The C# app references the native C++ project, so build with **Visual Studio
MSBuild** (the .NET SDK's MSBuild lacks the C++ targets):

```powershell
& "<VS>\MSBuild\Current\Bin\MSBuild.exe" `
    MasterSplinter.Entrypoint\MasterSplinter.Entrypoint.csproj `
    -restore -t:Build -p:Configuration=Debug -p:Platform=ARM64
```

The app needs package identity (it uses MicaBackdrop and other APIs that require it), so run it from
the registered package rather than the bare `.exe`:

```powershell
# Register the loose layout (no admin needed)
Add-AppxPackage -Register `
    "MasterSplinter.Entrypoint\bin\ARM64\Debug\net8.0-windows10.0.19041.0\win-arm64\AppxManifest.xml" `
    -ForceUpdateFromAnyVersion

# Launch via the Apps folder
Start-Process "shell:AppsFolder\<PackageFamilyName>!App"
```

> Match the build architecture to the machine (registering an x64 build over an installed ARM64
> package fails with `0x80073CF3`). If code changes don't appear to take effect, delete `bin/` and
> `obj/` (and the C++ project's platform output) for a clean rebuild.

## Testing

`MasterSplinter.Logic.Tests` is a **Google Test** suite that unit-tests the C++ core **hermetically**:
it injects a fake `IProcessRunner` into `GitBackend` and asserts the exact git command each operation
builds — no `git.exe`, no repository, no OS process. Because `GitBackend` is pure logic, the tests are
OS-independent: the same suite compiles and passes on Windows and macOS. The platform adapters
(`CreateProcessW` / `NSTask`) are the thin OS seam and are verified separately by integration/manual
testing, not unit tests.

```powershell
& "<VS>\MSBuild\Current\Bin\MSBuild.exe" `
    MasterSplinter.Logic.Tests\MasterSplinter.Logic.Tests.vcxproj `
    -t:Build -p:Configuration=Debug -p:Platform=ARM64
MasterSplinter.Logic.Tests\ARM64\Debug\MasterSplinter.Logic.Tests.exe   # runs all tests
```

## Project layout

```
MasterSplinter.Logic/            Native C++ core (flat C ABI over git.exe)
  MasterSplinter.Logic.h         ABI declarations (MsLogic* lifecycle, MsGit* git ops)
  GitApi.cpp                     extern "C" ABI shims — own the char* heap, hold the backend
  Git/GitBackend.cpp             Bridge abstraction — builds git commands, returns raw UTF-8
  Platform/IProcessRunner.h      Bridge implementor / Adapter interface
  Platform/IPlatformFactory.h    Abstract Factory interface
  Platform/PlatformFactory.cpp   Factory Method — selects the platform factory per OS
  Platform/Windows/              Windows Adapter (CreateProcessW)
  Platform/Mac/                  macOS Adapter (Foundation NSTask, Objective-C++ .mm)
MasterSplinter.Logic.Tests/      Google Test unit tests (fake IProcessRunner)
MasterSplinter.Entrypoint/       WinUI 3 app
  Interop/NativeLogic.cs         P/Invoke boundary
  Git/GitRepository.cs           stream parsing -> models
  Models/Models.cs               CommitRow, ChangedFile, DiffLine/DiffRow, ...
  ViewModels/                    MVVM
  Controls/, Themes/             UI + theming
  Infrastructure/                converters, template selectors, diff helpers
```
