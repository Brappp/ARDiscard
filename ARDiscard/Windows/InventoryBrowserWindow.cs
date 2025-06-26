using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARDiscard.GameData;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using ImGuiNET;
using LLib;
using LLib.ImGui;

namespace ARDiscard.Windows;

internal sealed class InventoryBrowserWindow : LWindow
{
    private readonly InventoryUtils _inventoryUtils;
    private readonly ItemCache _itemCache;
    private readonly IconCache _iconCache;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly Configuration _configuration;
    private readonly IListManager _listManager;

    private List<InventoryGroup> _inventoryGroups = new();
    private readonly Dictionary<uint, bool> _expandedCategories = new();

    public event EventHandler? OpenConfigurationClicked;
    public event EventHandler<ItemFilter>? DiscardAllClicked;

    public InventoryBrowserWindow(InventoryUtils inventoryUtils, ItemCache itemCache, IconCache iconCache,
        IClientState clientState, ICondition condition, Configuration configuration, IListManager listManager)
        : base("Inventory Browser###AutoDiscardInventoryBrowser")
    {
        _inventoryUtils = inventoryUtils;
        _itemCache = itemCache;
        _iconCache = iconCache;
        _clientState = clientState;
        _condition = condition;
        _configuration = configuration;
        _listManager = listManager;

        Size = new Vector2(800, 600);
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(9999, 9999),
        };
    }

    public bool Locked { get; set; }

    public override void DrawContent()
    {
        ImGui.Text("Select items or entire groups to discard:");

        ImGui.BeginDisabled(Locked);
        if (ImGui.BeginChild("InventoryBrowser", new Vector2(-1, -60), true, ImGuiWindowFlags.NoSavedSettings))
        {
            if (!_clientState.IsLoggedIn)
            {
                ImGui.Text("Not logged in.");
            }
            else if (_inventoryGroups.Count == 0)
            {
                ImGui.Text("No items found in inventory.");
            }
            else
            {
                DrawInventoryGroups();
            }
        }

        ImGui.EndDisabled();
        ImGui.EndChild();

        // Bottom buttons
        DrawBottomButtons();
    }

    private void DrawInventoryGroups()
    {
        foreach (var group in _inventoryGroups.OrderBy(x => x.CategoryName))
        {
            DrawInventoryGroup(group);
        }
    }

    private void DrawInventoryGroup(InventoryGroup group)
    {
        // Get expansion state
        if (!_expandedCategories.ContainsKey(group.CategoryId))
            _expandedCategories[group.CategoryId] = _configuration.InventoryBrowser.ExpandAllGroups;

        bool isExpanded = _expandedCategories[group.CategoryId];
        
        // Check if entire category is selected
        bool categorySelected = _configuration.SelectedDiscardCategories.Contains(group.CategoryId);
        bool hasSelection = categorySelected || group.Items.Any(item => _configuration.SelectedDiscardItems.Contains(item.ItemId));

        // Category header with selection checkbox
        ImGui.PushID($"Category_{group.CategoryId}");
        
        // Checkbox for entire category
        if (ImGui.Checkbox("##CategorySelect", ref categorySelected))
        {
            if (categorySelected)
            {
                _configuration.SelectedDiscardCategories.Add(group.CategoryId);
                // Remove individual selections for items in this category since we're selecting the whole category
                foreach (var item in group.Items)
                {
                    _configuration.SelectedDiscardItems.Remove(item.ItemId);
                    _configuration.ExcludedFromCategoryDiscard.Remove(item.ItemId);
                }
            }
            else
            {
                _configuration.SelectedDiscardCategories.Remove(group.CategoryId);
                _configuration.ExcludedFromCategoryDiscard.Clear(); // Clear exclusions when deselecting category
            }
        }

        ImGui.SameLine();

        // Color the header if it has selections
        if (hasSelection)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);

        // Expandable tree node
        if (ImGui.TreeNodeEx($"{group.CategoryName} ({group.Items.Count} items)##Category_{group.CategoryId}",
                ImGuiTreeNodeFlags.None))
        {
            isExpanded = true;
            _expandedCategories[group.CategoryId] = true;

            if (hasSelection)
                ImGui.PopStyleColor();

            // Items in this category
            foreach (var item in group.Items.OrderBy(x => x.Name))
            {
                DrawInventoryItem(item, categorySelected);
            }

            ImGui.TreePop();
        }
        else
        {
            _expandedCategories[group.CategoryId] = false;
            if (hasSelection)
                ImGui.PopStyleColor();
        }

        ImGui.PopID();
    }

    private void DrawInventoryItem(InventoryItem item, bool categorySelected)
    {
        ImGui.PushID($"Item_{item.ItemId}");

        // Determine selection state
        bool itemSelected;
        bool isExcluded = _configuration.ExcludedFromCategoryDiscard.Contains(item.ItemId);
        
        if (categorySelected)
        {
            // If category is selected, this item is selected unless explicitly excluded
            itemSelected = !isExcluded;
        }
        else
        {
            // If category not selected, check individual selection
            itemSelected = _configuration.SelectedDiscardItems.Contains(item.ItemId);
        }

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

        // Check if item can actually be discarded
        bool canBeDiscarded = _itemCache.TryGetItem(item.ItemId, out var itemInfo) && 
                              itemInfo.CanBeDiscarded(_listManager, false);

        ImGui.BeginDisabled(!canBeDiscarded);
        
        // Checkbox for individual item
        if (ImGui.Checkbox("##ItemSelect", ref itemSelected))
        {
            if (categorySelected)
            {
                // Category is selected, so this is about exclusion
                if (itemSelected)
                {
                    // Remove from exclusion list (so it gets discarded with category)
                    _configuration.ExcludedFromCategoryDiscard.Remove(item.ItemId);
                }
                else
                {
                    // Add to exclusion list
                    _configuration.ExcludedFromCategoryDiscard.Add(item.ItemId);
                }
            }
            else
            {
                // Category not selected, so this is individual selection
                if (itemSelected)
                {
                    _configuration.SelectedDiscardItems.Add(item.ItemId);
                }
                else
                {
                    _configuration.SelectedDiscardItems.Remove(item.ItemId);
                }
            }
        }
        
        ImGui.EndDisabled();

        ImGui.SameLine();

        // Item name and quantity
        string displayText = item.Name;
        if (_configuration.InventoryBrowser.ShowItemCounts && item.Quantity > 1)
        {
            displayText += $" ({item.Quantity}x)";
        }

        // Color text based on selection state and whether item can be discarded
        if (!canBeDiscarded)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"{displayText} (Cannot discard)");
        }
        else if (itemSelected)
        {
            if (categorySelected && !isExcluded)
                ImGui.TextColored(ImGuiColors.HealerGreen, displayText);
            else
                ImGui.TextColored(ImGuiColors.DalamudYellow, displayText);
        }
        else if (categorySelected && isExcluded)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, $"{displayText} (Excluded)");
        }
        else
        {
            ImGui.Text(displayText);
        }

        // Show additional info on hover
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text($"Item ID: {item.ItemId}");
            ImGui.Text($"Category: {item.CategoryName}");
            if (_listManager.IsBlacklisted(item.ItemId))
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "This item is blacklisted and cannot be discarded");
            }
            else if (!canBeDiscarded && _itemCache.TryGetItem(item.ItemId, out var tooltipItemInfo))
            {
                if (tooltipItemInfo.UiCategory is UiCategories.Currency or UiCategories.Crystals or UiCategories.Unobtainable)
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "Cannot discard: Currency/Crystals/Unobtainable item");
                }
                else if (tooltipItemInfo.IsIndisposable)
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "Cannot discard: Item is indisposable");
                }
                else if (tooltipItemInfo.IsUnique && tooltipItemInfo.IsUntradable && !tooltipItemInfo.CanBeBoughtFromCalamitySalvager)
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "Cannot discard: Unique/Untradable item not available from salvager");
                }
            }
            ImGui.EndTooltip();
        }

        ImGui.PopID();
    }

    private void DrawBottomButtons()
    {
        // Refresh button
        if (ImGui.Button("Refresh Inventory"))
        {
            RefreshInventory();
        }

        ImGui.SameLine();

        // Clear all selections
        if (ImGui.Button("Clear All Selections"))
        {
            _configuration.SelectedDiscardItems.Clear();
            _configuration.SelectedDiscardCategories.Clear();
            _configuration.ExcludedFromCategoryDiscard.Clear();
        }

        // Configuration button
        ImGui.SameLine();
        ImGui.BeginDisabled(OpenConfigurationClicked == null);
        if (ImGui.Button("Open Configuration"))
            OpenConfigurationClicked!.Invoke(this, EventArgs.Empty);
        ImGui.EndDisabled();

        // Discard button
        ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                       ImGui.GetStyle().WindowPadding.X -
                       ImGui.CalcTextSize("Discard Selected Items").X -
                       ImGui.GetStyle().ItemSpacing.X);
        
        var selectedItems = GetSelectedItemIds();
        ImGui.BeginDisabled(Locked ||
                            !_clientState.IsLoggedIn ||
                            !(_condition[ConditionFlag.NormalConditions] || _condition[ConditionFlag.Mounted]) ||
                            selectedItems.Count == 0 ||
                            DiscardAllClicked == null);
        
        if (ImGui.Button("Discard Selected Items"))
        {
            DiscardAllClicked!.Invoke(this, new ItemFilter
            {
                ItemIds = selectedItems
            });
        }

        ImGui.EndDisabled();
    }

    private List<uint> GetSelectedItemIds()
    {
        var selectedIds = new HashSet<uint>();

        // Add individually selected items
        selectedIds.UnionWith(_configuration.SelectedDiscardItems);

        // Add items from selected categories (minus exclusions)
        foreach (var categoryId in _configuration.SelectedDiscardCategories)
        {
            var categoryItems = _inventoryGroups
                .FirstOrDefault(g => g.CategoryId == categoryId)?
                .Items?
                .Select(i => i.ItemId) ?? Enumerable.Empty<uint>();
            
            selectedIds.UnionWith(categoryItems);
        }

        // Remove exclusions
        selectedIds.ExceptWith(_configuration.ExcludedFromCategoryDiscard);

        return selectedIds.ToList();
    }

    public override void OnOpen() => RefreshInventory();

    public override void OnClose() => _inventoryGroups.Clear();

    public unsafe void RefreshInventory()
    {
        if (!IsOpen || !_clientState.IsLoggedIn)
            return;

        // Get all items that could potentially be discarded
        var allItems = _inventoryUtils.GetAllInventoryItems();

        // Group by category - show ALL items, don't filter by CanBeDiscarded
        _inventoryGroups = allItems
            .Where(wrapper => _itemCache.TryGetItem(wrapper.InventoryItem->ItemId, out var itemInfo)) // Only check if we have item info
            .GroupBy(wrapper =>
            {
                _itemCache.TryGetItem(wrapper.InventoryItem->ItemId, out var itemInfo);
                return new { 
                    CategoryId = itemInfo!.UiCategory, 
                    CategoryName = itemInfo.UiCategoryName 
                };
            })
            .Select(group => new InventoryGroup
            {
                CategoryId = group.Key.CategoryId,
                CategoryName = group.Key.CategoryName,
                Items = group.GroupBy(wrapper => wrapper.InventoryItem->ItemId)
                    .Select(itemGroup =>
                    {
                        var firstItem = itemGroup.First();
                        _itemCache.TryGetItem(firstItem.InventoryItem->ItemId, out var itemInfo);
                        return new InventoryItem
                        {
                            ItemId = firstItem.InventoryItem->ItemId,
                            Name = itemInfo!.Name,
                            IconId = itemInfo.IconId,
                            Quantity = itemGroup.Sum(w => w.InventoryItem->Quantity),
                            CategoryName = itemInfo.UiCategoryName
                        };
                    })
                    .ToList()
            })
            .ToList();
    }

    private sealed class InventoryGroup
    {
        public required uint CategoryId { get; init; }
        public required string CategoryName { get; init; }
        public required List<InventoryItem> Items { get; init; }
    }

    private sealed class InventoryItem
    {
        public required uint ItemId { get; init; }
        public required string Name { get; init; }
        public required ushort IconId { get; init; }
        public required long Quantity { get; init; }
        public required string CategoryName { get; init; }
    }
} 