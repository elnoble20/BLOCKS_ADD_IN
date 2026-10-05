using Autodesk.Revit.UI;

namespace BlockToFamily;

public class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab("Blocks");
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // The tab may already exist when another add-in creates it.
        }

        RibbonPanel panel = application.CreateRibbonPanel("Blocks", "DWG");
        var buttonData = new PushButtonData(
            "Blocks → Families",
            "Blocks → Families",
            typeof(App).Assembly.Location,
            typeof(PlaceFromBlocksCommand).FullName!);
        var button = (PushButton)panel.AddItem(buttonData);
        button.ToolTip = "Select a DWG import or link and place loaded family types with matching block names.";

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}
