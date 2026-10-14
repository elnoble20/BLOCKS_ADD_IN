using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WinForms = System.Windows.Forms;

namespace BlockToFamily;

/// <summary>
/// Code-only WPF dialog that displays discovered DWG block names, validates them against
/// loaded Revit FamilySymbols, exports/imports CSV validation lists, lets the user remap
/// unmatched blocks via a dropdown, and confirms placement on top of each DWG block.
/// </summary>
public sealed class BlockValidationWindow : Window
{
    private readonly IReadOnlyList<BlockReferenceInfo> _allBlocks;
    private readonly List<BlockValidationRowViewModel> _rowViewModels;
    private readonly FamilyCatalog _catalog;
    private readonly string _dwgName;

    private readonly TextBlock _summaryTextBlock;
    private readonly DataGrid _grid;
    private readonly CheckBox _useBlockZCheckBox;
    private readonly CheckBox _skipDuplicatesCheckBox;

    public bool UseBlockZOffset => _useBlockZCheckBox.IsChecked == true;
    public bool SkipExistingDuplicates => _skipDuplicatesCheckBox.IsChecked == true;

    public BlockValidationWindow(
        string dwgName,
        IReadOnlyList<BlockReferenceInfo> allBlocks,
        IList<BlockValidationSummary> summaries,
        FamilyCatalog catalog)
    {
        _dwgName = string.IsNullOrWhiteSpace(dwgName) ? "DWG_Import" : Path.GetFileNameWithoutExtension(dwgName);
        _allBlocks = allBlocks;
        _catalog = catalog;

        var symbolOptions = new List<SymbolOptionItem> { new("(None / Unmatched)", null) };
        foreach (FamilySymbol sym in catalog.PlaceableSymbols)
        {
            string label = $"{sym.Family?.Name ?? "Family"} : {sym.Name}";
            symbolOptions.Add(new SymbolOptionItem(label, sym));
        }

        _rowViewModels = summaries
            .Select(s => new BlockValidationRowViewModel(s, symbolOptions))
            .ToList();

        Title = $"DWG Block-to-Family Validator & Placer — {_dwgName}";
        Width = 980;
        Height = 620;
        MinWidth = 820;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252));

        var rootGrid = new System.Windows.Controls.Grid { Margin = new Thickness(16) };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: Header & live validation counts
        _summaryTextBlock = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            Margin = new Thickness(0, 0, 0, 10)
        };
        System.Windows.Controls.Grid.SetRow(_summaryTextBlock, 0);
        rootGrid.Children.Add(_summaryTextBlock);

        // Row 1: CSV & Selection Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(CreateButton("Export Block Summary CSV...", OnExportSummaryCsvClick));
        toolbar.Children.Add(CreateButton("Export All Block Coordinates CSV...", OnExportInstancesCsvClick));
        toolbar.Children.Add(CreateButton("Import Mapping CSV...", OnImportMappingCsvClick));
        toolbar.Children.Add(CreateButton("Select All Valid", (_, _) => SetSelectionForValid(true)));
        toolbar.Children.Add(CreateButton("Clear Selection", (_, _) => SetSelectionForAll(false)));
        System.Windows.Controls.Grid.SetRow(toolbar, 1);
        rootGrid.Children.Add(toolbar);

        // Row 2: Validation DataGrid
        _grid = BuildDataGrid(symbolOptions);
        _grid.ItemsSource = _rowViewModels;
        System.Windows.Controls.Grid.SetRow(_grid, 2);
        rootGrid.Children.Add(_grid);

        // Row 3: Placement Options
        var optionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 12)
        };

        _useBlockZCheckBox = new CheckBox
        {
            Content = "Use DWG Block Z-Elevation (uncheck to place flat on active Level)",
            IsChecked = false,
            Margin = new Thickness(0, 0, 24, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        _skipDuplicatesCheckBox = new CheckBox
        {
            Content = "Skip blocks that already have a placed FamilyInstance at the same XY point",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center
        };

        optionsPanel.Children.Add(_useBlockZCheckBox);
        optionsPanel.Children.Add(_skipDuplicatesCheckBox);
        System.Windows.Controls.Grid.SetRow(optionsPanel, 3);
        rootGrid.Children.Add(optionsPanel);

        // Row 4: Dialog Action Buttons
        var footerPanel = new DockPanel { LastChildFill = false };
        var cancelBtn = CreateButton("Cancel", (_, _) =>
        {
            DialogResult = false;
            Close();
        });
        DockPanel.SetDock(cancelBtn, Dock.Right);

        var placeBtn = new System.Windows.Controls.Button
        {
            Content = "Place Selected Families on Blocks",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 8, 0),
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        placeBtn.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        DockPanel.SetDock(placeBtn, Dock.Right);

        footerPanel.Children.Add(cancelBtn);
        footerPanel.Children.Add(placeBtn);
        System.Windows.Controls.Grid.SetRow(footerPanel, 4);
        rootGrid.Children.Add(footerPanel);

        Content = rootGrid;
        UpdateSummaryBanner();
    }

    private DataGrid BuildDataGrid(IReadOnlyList<SymbolOptionItem> symbolOptions)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = false,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeaderWidth = 0,
            AlternatingRowBackground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1)
        };

        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Place",
            Binding = new System.Windows.Data.Binding(nameof(BlockValidationRowViewModel.PlaceSelected))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            },
            Width = new DataGridLength(60)
        });

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "DWG Block Name",
            Binding = new System.Windows.Data.Binding(nameof(BlockValidationRowViewModel.BlockName)),
            IsReadOnly = true,
            Width = new DataGridLength(180)
        });

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Count",
            Binding = new System.Windows.Data.Binding(nameof(BlockValidationRowViewModel.InstanceCount)),
            IsReadOnly = true,
            Width = new DataGridLength(65)
        });

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "DWG Layer",
            Binding = new System.Windows.Data.Binding(nameof(BlockValidationRowViewModel.SampleLayer)),
            IsReadOnly = true,
            Width = new DataGridLength(120)
        });

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Validation Status",
            Binding = new System.Windows.Data.Binding(nameof(BlockValidationRowViewModel.ValidationStatusText)),
            IsReadOnly = true,
            Width = new DataGridLength(210)
        });

        var comboColumn = new DataGridComboBoxColumn
        {
            Header = "Target Revit Family : Type (Click to Remap)",
            ItemsSource = symbolOptions,
            DisplayMemberPath = nameof(SymbolOptionItem.DisplayText),
            SelectedItemBinding = new System.Windows.Data.Binding(nameof(BlockValidationRowViewModel.SelectedOption))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        };
        grid.Columns.Add(comboColumn);

        foreach (BlockValidationRowViewModel vm in _rowViewModels)
        {
            vm.PropertyChanged += (_, _) => UpdateSummaryBanner();
        }

        return grid;
    }

    private void UpdateSummaryBanner()
    {
        int totalUnique = _rowViewModels.Count;
        int validUnique = _rowViewModels.Count(r => r.Model.IsValid);
        int missingUnique = totalUnique - validUnique;
        int selectedInstances = _rowViewModels.Where(r => r.PlaceSelected && r.Model.IsValid).Sum(r => r.InstanceCount);

        _summaryTextBlock.Text =
            $"DWG Blocks Found: {_allBlocks.Count} instances across {totalUnique} unique block names   |   " +
            $"Validated: {validUnique} matched, {missingUnique} missing   |   " +
            $"Queued to Place: {selectedInstances} instances";
    }

    private void SetSelectionForValid(bool selected)
    {
        foreach (BlockValidationRowViewModel row in _rowViewModels)
        {
            row.PlaceSelected = selected && row.Model.IsValid;
        }
        _grid.Items.Refresh();
        UpdateSummaryBanner();
    }

    private void SetSelectionForAll(bool selected)
    {
        foreach (BlockValidationRowViewModel row in _rowViewModels)
        {
            row.PlaceSelected = selected;
        }
        _grid.Items.Refresh();
        UpdateSummaryBanner();
    }

    private void OnExportSummaryCsvClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.SaveFileDialog
        {
            Title = "Export DWG Block Validation Summary CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{_dwgName}_BlockValidation.csv"
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            CsvBlockService.ExportValidationSummaryCsv(dialog.FileName, _rowViewModels.Select(r => r.Model));
            TaskDialog.Show("CSV Exported", $"Saved block validation summary CSV to:
{dialog.FileName}");
        }
    }

    private void OnExportInstancesCsvClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.SaveFileDialog
        {
            Title = "Export All DWG Block Instances CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{_dwgName}_BlockInstances.csv"
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            var dict = _rowViewModels.ToDictionary(r => r.BlockName, r => r.Model, StringComparer.OrdinalIgnoreCase);
            CsvBlockService.ExportBlockInstancesCsv(dialog.FileName, _allBlocks, dict);
            TaskDialog.Show("CSV Exported", $"Saved {_allBlocks.Count} block instances to:
{dialog.FileName}");
        }
    }

    private void OnImportMappingCsvClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Import Edited Block Validation CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            var models = _rowViewModels.Select(r => r.Model).ToList();
            int updated = CsvBlockService.ImportValidationOverridesFromCsv(dialog.FileName, models, _catalog);
            foreach (BlockValidationRowViewModel vm in _rowViewModels)
            {
                vm.SyncFromModel();
            }
            _grid.Items.Refresh();
            UpdateSummaryBanner();
            TaskDialog.Show("CSV Mapping Imported", $"Updated family mappings for {updated} block name(s).");
        }
    }

    private static System.Windows.Controls.Button CreateButton(string text, RoutedEventHandler onClick)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = text,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        btn.Click += onClick;
        return btn;
    }
}

