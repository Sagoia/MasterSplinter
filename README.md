# MasterSplinter

A SourceTree-style Git client: a **portable C++ core** behind a flat C ABI, with a **native WinUI 3**
front-end on Windows (macOS is the next target — each platform keeps its own UI). Git data comes from
shelling out to `git.exe` and parsing its output (TortoiseGit-style, no library binding), so behavior is
predictable and command-building lives in one place.

```
git.exe → C++ core (flat C ABI) → P/Invoke → C# parsing → MVVM → WinUI 3 UI
```

Phases 1–8 are implemented: read-only viewer, diff viewer, working tree, staging & commit, branches & tags,
remotes, merge/rebase/cherry-pick/revert, and stash/blame/search/reflog.

## Docs

| | |
|---|---|
| **[docs/architecture.md](docs/architecture.md)** | Layer map, the C++ core's four GoF patterns, process-execution contract, wire format, threading & refresh. |
| **[docs/abi.md](docs/abi.md)** | The 52 exports, the three return conventions, ownership rules, and the traps this boundary has already paid for. |
| **[docs/ui.md](docs/ui.md)** | Shell, workspace, every feature surface, theming, and the WinUI gotchas. |
| **[docs/graph.md](docs/graph.md)** | The commit graph — current placeholder and the planned layout + Direct2D design. |
| **[docs/features.md](docs/features.md)** | Phase-by-phase feature map keyed to the task IDs used in code comments. |
| **[docs/refactoring.md](docs/refactoring.md)** | What the refactoring pass changed, the patterns it established, and what is deliberately deferred. |
| **[docs/testing.md](docs/testing.md)** | All three suites (gtest, xunit, end-to-end), `FakeProcessRunner`, the four verification levels, coverage gaps, CI. |

## Tech stack

- .NET 8 (`net8.0-windows10.0.19041`), WinUI 3 / Windows App SDK, MSIX-packaged
- Native C++20 DLL; Windows adapter = Win32 + C++/WinRT + WIL, macOS adapter = NSTask + POSIX
- Google Test — native, hermetic unit tests for the C++ core
- Platforms: `x64`, `ARM64`
- NuGet: CommunityToolkit Sizers/TabbedCommandBar, ColorCode; (native) CppWinRT + WIL

## Build & run

Needs `git` on `PATH`. Build with **VS MSBuild** — the .NET SDK's MSBuild lacks the C++ targets.

```powershell
msbuild MasterSplinter.Entrypoint\MasterSplinter.Entrypoint.csproj -restore -t:Build -p:Configuration=Debug -p:Platform=ARM64
```

The app needs package identity, so run the **registered package**, not the bare `.exe`. Visual Studio's
Deploy does this for you. From the command line, build with a runtime identifier and stage the loose layout
from the generated recipe — a plain build does **not** produce a registrable folder (`Assets\` is missing
next to the manifest):

```powershell
msbuild MasterSplinter.Entrypoint\MasterSplinter.Entrypoint.csproj -restore -t:Build -p:Configuration=Debug -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64
```

Then stage `win-arm64\AppX\` from the build recipe (each `AppxPackagedFile`: Include → PackagePath) and:

```powershell
Add-AppxPackage -Register "MasterSplinter.Entrypoint\bin\ARM64\Debug\net8.0-windows10.0.19041.0\win-arm64\AppX\AppxManifest.xml" -ForceUpdateFromAnyVersion
```

```powershell
Start-Process "shell:AppsFolder\<PackageFamilyName>!App"
```

> Match the build architecture to the machine. If changes don't take effect, delete `bin/` + `obj/` and
> rebuild — a native ABI change wants a clean C++ rebuild. If registration fails with `0x80070003` and
> `Get-AppxPackage` shows the package with an empty `InstallLocation` (its old folder was deleted),
> `Remove-AppxPackage` it first. For an incremental loop, re-copy the recipe files into `AppX\` and just
> relaunch — no re-register needed.

## Testing

```powershell
msbuild MasterSplinter.Logic.Tests\MasterSplinter.Logic.Tests.vcxproj -t:Build -p:Configuration=Debug -p:Platform=ARM64
```

```powershell
MasterSplinter.Logic.Tests\ARM64\Debug\MasterSplinter.Logic.Tests.exe
```

```bash
dotnet test MasterSplinter.Core.Tests/MasterSplinter.Core.Tests.csproj -p:Platform=ARM64
```

**188 gtest cases** against the C++ core and **227 xunit cases** against the C# parsers, policy and
stores. The gtest suite and 213 of the xunit cases are hermetic — no `git.exe`, no repository, and no
Windows App SDK runtime, because `MasterSplinter.Core` is plain net8.0. The remaining **14 are
end-to-end**: they build a scratch repo with real git and drive the real native DLL, and skip
themselves when either is unavailable. Details and coverage gaps in
**[docs/testing.md](docs/testing.md)**. CI runs all of it on an `x64` + `ARM64` matrix on every push.

## Project layout

```
MasterSplinter.Logic/            C++ core (flat C ABI over git.exe)
  MasterSplinter.Logic.h         ABI declarations (MsLogic* / MsGit*)
  GitApi.cpp                     extern "C" shims — char* heap + the exception guard
  Git/GitBackend.cpp             Bridge abstraction — process plumbing + terminal verbs
  Git/GitBackend.<Area>.cpp      the git commands, split by area (History, Diff, Refs, ...)
  Git/GitArgs.h, GitText.h       argument builder + shared text helpers
  Platform/IProcessRunner.h      Bridge/Adapter interface
  Platform/IPlatformFactory.h, PlatformFactory.cpp   Abstract Factory + OS selector
  Platform/Windows/              Windows adapter (Win32 + C++/WinRT + WIL)
  Platform/Mac/                  macOS adapter (NSTask + POSIX, .mm) — source-complete, not yet built
MasterSplinter.Logic.Tests/      Google Test unit tests (fake IProcessRunner)
MasterSplinter.Core/             Portable C# half - net8.0, NO WinUI
  Git/GitRepository.cs           Open + shared helpers
  Git/GitRepository.<Area>.cs    Parsing by area - mirrors GitBackend.<Area>.cpp
  Interop/NativeLogic.cs         The only P/Invoke site (internal); NativeCore.cs is the app's view
  Git/GitOperationRunner.cs      Refresh policy (RefreshScope) behind IGitOperationHost
  Git/AppDataStores.cs           Settings + recent repos; directory injected by the host
  ViewModels/SidebarBuilder.cs   Sidebar tree construction
  Models/Graph.cs                Placeholder graph primitives - replaced by the display list
  Models/Commits,Refs,...        Models split by concern (commits, refs, inspection, diff)
  Infrastructure/                UI-agnostic helpers
MasterSplinter.Core.Tests/       xunit: parsers, ABI contracts, policy + end-to-end smoke
MasterSplinter.Entrypoint/       WinUI 3 app (ViewModels, Controls, Themes, RepositoryWatcher)
TortoiseGit/                     Vendored read-only reference clone — GPLv2+, never compiled
```
