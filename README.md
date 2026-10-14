# BlockToFamily — Revit DWG Block Reader, CSV Validator & Family Placer

A C# Revit add-in (compatible with **Revit 2025/2026 on .NET 8** and **Revit 2027 on .NET 10**) that automates converting 2D/3D CAD block layouts into native Revit families:

1. **Read DWG Block Names & Transforms**: Inspects a selected linked or imported DWG (`ImportInstance`), recursively walks nested `GeometryInstance` objects using `GetSymbolGeometryId().SymbolId`, strips `<filename>.dwg.` prefixes, filters out anonymous AutoCAD blocks (`*U...`, `*D...`), and extracts each block's name, layer, world `(X, Y, Z)` insertion point, rotation angle, scale, and mirror state.
2. **Extract Block Lists to CSV**: Exports two UTF-8 CSV files:
   - `<DwgName>_BlockValidation.csv`: Unique block names, occurrence counts, DWG layer, validation status (`Valid` vs. `Missing`), matched Revit Family & Type names, Category, and `FamilyPlacementType`.
   - `<DwgName>_BlockInstances.csv`: Every individual block reference with exact `OriginX_Ft`, `OriginY_Ft`, `OriginZ_Ft`, `RotationDegrees`, `ScaleX`, `ScaleY`, `IsMirrored`, and validation match.
3. **Validate Against Current Revit Families**: Compares DWG block names against all loaded placeable `FamilySymbol`s in the active Revit model (matching by **Type Name** first, **Family Name** second, or `Family : Type`), and displays an interactive **WPF Validation & Mapping Window** where you can remap any unmatched DWG block name via dropdown or import an edited CSV mapping file.
4. **Place Families on Top of Each Block**: Activates matched `FamilySymbol`s, places `FamilyInstance`s at each DWG block's exact coordinates on the host `Level`, rotates each element around the Z-axis to match the CAD block orientation, and tags `Comments` with `DWG_BLOCK:<BlockName>` so re-running can automatically skip already-placed instances.

## Project Structure

| File | Responsibility |
| :--- | :--- |
| `BlockToFamily/BlockToFamily.csproj` | Multi-targets `net8.0-windows` (Revit 2025) and `net10.0-windows` (Revit 2027) with local `RevitAPI.dll` references and `Nice3point.Revit.Api` NuGet fallback for CI. |
| `BlockToFamily/BlockToFamily.addin` | Registers the `BlockToFamily.App` ribbon application and both external commands (`PlaceFromBlocksCommand` and `ExportBlocksCsvCommand`). |
| `BlockToFamily/App.cs` | Creates the **Blocks** ribbon tab and **DWG** panel (reusing the panel safely if another add-in already created it). |
| `BlockToFamily/DwgBlockReader.cs` | Traverses `ImportInstance` geometry to extract `BlockReferenceInfo` records (name, layer, XYZ origin, Z rotation, scale, mirror check). |
| `BlockToFamily/FamilyValidator.cs` | Indexes loaded `FamilySymbol`s (`FamilyCatalog`) and validates unique DWG block names (`BlockValidationSummary`). |
| `BlockToFamily/CsvBlockService.cs` | Exports summary/instance CSV files and imports user-edited CSV family mappings. |
| `BlockToFamily/BlockValidationWindow.cs` | Pure-C# WPF window with live validation counts, CSV export/import buttons, and per-block `Family : Type` remapping dropdowns. |
| `BlockToFamily/FamilyPlacementService.cs` | Executes transactional symbol activation, `NewFamilyInstance` placement, Z-rotation, and duplicate-prevention tracking. |
| `BlockToFamily/ExportBlocksCsvCommand.cs` | Read-only ribbon command to extract and validate DWG block lists directly to CSV. |
| `BlockToFamily/PlaceFromBlocksCommand.cs` | Full workflow command that launches the validation dialog and places families on top of each DWG block. |

## Build & Installation

### Build via .NET CLI or Visual Studio
```bash
# Build using local Revit installation (Revit 2025 and/or 2027):
dotnet build BlockToFamily/BlockToFamily.csproj -c Release

# Or build anywhere (including GitHub Actions CI) using Nice3point NuGet packages:
dotnet build BlockToFamily/BlockToFamily.csproj -c Release -p:UseNuGetRevitApi=true
```

### Automatic Deployment
On Windows, building the project automatically copies `BlockToFamily.dll` and `BlockToFamily.addin` to:
- **Revit 2025**: `%AppData%\Autodesk\Revit\Addins\2025\`
- **Revit 2027**: `%AppData%\Autodesk\Revit\Addins\2027\`