public sealed record SymbolOptionItem(string DisplayText, FamilySymbol? Symbol);

public sealed class BlockValidationRowViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<SymbolOptionItem> _options;
    private SymbolOptionItem _selectedOption;

    public BlockValidationSummary Model { get; }

    public BlockValidationRowViewModel(BlockValidationSummary model, IReadOnlyList<SymbolOptionItem> options)
    {
        Model = model;
        _options = options;
        _selectedOption = FindMatchingOption(model.MatchedSymbol);
    }

    public string BlockName => Model.BlockName;
    public int InstanceCount => Model.InstanceCount;
    public string SampleLayer => Model.SampleLayer;
    public string ValidationStatusText => Model.ValidationStatusText;

    public bool PlaceSelected
    {
        get => Model.PlaceSelected;
        set
        {
            if (Model.PlaceSelected != value)
            {
                Model.PlaceSelected = value;
                OnPropertyChanged(nameof(PlaceSelected));
            }
        }
    }

    public SymbolOptionItem SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (!Equals(_selectedOption, value))
            {
                _selectedOption = value;
                Model.MatchedSymbol = value?.Symbol;
                Model.MatchKind = value?.Symbol is not null
                    ? BlockMatchKind.ManualOverride
                    : BlockMatchKind.Unmatched;
                Model.PlaceSelected = value?.Symbol is not null;

                OnPropertyChanged(nameof(SelectedOption));
                OnPropertyChanged(nameof(ValidationStatusText));
                OnPropertyChanged(nameof(PlaceSelected));
            }
        }
    }

    public void SyncFromModel()
    {
        _selectedOption = FindMatchingOption(Model.MatchedSymbol);
        OnPropertyChanged(nameof(SelectedOption));
        OnPropertyChanged(nameof(ValidationStatusText));
        OnPropertyChanged(nameof(PlaceSelected));
    }

    private SymbolOptionItem FindMatchingOption(FamilySymbol? symbol)
    {
        if (symbol is null)
        {
            return _options[0];
        }

        return _options.FirstOrDefault(o => o.Symbol?.Id == symbol.Id) ?? _options[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
