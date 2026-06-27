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
| Native core | `MasterSplinter.Logic` (C++20 DLL) | Builds git command lines, runs `git.exe` (`CreateProcessW`), returns raw UTF-8. `extern "C"` `MsGit*` functions only — no C++/WinRT, no `<windows.h>` in the portable logic. |
| Interop | `Interop/NativeLogic.cs` | The **only** file that touches P/Invoke. Marshals UTF-8 in/out; frees native strings via `MsGitFree`. |
| Git service | `Git/GitRepository.cs` | Parses the delimited streams into view models. |
| View models | `ViewModels/` (MVVM, `ObservableObject`) | `MainViewModel` drives selection, loading, search; native calls run on `Task.Run`, continuations resume on the UI thread. |
| UI | `Controls/`, `MainWindow.xaml`, `Themes/AppResources.xaml` | WinUI 3 shell: custom title bar, repository tab strip, navigation sidebar, commit history table + graph, detail and diff panels, light/dark theme dictionaries. |

**Wire format:** all native string returns are delimited UTF-8 — fields separated by `0x1F`, records
by `0x1E` — and must be **NUL-free** (the managed marshaller stops at the first NUL). Raw binary
(e.g. image blobs for previews) is the sole exception: it is returned as bytes + an explicit length.

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
- Native C++20 DLL (`ConfigurationType=DynamicLibrary`, `TargetName=MasterSplinterLogic`)
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

## Project layout

```
MasterSplinter.Logic/        Native C++ core (flat C ABI over git.exe)
  MasterSplinter.Logic.h     ABI declarations (MsLogic* lifecycle, MsGit* git ops)
  GitBackend.cpp             git command construction + process execution
MasterSplinter.Entrypoint/   WinUI 3 app
  Interop/NativeLogic.cs     P/Invoke boundary
  Git/GitRepository.cs       stream parsing -> models
  Models/Models.cs           CommitRow, ChangedFile, DiffLine/DiffRow, ...
  ViewModels/                MVVM
  Controls/, Themes/         UI + theming
  Infrastructure/            converters, template selectors, diff helpers
```
