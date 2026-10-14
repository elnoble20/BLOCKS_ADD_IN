using System.Reflection;
using Autodesk.Revit.UI;

namespace BlockToFamily;

/// <summary>
/// Registers the "Blocks" ribbon tab and "DWG" panel with commands for:
/// 1. Exporting DWG block names &amp; validation reports to CSV.
/// 2. Validating DWG blocks against loaded Revit families and placing instances on top of each block.
/// </summary>
public class App : IExternalApplication
{
    private const string TabName = "Blocks";
    private const string PanelName = "DWG";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Tab already exists; safe to continue.
        }

        // Reuse existing "DWG" panel if present to prevent startup ArgumentException.
        RibbonPanel? panel = application
            .GetRibbonPanels(TabName)
            .FirstOrDefault(p => string.Equals(p.Name, PanelName, StringComparison.OrdinalIgnoreCase))
            ?? application.CreateRibbonPanel(TabName, PanelName);

        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var exportButtonData = new PushButtonData(
            name: "BlockToFamily_ExportCsv",
            text: "Extract & Validate
Blocks CSV",
            assemblyName: assemblyPath,
            className: typeof(ExportBlocksCsvCommand).FullName!)
        {
            ToolTip = "Reads block references from a selected DWG import/link, validates them against loaded Revit families, and exports summary and instance CSV reports."
        };

        var placeButtonData = new PushButtonData(
            name: "BlockToFamily_ValidateAndPlace",
            text: "Validate & Place
Families",
            assemblyName: assemblyPath,
            className: typeof(PlaceFromBlocksCommand).FullName!)
        {
            ToolTip = "Opens the DWG Block-to-Family Validation manager to export CSVs, map unmatched block names to loaded Revit families, and place instances on top of each DWG block."
        };

        panel.AddItem(exportButtonData);
        panel.AddItem(placeButtonData);

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}
