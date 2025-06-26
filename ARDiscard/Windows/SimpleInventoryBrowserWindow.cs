using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARDiscard.GameData;
using Dalamud.Interface.Colors;
using LLib;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using ImGuiNET;
using LLib.ImGui;

namespace ARDiscard.Windows;

internal sealed class SimpleInventoryBrowserWindow : LWindow
{
    private readonly InventoryUtils _inventoryUtils;
    private readonly ItemCache _itemCache;
    private readonly IconCache _iconCache;
    private readonly IClientState _clientState;
    private readonly Configuration _configuration;
    private readonly IListManager _listManager;

    private List<CategoryGroup> _categories = new();
    private string _searchFilter = string.Empty;
    private readonly Dictionary<uint, bool> _expandedCategories = new();

    public event EventHandler<List<uint>>? DiscardSelectedClicked;

    public SimpleInventoryBrowserWindow(
        InventoryUtils inventoryUtils, 
        ItemCache itemCache, 
        IconCache iconCache,
        IClientState clientState, 
        Configuration configuration,
        IListManager listManager)
        : base("Inventory Browser###InventoryBrowser")
    {
        _inventoryUtils = inventoryUtils;
        _itemCache = itemCache;
        _iconCache = iconCache;
        _clientState = clientState;
        _configuration = configuration;
        _listManager = listManager;

        Size = new Vector2(700, 800);
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(9999, 9999),
        };
    }

    public override void DrawContent()
    {
        // Search bar
        ImGui.Text("Search:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##Search", ref _searchFilter, 256);

        ImGui.Separator();

        // Main content
        if (ImGui.BeginChild("InventoryList", new Vector2(-1, -60), true))
        {
            if (!_clientState.IsLoggedIn)
            {
                ImGui.Text("Not logged in.");
            }
            else if (_categories.Count == 0)
            {
                ImGui.Text("No items in inventory.");
            }
            else
            {
                DrawCategories();
            }
        }
        ImGui.EndChild();

        // Bottom buttons
        DrawBottomButtons();
    }

    private void DrawCategories()
    {
        foreach (var category in _categories.OrderBy(c => c.CategoryName))
        {
            // Filter check
            if (!string.IsNullOrEmpty(_searchFilter))
            {
                bool hasMatch = category.Items.Any(item => 
                    item.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase));
                
                if (!hasMatch)
                    continue;
            }

            DrawCategory(category);
        }
    }

    private void DrawCategory(CategoryGroup category)
    {
        ImGui.PushID($"Cat_{category.CategoryId}");

        // Get expanded state
        bool isExpanded = _expandedCategories.GetValueOrDefault(category.CategoryId, false);

        // Check if any items in this category are selected for discard
        bool hasSelectedItems = category.Items.Any(item => _configuration.ItemsToDiscard.Contains(item.ItemId));

        // Category header with selection checkbox
        ImGui.PushID("CategoryHeader");
        
        // Category-wide selection checkbox
        bool categorySelected = category.Items.All(item => _configuration.ItemsToDiscard.Contains(item.ItemId));
        bool categoryPartial = hasSelectedItems && !categorySelected;
        
        if (categoryPartial)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, ImGuiColors.DalamudOrange);
            ImGui.PushStyleColor(ImGuiCol.CheckMark, ImGuiColors.DalamudOrange);
        }
        
        if (ImGui.Checkbox("##CategorySelect", ref categorySelected))
        {
            // Select/deselect all discardable items in this category
            foreach (var item in category.Items)
            {
                if (item.CanBeDiscarded)
                {
                    if (categorySelected)
                        _configuration.ItemsToDiscard.Add(item.ItemId);
                    else
                        _configuration.ItemsToDiscard.Remove(item.ItemId);
                }
            }
        }
        
        if (categoryPartial)
        {
            ImGui.PopStyleColor(2);
        }
        
        ImGui.PopID();
        ImGui.SameLine();

        // Category header text with color based on selection
        string headerText = $"{category.CategoryName} ({category.TotalItems:N0} items, {category.TotalQuantity:N0} total)";
        
        if (hasSelectedItems)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);

        if (ImGui.TreeNodeEx(headerText, isExpanded ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
        {
            if (hasSelectedItems)
                ImGui.PopStyleColor();

            _expandedCategories[category.CategoryId] = true;

            // Draw items
            foreach (var item in category.Items.OrderBy(i => i.Name))
            {
                if (!string.IsNullOrEmpty(_searchFilter) && 
                    !item.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                DrawItem(item);
            }

            ImGui.TreePop();
        }
        else
        {
            if (hasSelectedItems)
                ImGui.PopStyleColor();
                
            _expandedCategories[category.CategoryId] = false;
        }

        ImGui.PopID();
    }

    private void DrawItem(InventoryItemInfo item)
    {
        ImGui.PushID($"Item_{item.ItemId}");

        // Checkbox for individual item selection
        bool itemSelected = _configuration.ItemsToDiscard.Contains(item.ItemId);
        
        ImGui.BeginDisabled(!item.CanBeDiscarded);
        
        if (ImGui.Checkbox("##ItemSelect", ref itemSelected))
        {
            if (itemSelected)
                _configuration.ItemsToDiscard.Add(item.ItemId);
            else
                _configuration.ItemsToDiscard.Remove(item.ItemId);
        }
        
        ImGui.EndDisabled();
        ImGui.SameLine();

        // Icon
        if (_configuration.InventoryBrowser.ShowIcons)
        {
            using IDalamudTextureWrap? icon = _iconCache.GetIcon(item.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.ImGuiHandle, new Vector2(20, 20));
                ImGui.SameLine();
            }
        }

        // Item text with color coding
        string itemText = item.Name;
        if (item.Quantity > 1)
            itemText += $" x{item.Quantity:N0}";

        if (!item.CanBeDiscarded)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"{itemText} (Cannot discard)");
        }
        else if (itemSelected)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, itemText);
        }
        else
        {
            ImGui.Text(itemText);
        }

        // Tooltip on hover
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text($"Item ID: {item.ItemId}");
            ImGui.Text($"Icon ID: {item.IconId}");
            ImGui.Text($"Quantity: {item.Quantity:N0}");
            if (item.ItemLevel > 0)
                ImGui.Text($"Item Level: {item.ItemLevel}");
            if (!item.CanBeDiscarded)
                ImGui.TextColored(ImGuiColors.DalamudRed, "This item cannot be discarded");
            ImGui.EndTooltip();
        }

        ImGui.PopID();
    }

    private void DrawBottomButtons()
    {
        if (ImGui.Button("Refresh"))
            RefreshInventory();
        
        ImGui.SameLine();
        if (ImGui.Button("Expand All"))
        {
            foreach (var cat in _categories)
                _expandedCategories[cat.CategoryId] = true;
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Collapse All"))
        {
            _expandedCategories.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear All Selections"))
        {
            _configuration.ItemsToDiscard.Clear();
        }

        // Discard button
        ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                       ImGui.GetStyle().WindowPadding.X -
                       ImGui.CalcTextSize("Discard Selected Items").X -
                       ImGui.GetStyle().ItemSpacing.X);

        var selectedCount = _configuration.ItemsToDiscard.Count;
        ImGui.BeginDisabled(selectedCount == 0 || DiscardSelectedClicked == null);
        
        if (ImGui.Button($"Discard Selected Items ({selectedCount})"))
        {
            DiscardSelectedClicked?.Invoke(this, _configuration.ItemsToDiscard.ToList());
        }
        
        ImGui.EndDisabled();
    }

    public override void OnOpen()
    {
        RefreshInventory();
    }

    public override void OnClose()
    {
        _categories.Clear();
        _expandedCategories.Clear();
    }

    public unsafe void RefreshInventory()
    {
        if (!_clientState.IsLoggedIn)
            return;

        _categories.Clear();

        // Get all inventory items
        var allItems = _inventoryUtils.GetAllInventoryItems();

        // Group by category
        var grouped = allItems
            .Where(wrapper => wrapper.InventoryItem != null && wrapper.InventoryItem->ItemId != 0)
            .Select(wrapper => 
            {
                var item = wrapper.InventoryItem;
                _itemCache.TryGetItem(item->ItemId, out var itemInfo);
                return new
                {
                    ItemId = item->ItemId,
                    Quantity = (int)item->Quantity,
                    ItemInfo = itemInfo
                };
            })
            .Where(x => x.ItemInfo != null)
            .GroupBy(x => new { x.ItemInfo!.UiCategory, x.ItemInfo.UiCategoryName })
            .Select(categoryGroup => new CategoryGroup
            {
                CategoryId = categoryGroup.Key.UiCategory,
                CategoryName = categoryGroup.Key.UiCategoryName,
                Items = categoryGroup
                    .GroupBy(x => x.ItemId)
                    .Select(itemGroup => new InventoryItemInfo
                    {
                        ItemId = itemGroup.Key,
                        Name = itemGroup.First().ItemInfo!.Name,
                        IconId = itemGroup.First().ItemInfo!.IconId,
                        Quantity = itemGroup.Sum(x => x.Quantity),
                        ItemLevel = itemGroup.First().ItemInfo!.ILvl,
                        CanBeDiscarded = itemGroup.First().ItemInfo!.CanBeDiscarded(_listManager)
                    })
                    .ToList(),
                TotalItems = categoryGroup.Select(x => x.ItemId).Distinct().Count(),
                TotalQuantity = categoryGroup.Sum(x => x.Quantity)
            })
            .ToList();

        _categories = grouped;

        // Auto-expand categories with few items
        foreach (var category in _categories.Where(c => c.Items.Count <= 5))
        {
            _expandedCategories[category.CategoryId] = true;
        }
    }

    private sealed class CategoryGroup
    {
        public required uint CategoryId { get; init; }
        public required string CategoryName { get; init; }
        public required List<InventoryItemInfo> Items { get; init; }
        public required int TotalItems { get; init; }
        public required int TotalQuantity { get; init; }
    }

    private sealed class InventoryItemInfo  
    {
        public required uint ItemId { get; init; }
        public required string Name { get; init; }
        public required ushort IconId { get; init; }
        public required int Quantity { get; init; }
        public required uint ItemLevel { get; init; }
        public required bool CanBeDiscarded { get; init; }
    }
} 