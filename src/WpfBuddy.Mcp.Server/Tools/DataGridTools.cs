using System.ComponentModel;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class DataGridTools
{
    private readonly UiaAdapter _uia;
    private readonly AuditLog _audit;

    public DataGridTools(UiaAdapter uia, AuditLog audit)
    {
        _uia = uia;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_grid_get_rows", ReadOnly = true), Description("Return visible DataGrid/ListView rows.")]
    public string GridGetRows(
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null,
        [Description("Maximum number of rows to return. Optional; default 50.")] int maxRows = 50)
    {
        _audit.Record("wpf_grid_get_rows");
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found or does not support Grid pattern.");

        var rows = new List<object>();
        try
        {
            if (grid.Patterns.Grid.IsSupported)
            {
                var rowCount = grid.Patterns.Grid.Pattern.RowCount.ValueOrDefault;
                var colCount = grid.Patterns.Grid.Pattern.ColumnCount.ValueOrDefault;

                for (int r = 0; r < Math.Min(rowCount, maxRows); r++)
                {
                    var cells = new List<string?>();
                    for (int c = 0; c < colCount; c++)
                    {
                        try
                        {
                            var cell = grid.Patterns.Grid.Pattern.GetItem(r, c);
                            cells.Add(cell.Properties.Name.ValueOrDefault);
                        }
                        catch { cells.Add(null); }
                    }
                    rows.Add(new { rowIndex = r, cells });
                }
            }
            else
            {
                // Fallback: find DataItem/ListItem children
                var items = grid.FindAll(TreeScope.Children, grid.Automation.ConditionFactory.ByControlType(ControlType.DataItem));
                if (items.Length == 0)
                    items = grid.FindAll(TreeScope.Children, grid.Automation.ConditionFactory.ByControlType(ControlType.ListItem));

                foreach (var item in items.Take(maxRows))
                {
                    rows.Add(new { name = item.Properties.Name.ValueOrDefault, automationId = item.Properties.AutomationId.ValueOrDefault });
                }
            }
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        return JsonSerializer.Serialize(new { rowCount = rows.Count, rows }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_grid_get_columns", ReadOnly = true), Description("Return column headers and metadata.")]
    public string GridGetColumns(
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_grid_get_columns");
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found.");

        var columns = new List<object>();
        try
        {
            var headers = grid.FindAll(TreeScope.Descendants, grid.Automation.ConditionFactory.ByControlType(ControlType.HeaderItem));
            foreach (var header in headers)
            {
                columns.Add(new
                {
                    name = header.Properties.Name.ValueOrDefault,
                    automationId = header.Properties.AutomationId.ValueOrDefault
                });
            }
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        return JsonSerializer.Serialize(new { columnCount = columns.Count, columns }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_grid_get_cell", ReadOnly = true), Description("Get cell value by row and column index.")]
    public string GridGetCell(
        [Description("Zero-based row index of the target cell.")] int row,
        [Description("Zero-based column index of the target cell.")] int column,
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_grid_get_cell");
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found or does not support Grid pattern.");

        if (!grid.Patterns.Grid.IsSupported)
            return Error("Grid pattern not supported.");

        var gridRowCount = grid.Patterns.Grid.Pattern.RowCount.ValueOrDefault;
        var gridColCount = grid.Patterns.Grid.Pattern.ColumnCount.ValueOrDefault;
        if (row < 0 || row >= gridRowCount || column < 0 || column >= gridColCount)
            return Error($"Cell ({row},{column}) out of range (rowCount={gridRowCount}, columnCount={gridColCount}).");

        try
        {
            var cell = grid.Patterns.Grid.Pattern.GetItem(row, column);
            var value = cell.Properties.Name.ValueOrDefault;
            string? cellValue = null;
            try
            {
                if (cell.Patterns.Value.IsSupported)
                    cellValue = cell.Patterns.Value.Pattern.Value.ValueOrDefault;
            }
            catch { }

            return JsonSerializer.Serialize(new { row, column, name = value, value = cellValue ?? value }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_grid_set_cell", Destructive = true, Idempotent = true), Description("Edit cell value by row and column index.")]
    public string GridSetCell(
        [Description("Zero-based row index of the target cell.")] int row,
        [Description("Zero-based column index of the target cell.")] int column,
        [Description("New text value to write into the cell (overwrites existing content).")] string value,
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_grid_set_cell", parameters: new() { ["row"] = row, ["column"] = column, ["value"] = "***" });
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found.");

        if (!grid.Patterns.Grid.IsSupported)
            return Error("Grid pattern not supported.");

        var gridRowCount = grid.Patterns.Grid.Pattern.RowCount.ValueOrDefault;
        var gridColCount = grid.Patterns.Grid.Pattern.ColumnCount.ValueOrDefault;
        if (row < 0 || row >= gridRowCount || column < 0 || column >= gridColCount)
            return Error($"Cell ({row},{column}) out of range (rowCount={gridRowCount}, columnCount={gridColCount}).");

        try
        {
            var cell = grid.Patterns.Grid.Pattern.GetItem(row, column);
            if (cell.Patterns.Value.IsSupported)
            {
                cell.Patterns.Value.Pattern.SetValue(value);
                return Ok("cell_set");
            }
            // Fallback: double-click, clear existing content, then type
            cell.DoubleClick();
            Thread.Sleep(50);
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
            FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.DELETE);
            FlaUI.Core.Input.Keyboard.Type(value);
            return Ok("cell_typed");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_grid_select_row", Destructive = false, Idempotent = true), Description("Select row by index or by cell text.")]
    public string GridSelectRow(
        [Description("Zero-based row index to select. Provide either rowIndex or cellText.")] int? rowIndex = null,
        [Description("Cell text to match (case-insensitive contains); selects the first row whose content matches. Used when rowIndex is omitted.")] string? cellText = null,
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_grid_select_row");
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found.");

        try
        {
            if (rowIndex.HasValue && grid.Patterns.Grid.IsSupported)
            {
                var cell = grid.Patterns.Grid.Pattern.GetItem(rowIndex.Value, 0);
                if (cell.Patterns.SelectionItem.IsSupported)
                    cell.Patterns.SelectionItem.Pattern.Select();
                else
                    cell.Click();
                return Ok($"row_{rowIndex}_selected");
            }

            if (!string.IsNullOrEmpty(cellText))
            {
                var items = grid.FindAll(TreeScope.Descendants, grid.Automation.ConditionFactory.ByControlType(ControlType.DataItem));
                if (items.Length == 0)
                    items = grid.FindAll(TreeScope.Descendants, grid.Automation.ConditionFactory.ByControlType(ControlType.ListItem));
                foreach (var item in items)
                {
                    if (item.Properties.Name.ValueOrDefault?.Contains(cellText, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (item.Patterns.SelectionItem.IsSupported)
                            item.Patterns.SelectionItem.Pattern.Select();
                        else
                            item.Click();
                        return Ok("row_selected_by_text");
                    }
                }
                return Error($"Row with text '{cellText}' not found.");
            }

            return Error("Provide rowIndex or cellText.");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_grid_double_click_row", Destructive = false), Description("Double-click a row by index.")]
    public string GridDoubleClickRow(
        [Description("Zero-based row index to double-click.")] int rowIndex,
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_grid_double_click_row");
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found.");

        try
        {
            if (grid.Patterns.Grid.IsSupported)
            {
                var cell = grid.Patterns.Grid.Pattern.GetItem(rowIndex, 0);
                cell.DoubleClick();
                return Ok("row_double_clicked");
            }

            var items = grid.FindAll(TreeScope.Children, grid.Automation.ConditionFactory.ByControlType(ControlType.DataItem));
            if (rowIndex < items.Length)
            {
                items[rowIndex].DoubleClick();
                return Ok("row_double_clicked");
            }
            return Error($"Row index {rowIndex} out of range.");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_grid_find_row", ReadOnly = true), Description("Find row by column values.")]
    public string GridFindRow(
        [Description("Text to search for in row content (case-insensitive contains match). Required.")] string searchText,
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_grid_find_row");
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found.");

        try
        {
            var items = grid.FindAll(TreeScope.Descendants, grid.Automation.ConditionFactory.ByControlType(ControlType.DataItem));
            if (items.Length == 0)
                items = grid.FindAll(TreeScope.Descendants, grid.Automation.ConditionFactory.ByControlType(ControlType.ListItem));

            var matches = new List<object>();
            for (int i = 0; i < items.Length; i++)
            {
                var itemName = items[i].Properties.Name.ValueOrDefault ?? "";
                if (itemName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new { rowIndex = i, name = itemName, automationId = items[i].Properties.AutomationId.ValueOrDefault });
                }
            }

            return JsonSerializer.Serialize(new { matchCount = matches.Count, matches }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_grid_sort_by_column", Destructive = false), Description("Click column header to sort.")]
    public string GridSortByColumn(
        [Description("Column header text to sort by (case-insensitive contains match). Required.")] string columnName,
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_grid_sort_by_column");
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found.");

        try
        {
            var headers = grid.FindAll(TreeScope.Descendants, grid.Automation.ConditionFactory.ByControlType(ControlType.HeaderItem));
            var header = headers.FirstOrDefault(h => h.Properties.Name.ValueOrDefault?.Contains(columnName, StringComparison.OrdinalIgnoreCase) == true);
            if (header is null)
                return Error($"Column header '{columnName}' not found.");

            if (header.Patterns.Invoke.IsSupported)
                header.Patterns.Invoke.Pattern.Invoke();
            else
                header.Click();

            return Ok("column_sorted");
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_grid_scroll_to_row", Destructive = false), Description("Scroll grid until row with given text is visible.")]
    public string GridScrollToRow(
        [Description("Row text to scroll to (case-insensitive contains match). Required.")] string rowText,
        [Description("AutomationId of the target grid/list element.")] string? automationId = null,
        [Description("Element Name/content of the grid; used when automationId is omitted.")] string? name = null,
        [Description("Maximum number of scroll increments to attempt before giving up. Optional; default 20.")] int maxScrollAttempts = 20)
    {
        _audit.Record("wpf_grid_scroll_to_row");
        var grid = FindGrid(automationId, name);
        if (grid is null)
            return Error("Grid element not found.");

        for (int i = 0; i < maxScrollAttempts; i++)
        {
            var items = grid.FindAll(TreeScope.Descendants, grid.Automation.ConditionFactory.ByControlType(ControlType.DataItem));
            if (items.Length == 0)
                items = grid.FindAll(TreeScope.Descendants, grid.Automation.ConditionFactory.ByControlType(ControlType.ListItem));
            var match = items.FirstOrDefault(item => item.Properties.Name.ValueOrDefault?.Contains(rowText, StringComparison.OrdinalIgnoreCase) == true);
            if (match is not null)
            {
                if (match.Patterns.ScrollItem.IsSupported)
                    match.Patterns.ScrollItem.Pattern.ScrollIntoView();
                return Ok("row_found_and_scrolled");
            }

            if (grid.Patterns.Scroll.IsSupported)
            {
                grid.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                Thread.Sleep(100);
            }
            else break;
        }

        return Error($"Row with text '{rowText}' not found after scrolling.");
    }

    [McpServerTool(Name = "wpf_tree_get_nodes", ReadOnly = true), Description("Return visible TreeView nodes.")]
    public string TreeGetNodes(
        [Description("AutomationId of the target TreeView element.")] string? automationId = null,
        [Description("Element Name/content of the tree; used when automationId is omitted.")] string? name = null,
        [Description("Maximum depth of nested nodes to traverse. Optional; default 3.")] int maxDepth = 3)
    {
        _audit.Record("wpf_tree_get_nodes");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var tree = _uia.FindElement(criteria);
        if (tree is null)
            return Error("Tree element not found.");

        var nodes = GetTreeNodes(tree, maxDepth, 0);
        return JsonSerializer.Serialize(new { nodeCount = nodes.Count, nodes }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_tree_expand_path", Destructive = false, Idempotent = true), Description("Expand tree path such as 'Settings > Network > Devices'.")]
    public string TreeExpandPath(
        [Description("Node path to expand, separated by '>' (or ' > ' / ' → '), e.g. 'Settings > Network > Devices'. Each segment is matched case-insensitive contains. Required.")] string path,
        [Description("AutomationId of the target TreeView element.")] string? automationId = null,
        [Description("Element Name/content of the tree; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_tree_expand_path");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var tree = _uia.FindElement(criteria);
        if (tree is null)
            return Error("Tree element not found.");

        var parts = path.Split(new[] { ">", " > ", " → " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AutomationElement current = tree;

        foreach (var part in parts)
        {
            var treeItems = current.FindAll(TreeScope.Children, current.Automation.ConditionFactory.ByControlType(ControlType.TreeItem));
            var match = treeItems.FirstOrDefault(t => t.Properties.Name.ValueOrDefault?.Contains(part, StringComparison.OrdinalIgnoreCase) == true);
            if (match is null)
                return Error($"Tree node '{part}' not found.");

            if (match.Patterns.ExpandCollapse.IsSupported)
                match.Patterns.ExpandCollapse.Pattern.Expand();

            Thread.Sleep(100);
            current = match;
        }

        return Ok("path_expanded");
    }

    [McpServerTool(Name = "wpf_tree_select_path", Destructive = false, Idempotent = true), Description("Select tree node by path.")]
    public string TreeSelectPath(
        [Description("Node path to select, separated by '>' (or ' > ' / ' → '), e.g. 'Settings > Network > Devices'. Intermediate segments are expanded; the final segment is selected. Each segment is matched case-insensitive contains. Required.")] string path,
        [Description("AutomationId of the target TreeView element.")] string? automationId = null,
        [Description("Element Name/content of the tree; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_tree_select_path");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var tree = _uia.FindElement(criteria);
        if (tree is null)
            return Error("Tree element not found.");

        var parts = path.Split(new[] { ">", " > ", " → " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AutomationElement current = tree;
        AutomationElement? lastNode = null;

        foreach (var part in parts)
        {
            var treeItems = current.FindAll(TreeScope.Children, current.Automation.ConditionFactory.ByControlType(ControlType.TreeItem));
            var match = treeItems.FirstOrDefault(t => t.Properties.Name.ValueOrDefault?.Contains(part, StringComparison.OrdinalIgnoreCase) == true);
            if (match is null)
                return Error($"Tree node '{part}' not found.");

            if (match.Patterns.ExpandCollapse.IsSupported && part != parts.Last())
                match.Patterns.ExpandCollapse.Pattern.Expand();

            Thread.Sleep(100);
            current = match;
            lastNode = match;
        }

        if (lastNode is not null)
        {
            if (lastNode.Patterns.SelectionItem.IsSupported)
                lastNode.Patterns.SelectionItem.Pattern.Select();
            else
                lastNode.Click();
        }

        return Ok("path_selected");
    }

    [McpServerTool(Name = "wpf_tree_find_node", ReadOnly = true), Description("Find tree node by text or automation id.")]
    public string TreeFindNode(
        [Description("Text matched against node Name or AutomationId (case-insensitive contains). Required.")] string searchText,
        [Description("AutomationId of the TreeView to search within.")] string? treeAutomationId = null,
        [Description("Name/content of the TreeView to search within; used when treeAutomationId is omitted.")] string? treeName = null)
    {
        _audit.Record("wpf_tree_find_node");
        var criteria = new ElementCriteria { AutomationId = treeAutomationId, Name = treeName };
        var tree = _uia.FindElement(criteria);
        if (tree is null)
            return Error("Tree element not found.");

        var allTreeItems = tree.FindAll(TreeScope.Descendants, tree.Automation.ConditionFactory.ByControlType(ControlType.TreeItem));
        var matches = allTreeItems
            .Where(t => t.Properties.Name.ValueOrDefault?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true ||
                        t.Properties.AutomationId.ValueOrDefault?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
            .Select(t => new
            {
                name = t.Properties.Name.ValueOrDefault,
                automationId = t.Properties.AutomationId.ValueOrDefault,
                isExpanded = t.Patterns.ExpandCollapse.IsSupported ? t.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault.ToString() : null
            })
            .Take(10)
            .ToList();

        return JsonSerializer.Serialize(new { matchCount = matches.Count, matches }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_items_get", ReadOnly = true), Description("Get items from a generic ItemsControl.")]
    public string ItemsGet(
        [Description("AutomationId of the target ItemsControl container.")] string? automationId = null,
        [Description("Element Name/content of the container; used when automationId is omitted.")] string? name = null,
        [Description("Maximum number of items to return. Optional; default 50.")] int maxItems = 50)
    {
        _audit.Record("wpf_items_get");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var container = _uia.FindElement(criteria);
        if (container is null)
            return Error("Container element not found.");

        var items = new List<object>();
        var children = container.FindAll(TreeScope.Children, FlaUI.Core.Conditions.TrueCondition.Default);
        foreach (var child in children.Take(maxItems))
        {
            items.Add(new
            {
                name = child.Properties.Name.ValueOrDefault,
                automationId = child.Properties.AutomationId.ValueOrDefault,
                controlType = child.Properties.ControlType.ValueOrDefault.ToString(),
                isSelected = child.Patterns.SelectionItem.IsSupported ? child.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault : (bool?)null
            });
        }

        return JsonSerializer.Serialize(new { itemCount = items.Count, items }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_items_select", Destructive = false, Idempotent = true), Description("Select item in a generic ItemsControl by name or index.")]
    public string ItemsSelect(
        [Description("Item text to match (case-insensitive contains). Provide either itemName or index.")] string? itemName = null,
        [Description("Zero-based index of the item to select. Takes precedence over itemName when both are given.")] int? index = null,
        [Description("AutomationId of the target ItemsControl container.")] string? automationId = null,
        [Description("Element Name/content of the container; used when automationId is omitted.")] string? name = null)
    {
        _audit.Record("wpf_items_select");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var container = _uia.FindElement(criteria);
        if (container is null)
            return Error("Container element not found.");

        var children = container.FindAll(TreeScope.Children, FlaUI.Core.Conditions.TrueCondition.Default);

        AutomationElement? target = null;
        if (index.HasValue)
        {
            if (index.Value < 0 || index.Value >= children.Length)
                return Error($"index {index.Value} out of range (count={children.Length})");
            target = children[index.Value];
        }
        else if (!string.IsNullOrEmpty(itemName))
        {
            target = children.FirstOrDefault(c => c.Properties.Name.ValueOrDefault?.Contains(itemName, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (target is null)
            return Error("Item not found.");

        if (target.Patterns.SelectionItem.IsSupported)
            target.Patterns.SelectionItem.Pattern.Select();
        else
            target.Click();

        return Ok("item_selected");
    }

    private AutomationElement? FindGrid(string? automationId, string? name)
    {
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        return _uia.FindElement(criteria);
    }

    private List<object> GetTreeNodes(AutomationElement parent, int maxDepth, int currentDepth)
    {
        var nodes = new List<object>();
        if (currentDepth >= maxDepth) return nodes;

        var treeItems = parent.FindAll(TreeScope.Children, parent.Automation.ConditionFactory.ByControlType(ControlType.TreeItem));
        foreach (var item in treeItems)
        {
            var children = GetTreeNodes(item, maxDepth, currentDepth + 1);
            nodes.Add(new
            {
                name = item.Properties.Name.ValueOrDefault,
                automationId = item.Properties.AutomationId.ValueOrDefault,
                isExpanded = item.Patterns.ExpandCollapse.IsSupported ? item.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault.ToString() : null,
                childCount = children.Count,
                children
            });
        }
        return nodes;
    }

    private static string Ok(string result) =>
        JsonSerializer.Serialize(new { result }, JsonOptions.Default);

    private static string Error(string message) =>
        throw new McpException(message);
}
