# BlockToFamily

A Revit add-in that reads block references from a selected DWG import or link.
Each block name is matched case-insensitively to a loaded family type name first,
then to a family name as a fallback. Matching instances are placed at the block's
XY insertion point on the resolved level, with the block's rotation about Z.

## Requirements

- Autodesk Revit 2027 (runs on .NET 10), installed on Windows.
- .NET 10 SDK.
- Visual Studio 2026 with .NET desktop development, or the `dotnet` CLI.
- Matching family types loaded in the Revit document.

## Build and install

Open `BlockToFamily.sln` in Visual Studio and build Release, or run from the
repository root:

```powershell
dotnet restore BlockToFamily.sln
dotnet build BlockToFamily.sln -c Release --no-restore
```

The project references `RevitAPI.dll` and `RevitAPIUI.dll` from
`C:\Program Files\Autodesk\Revit 2027\`. These assemblies are not redistributed.
Restore supports non-Windows environments, but a full build requires the Revit
API assemblies; running the add-in requires Revit.

Output is in `BlockToFamily\bin\Release\net10.0-windows\`. On Windows, a
best-effort post-build step copies `BlockToFamily.dll` and `BlockToFamily.addin`
to `%AppData%\Autodesk\Revit\Addins\2027\`. Deployment failures produce warnings
without failing the build. To disable automatic installation:

```powershell
dotnet build BlockToFamily.sln -c Release -p:DeployToRevit=false
```

For manual installation, create that Addins folder and copy both files from the
output directory into it. Keep them together: the manifest uses the relative
assembly path `BlockToFamily.dll`. Close Revit before replacing the DLL, then
restart Revit to load the add-in. The manifest registers both the ribbon
application and the External Tools command.

## Usage

1. Open a plan view with a DWG import or link and load the matching families.
2. On the **Blocks** tab, in the **DWG** panel, run **Blocks → Families**.
   The command is also available from **Add-Ins → External Tools**.
3. Select the DWG import/link when prompted.
4. Read the summary dialog for the placed instance count and unmatched block names.

## Notes and limitations

- Block names may appear as `file.dwg.BLOCKNAME`.
  `DwgBlockReader.NormalizeName` strips the `.dwg.` prefix for matching.
- Hosted families (doors, windows, face-based families) are not supported for
  placement: the command does not supply a host. Use non-hosted families; matching
  a hosted type may cause placement to fail.
- Mirrored blocks are not mirrored; only their insertion point and rotation are used.
- Re-running the command creates duplicates; existing instances are not tracked.
- The DWG import transform accounts for units and scale, but instance Z is
  flattened to the resolved level's elevation.