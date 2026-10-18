# Prebuilt bridge packages

Each ZIP here is manually built with `scripts/package-bridge.ps1` against the
named public Blade & Sorcery version and checked in so clients can install the
bridge without compiling it. The packages contain no game, Unity, or
Newtonsoft assemblies: only `ThunderRoadMcp.Bridge.dll`, `manifest.json`, and
`runtime/*.dll.payload`.

Build a new entry against an installed game's `Managed` directory, then add the
ZIP and a `versions.json` entry (`gameVersion`, `package`):

```powershell
scripts/package-bridge.ps1 -GameManagedDir '<game>\BladeAndSorcery_Data\Managed' -OutputDirectory '<new-empty-dir>'
```
