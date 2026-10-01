\# Copilot Instructions — SpatialMorphology (Grasshopper plugin)



\## Project



SpatialMorphology is a Grasshopper plugin for Rhino, being restructured from a single

legacy project into a layered, cross-platform solution. It must run on \*\*Rhino 8 on both

Windows and macOS\*\* from a single set of assemblies.



The assembly and root namespace are spelled `SpatialMorphology`. A misspelled variant

(`SptailMorphology`) existed in stale build output. Never reintroduce it; if it appears,

it is a bug.



\## Hard constraints — never violate these



1\. \*\*Never add WPF or WinForms.\*\* Do not set `<UseWPF>` or `<UseWindowsForms>`, and never

&#x20;  add `System.Windows.\*`, `System.Windows.Forms`, or `RhinoWindows` references.

&#x20;  All UI must use \*\*Eto.Forms\*\*, which is cross-platform and shipped by Rhino.

2\. \*\*Target framework is bare `net8.0`.\*\* Never use a `-windows` suffix (e.g. `net8.0-windows`)

&#x20;  — it breaks macOS. Do not use `net10.0` unless explicitly asked for the Rhino 9 validation leg.

&#x20;  Do not add `net48` unless explicitly asked for a Rhino 7 legacy leg.

3\. \*\*Platform must be `AnyCPU`.\*\* Never add `x64` or `arm64` platform configurations or

&#x20;  `Condition="'$(Configuration)|$(Platform)'=='Debug|x64'"`-style PropertyGroups.

&#x20;  Rhino 8 runs on Windows x64, Intel Mac, and Apple Silicon — only AnyCPU covers all three.

&#x20;  In `.csproj` the spelling is `AnyCPU` (no space); in `.sln` it is `Any CPU` (with a space).

4\. \*\*Never change a `ComponentGuid`.\*\* These GUIDs are how Grasshopper rehydrates components

&#x20;  in saved `.gh` files. Changing one silently breaks every existing user definition.

&#x20;  Also preserve: parameter order, parameter nicknames, and `Write`/`Read` serialization keys.

5\. \*\*Never use `BinaryFormatter`.\*\* It is disabled by default on .NET 9+. Use explicit

&#x20;  serialization instead.

6\. \*\*Case-sensitive paths.\*\* macOS filesystems are case-sensitive. Folder and namespace

&#x20;  casing must match exactly.

7\. \*\*No absolute paths anywhere.\*\* No `C:\\Program Files\\Rhino 8\\...` hint paths, no

&#x20;  machine-specific output directories. The repo must clone and build on any machine.



\## NuGet references



Reference RhinoCommon and Grasshopper as PackageReferences with:



```xml

<PackageReference Include="Grasshopper" Version="8.\*" ExcludeAssets="runtime" PrivateAssets="all" />

```



`ExcludeAssets="runtime"` and `PrivateAssets="all"` are mandatory — Rhino supplies these

assemblies at runtime, and copying them next to the `.gha` causes assembly-identity

conflicts and load failures (more often on macOS).



Never use `<Reference>` with a `<HintPath>` pointing at an installed Rhino directory.



\## Target architecture



Dependencies point downward only:



```

SpatialMorphology.Core         netstandard2.0. Pure logic. NO Rhino references. Fully unit-testable.

SpatialMorphology.Rhino        Geometry work requiring RhinoCommon (Brep, Mesh, Curve).

SpatialMorphology.Grasshopper  Thin GH\_Component shells. The only project producing the .gha.

SpatialMorphology.UI           Optional Eto.Forms dialogs.

tests/                         NUnit tests, primarily against Core.

```



`Core` must never reference `Rhino`, `Grasshopper`, or `UI`. If logic seems to need

RhinoCommon, it belongs in the `Rhino` layer instead.



`SolveInstance` should be a thin adapter: read inputs → call a `Core`/`Rhino` method →

set outputs. Business logic belongs in `Core` where it can be tested without launching Rhino.



\## Style



\- C# with nullable reference types enabled.

\- XML doc comments on public members.

\- Prefer pure, side-effect-free methods in `Core`.

\- Keep `bin/` and `obj/` out of Git.



\## Verifying your own work



A successful Windows build does not mean the change is correct. Before reporting a task

complete, confirm:



\- No `-windows` TFM, no `UseWPF`, no `UseWindowsForms` anywhere in any `.csproj`.

\- No `x64` or `arm64` folders appear under `bin/` or `obj/` after a rebuild.

\- Every `ComponentGuid` in the repo is unchanged from before the task.

\- `Core` has no reference to RhinoCommon, Grasshopper, or Eto.

\- No new absolute filesystem paths were introduced.



\## Agent behaviour



\- Stale `obj/` and `bin/` state causes misleading results. When build output looks wrong,

&#x20; delete those folders and rebuild before diagnosing further.

\- State clearly what any destructive command will do before proposing it, especially

&#x20; `git rm`, `rmdir`, `del`, or anything that moves or deletes files. Prefer commands that

&#x20; are reversible via Git.

\- Prefer many small commits over one large one during the restructure.

\- When moving files between projects, use `git mv` so history is preserved.



