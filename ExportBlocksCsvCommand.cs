using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WinForms = System.Windows.Forms;

namespace BlockToFamily;

/// <summary>
/// Standalone external command that reads DWG block references from a selected ImportInstance,
/// validates them against the currently loaded Revit families, and exports both the
/// Validation Summary CSV and the per-instance Coordinates CSV.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public class ExportBlocksCsvCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        ImportInstance? import = SelectImportInstance(uidoc, doc);
        if (import is null)
        {
            return Result.Cancelled;
        }

        IList<BlockReferenceInfo> blocks = DwgBlockReader.ReadBlocks(import, view);
        if (blocks.Count == 0)
        {
            TaskDialog.Show("Extract & Validate Blocks CSV", "No block references were found in the selected DWG import/link.");
            return Result.Succeeded;
        }

        FamilyCatalog catalog = FamilyCatalog.Build(doc);
        List<BlockValidationSummary> summaries = catalog.ValidateBlocks(blocks);

        string dwgName = ResolveDwgName(doc, import);

        using var saveDialog = new WinForms.SaveFileDialog
        {
            Title = "Save DWG Block Validation Summary CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{dwgName}_BlockValidation.csv"
        };

        if (saveDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return Result.Cancelled;
        }

        string summaryCsvPath = saveDialog.FileName;
        CsvBlockService.ExportValidationSummaryCsv(summaryCsvPath, summaries);

        string directory = Path.GetDirectoryName(summaryCsvPath) ?? string.Empty;
        string instancesCsvPath = Path.Combine(directory, $"{dwgName}_BlockInstances.csv");
        var validationMap = summaries.ToDictionary(s => s.BlockName, StringComparer.OrdinalIgnoreCase);
        CsvBlockService.ExportBlockInstancesCsv(instancesCsvPath, blocks, validationMap);

        int matchedCount = summaries.Count(s => s.IsValid);
        int unmatchedCount = summaries.Count - matchedCount;

        var report = new StringBuilder();
        report.AppendLine($"DWG Source: {dwgName}");
        report.AppendLine($"Total Block References Read: {blocks.Count}");
        report.AppendLine($"Unique Block Names: {summaries.Count}");
        report.AppendLine($"Validated (Matching Revit Family Found): {matchedCount}");
        report.AppendLine($"Missing (No Matching Revit Family): {unmatchedCount}");
        report.AppendLine();
        report.AppendLine("Exported CSV Files:");
        report.AppendLine($"1. Summary & Validation: {summaryCsvPath}");
        report.AppendLine($"2. Full Instance Coordinates: {instancesCsvPath}");

        if (unmatchedCount > 0)
        {
            report.AppendLine();
            report.AppendLine("Unmatched DWG Block Names:");
            foreach (BlockValidationSummary missing in summaries.Where(s => !s.IsValid).Take(20))
            {
                report.AppendLine($" • {missing.BlockName} ({missing.InstanceCount}x)");
            }
        }

        TaskDialog.Show("DWG Blocks Extracted & Validated", report.ToString());
        return Result.Succeeded;
    }

    internal static ImportInstance? SelectImportInstance(UIDocument uidoc, Document doc)
    {
        // Honor pre-selected ImportInstance if one is already selected
        ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
        foreach (ElementId id in selectedIds)
        {
            if (doc.GetElement(id) is ImportInstance preselected)
            {
                return preselected;
            }
        }

        try
        {
            Reference r = uidoc.Selection.PickObject(
                ObjectType.Element,
                new ImportInstanceSelectionFilter(),
                "Select a linked or imported DWG containing block references");
            return doc.GetElement(r) as ImportInstance;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }
    }

    internal static string ResolveDwgName(Document doc, ImportInstance import)
    {
        if (doc.GetElement(import.GetTypeId()) is ElementType type &&
            !string.IsNullOrWhiteSpace(type.Name))
        {
            return Path.GetFileNameWithoutExtension(type.Name.Trim());
        }

        return "DWG_Import";
    }
}

internal sealed class ImportInstanceSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => elem is ImportInstance;
    public bool AllowReference(Reference reference, XYZ position) => false;
}
