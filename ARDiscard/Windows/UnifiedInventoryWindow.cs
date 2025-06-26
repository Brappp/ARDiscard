using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARDiscard.GameData;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ImGuiNET;
using LLib;
using LLib.ImGui;

namespace ARDiscard.Windows;

internal sealed class UnifiedInventoryWindow : LWindow
{
    private readonly InventoryUtils _inventoryUtils;
    private readonly ItemCache _itemCache;
    private readonly IconCache _iconCache;
    private readonly IClientState _clientState;
    private readonly Configuration _configuration;
    private readonly IListManager _listManager;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICondition _condition;

    private List<CategoryGroup> _categories = new();
    private string _searchFilter = string.Empty;
    private readonly Dictionary<uint, bool> _expandedCategories = new();
    private readonly ExcludedListTab _excludedListTab;
    private List<(uint ItemId, string Name)>? _allItems;

    public event EventHandler<List<uint>>? DiscardSelectedClicked;
    public event EventHandler? ConfigSaved;
    public bool Locked { get; set; }

    public UnifiedInventoryWindow(
        InventoryUtils inventoryUtils, 
        ItemCache itemCache, 
        IconCache iconCache,
        IClientState clientState, 
        Configuration configuration,
        IListManager listManager,
        IDalamudPluginInterface pluginInterface,
        ICondition condition)
        : base("Inventory Manager###InventoryManager")
    {
        _inventoryUtils = inventoryUtils;
        _itemCache = itemCache;
        _iconCache = iconCache;
        _clientState = clientState;
        _configuration = configuration;
        _listManager = listManager;
        _pluginInterface = pluginInterface;
        _condition = condition;

        Size = new Vector2(800, 900);
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(9999, 9999),
        };

        _excludedListTab = new ExcludedListTab(this, itemCache, _configuration.BlacklistedItems, listManager);
    }

    public override void DrawContent()
    {
        if (ImGui.BeginTabBar("InventoryTabs"))
        {
            if (ImGui.BeginTabItem("Browse & Select"))
            {
                DrawBrowseTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Preview Discard"))
            {
                DrawPreviewTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawBrowseTab()
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
        DrawBrowseButtons();
    }

    private void DrawPreviewTab()
    {
        ImGui.Text("Items marked for discard:");
        ImGui.Separator();

        if (ImGui.BeginChild("PreviewList", new Vector2(-1, -60), true))
        {
            var itemsToDiscard = GetItemsToDiscard();
            
            if (itemsToDiscard.Count == 0)
            {
                ImGui.Text("No items selected for discard.");
            }
            else
            {
                if (_configuration.Preview.GroupByCategory)
                {
                    foreach (var category in itemsToDiscard
                        .GroupBy(x => x.CategoryName)
                        .OrderBy(g => g.Key))
                    {
                        ImGui.Text($"{category.Key}");
                        ImGui.Indent();
                        foreach (var item in category.OrderBy(i => i.Name))
                        {
                            DrawPreviewItem(item);
                        }
                        ImGui.Unindent();
                    }
                }
                else
                {
                    foreach (var item in itemsToDiscard.OrderBy(i => i.Name))
                    {
                        DrawPreviewItem(item);
                    }
                }
            }
        }
        ImGui.EndChild();

        // Bottom buttons
        DrawPreviewButtons();
    }

    private void DrawPreviewItem(PreviewItem item)
    {
        if (_configuration.Preview.ShowIcons)
        {
            using IDalamudTextureWrap? icon = _iconCache.GetIcon(item.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.ImGuiHandle, new Vector2(23, 23));
                ImGui.SameLine();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3);
            }
        }

        string text = item.Name;
        if (item.Quantity > 1)
            text += $" ({item.Quantity}x)";
            
        ImGui.Text(text);
    }

    private List<PreviewItem> GetItemsToDiscard()
    {
        if (!_clientState.IsLoggedIn)
            return new List<PreviewItem>();

        return _categories
            .SelectMany(c => c.Items)
            .Where(i => _configuration.ItemsToDiscard.Contains(i.ItemId))
            .GroupBy(i => i.ItemId)
            .Select(g => new PreviewItem
            {
                ItemId = g.Key,
                Name = g.First().Name,
                IconId = g.First().IconId,
                CategoryName = g.First().CategoryName,
                Quantity = g.Sum(i => i.Quantity)
            })
            .ToList();
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
        
        // Only consider discardable items for category selection logic
        var discardableItems = category.Items.Where(i => i.CanBeDiscarded).ToList();
        bool categorySelected = discardableItems.Count > 0 && discardableItems.All(item => _configuration.ItemsToDiscard.Contains(item.ItemId));
        bool categoryPartial = discardableItems.Any(item => _configuration.ItemsToDiscard.Contains(item.ItemId)) && !categorySelected;
        
        // Disable the category checkbox if no items can be discarded
        ImGui.BeginDisabled(discardableItems.Count == 0);
        
        if (categoryPartial)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, ImGuiColors.DalamudOrange);
            ImGui.PushStyleColor(ImGuiCol.CheckMark, ImGuiColors.DalamudOrange);
        }
        
        if (ImGui.Checkbox("##CategorySelect", ref categorySelected))
        {
            // Select/deselect all discardable items in this category
            foreach (var item in discardableItems)
            {
                if (categorySelected)
                    _configuration.ItemsToDiscard.Add(item.ItemId);
                else
                    _configuration.ItemsToDiscard.Remove(item.ItemId);
            }
        }
        
        if (categoryPartial)
        {
            ImGui.PopStyleColor(2);
        }
        
        ImGui.EndDisabled();
        
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
            // Double-check that we can discard this item before modifying the list
            if (item.CanBeDiscarded)
            {
                if (itemSelected)
                    _configuration.ItemsToDiscard.Add(item.ItemId);
                else
                    _configuration.ItemsToDiscard.Remove(item.ItemId);
            }
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
            ImGui.Text($"Quantity: {item.Quantity:N0}");
            if (item.ItemLevel > 0)
                ImGui.Text($"Item Level: {item.ItemLevel}");
            if (!item.CanBeDiscarded)
                ImGui.TextColored(ImGuiColors.DalamudRed, "This item cannot be discarded");
            ImGui.EndTooltip();
        }

        ImGui.PopID();
    }

    private void DrawBrowseButtons()
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
        if (ImGui.Button("Clear All"))
        {
            _configuration.ItemsToDiscard.Clear();
        }

        // Settings button removed - now handled by tabs
    }

    private void DrawPreviewButtons()
    {
        var selectedCount = _configuration.ItemsToDiscard.Count;
        
        // Center the discard button
        string buttonText = $"Discard Selected Items ({selectedCount})";
        float buttonWidth = ImGui.CalcTextSize(buttonText).X + ImGui.GetStyle().FramePadding.X * 2;
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float centerPos = (availableWidth - buttonWidth) * 0.5f;
        
        if (centerPos > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + centerPos);

        ImGui.BeginDisabled(selectedCount == 0 || DiscardSelectedClicked == null || Locked);
        
        if (ImGui.Button(buttonText))
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
                        CategoryName = itemGroup.First().ItemInfo!.UiCategoryName,
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

        // Clean up any non-discardable items from the discard list
        CleanupDiscardList();
    }

    private void CleanupDiscardList()
    {
        // Get all current inventory item IDs that cannot be discarded
        var nonDiscardableIds = _categories
            .SelectMany(c => c.Items)
            .Where(i => !i.CanBeDiscarded)
            .Select(i => i.ItemId)
            .ToHashSet();

        // Remove any non-discardable items from the discard list
        var removedAny = false;
        foreach (var itemId in nonDiscardableIds)
        {
            if (_configuration.ItemsToDiscard.Remove(itemId))
            {
                removedAny = true;
            }
        }

        // Save if we removed anything
        if (removedAny)
        {
            _pluginInterface.SavePluginConfig(_configuration);
        }
    }

    private void DrawSettingsTab()
    {
        if (ImGui.BeginTabBar("SettingsTabs"))
        {
            if (ImGui.BeginTabItem("Excluded Items"))
            {
                ImGui.Text("Items configured here will never be discarded, and have priority over the 'Items to Discard' tab.");
                ImGui.Text("Some items (such as Ultimate weapons) can not be un-blacklisted.");

                _excludedListTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("General Settings"))
            {
                bool discardFromArmouryChest = _configuration.Armoury.DiscardFromArmouryChest;
                if (ImGui.Checkbox("Discard items from Armoury Chest", ref discardFromArmouryChest))
                {
                    _configuration.Armoury.DiscardFromArmouryChest = discardFromArmouryChest;
                    Save();
                }

                ImGui.BeginDisabled(!discardFromArmouryChest);
                ImGui.Indent(30);

                bool mainHandOffHand = _configuration.Armoury.CheckMainHandOffHand;
                if (ImGui.Checkbox("Discard when items are found in Main Hand/Off Hand (Weapons and Tools)",
                        ref mainHandOffHand))
                {
                    _configuration.Armoury.CheckMainHandOffHand = mainHandOffHand;
                    Save();
                }

                bool leftSideGear = _configuration.Armoury.CheckLeftSideGear;
                if (ImGui.Checkbox("Discard when items are found in Head/Body/Hands/Legs/Feet", ref leftSideGear))
                {
                    _configuration.Armoury.CheckLeftSideGear = leftSideGear;
                    Save();
                }

                bool rightSideGear = _configuration.Armoury.CheckRightSideGear;
                if (ImGui.Checkbox("Discard when items are found in Accessories", ref rightSideGear))
                {
                    _configuration.Armoury.CheckRightSideGear = rightSideGear;
                    Save();
                }

                ImGui.SetNextItemWidth(ImGuiHelpers.GlobalScale * 100);
                int maximumItemLevel = _configuration.Armoury.MaximumGearItemLevel;
                if (ImGui.InputInt("Ignore items >= this ilvl (Armoury Chest only)",
                        ref maximumItemLevel))
                {
                    _configuration.Armoury.MaximumGearItemLevel =
                        Math.Max(0, Math.Min(_itemCache.MaxDungeonItemLevel, maximumItemLevel));
                    Save();
                }

                ImGui.Unindent(30);
                ImGui.EndDisabled();

                ImGui.Separator();

                bool contextMenuEnabled = _configuration.ContextMenu.Enabled;
                if (ImGui.Checkbox("Inventory context menu integration", ref contextMenuEnabled))
                {
                    _configuration.ContextMenu.Enabled = contextMenuEnabled;
                    Save();
                }

                ImGui.BeginDisabled(!contextMenuEnabled);
                ImGui.Indent(30);
                bool contextMenuOnlyWhenConfigIsOpen = _configuration.ContextMenu.OnlyWhenConfigIsOpen;
                if (ImGui.Checkbox("Only add menu entries while config window is open",
                        ref contextMenuOnlyWhenConfigIsOpen))
                {
                    _configuration.ContextMenu.OnlyWhenConfigIsOpen = contextMenuOnlyWhenConfigIsOpen;
                    Save();
                }

                ImGui.Unindent(30);
                ImGui.EndDisabled();

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Display Settings"))
            {
                bool groupPreviewByCategory = _configuration.Preview.GroupByCategory;
                if (ImGui.Checkbox("Group items in 'Preview' by category", ref groupPreviewByCategory))
                {
                    _configuration.Preview.GroupByCategory = groupPreviewByCategory;
                    Save();
                }

                bool showIconsInPreview = _configuration.Preview.ShowIcons;
                if (ImGui.Checkbox("Show icons in 'Preview'", ref showIconsInPreview))
                {
                    _configuration.Preview.ShowIcons = showIconsInPreview;
                    Save();
                }

                ImGui.Separator();
                ImGui.Text("Inventory Browser Settings");

                bool browserShowIcons = _configuration.InventoryBrowser.ShowIcons;
                if (ImGui.Checkbox("Show icons in Inventory Browser", ref browserShowIcons))
                {
                    _configuration.InventoryBrowser.ShowIcons = browserShowIcons;
                    Save();
                }

                bool browserShowItemCounts = _configuration.InventoryBrowser.ShowItemCounts;
                if (ImGui.Checkbox("Show item counts in Inventory Browser", ref browserShowItemCounts))
                {
                    _configuration.InventoryBrowser.ShowItemCounts = browserShowItemCounts;
                    Save();
                }

                bool browserExpandAllGroups = _configuration.InventoryBrowser.ExpandAllGroups;
                if (ImGui.Checkbox("Expand all groups by default in Inventory Browser", ref browserExpandAllGroups))
                {
                    _configuration.InventoryBrowser.ExpandAllGroups = browserExpandAllGroups;
                    Save();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Help & Info"))
            {
                DrawHelpAndInfo();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHelpAndInfo()
    {
        if (ImGui.CollapsingHeader("What Items Cannot Be Discarded?", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("The plugin uses conservative logic to prevent accidentally discarding valuable or irreplaceable items. Items CANNOT be discarded if they fall into any of these categories:");
            
            ImGui.Spacing();
            ImGui.BulletText("Special/Preorder Items");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Preorder earrings (Ala Mhigan, Aetheryte, Menphina's, Azeyma's)");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Ultimate raid tokens (UCOB, UWU, TEA, DSR, TOP)");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Magitek repair materials and similar special items");
            ImGui.Unindent();

            ImGui.BulletText("Currency & Resources");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• All currencies (Gil, Tomestones, MGP, etc.)");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• All crystals and shards");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Unobtainable items");
            ImGui.Unindent();

            ImGui.BulletText("Unique/Untradeable Items");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Items marked as 'Unique' AND 'Untradeable'");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Job quest rewards and MSQ rewards");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Achievement rewards");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Items the game marks as 'Indisposable'");
            ImGui.Unindent();

            ImGui.BulletText("User-Configured Exclusions");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Items manually added to the 'Excluded Items' list");
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("What Items CAN Be Discarded?"))
        {
            ImGui.TextWrapped("Items CAN be discarded if they meet any of these criteria:");
            
            ImGui.Spacing();
            ImGui.BulletText("Regular Items");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Items that are NOT unique, ARE tradeable, and CAN be discarded by the game");
            ImGui.Unindent();

            ImGui.BulletText("Calamity Salvager Items");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Items that can be repurchased from the Calamity Salvager");
            ImGui.Unindent();

            ImGui.BulletText("Whitelisted Items");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Normal raid materials (Deltascape, Sigmascape, etc.)");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• FATE drops used for mounts/glamour");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Collectables shop items");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Certain quest rewards that are safe to discard");
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Safety Features"))
        {
            ImGui.TextWrapped("The plugin includes several safety features:");
            
            ImGui.Spacing();
            ImGui.BulletText("Conservative Logic");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• When in doubt, items are marked as non-discardable");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Only items that are confirmed safe are allowed for discard");
            ImGui.Unindent();

            ImGui.BulletText("Visual Feedback");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Non-discardable items are grayed out with disabled checkboxes");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Tooltips explain why items cannot be discarded");
            ImGui.Unindent();

            ImGui.BulletText("Preview Before Discard");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Always review items in the 'Preview Discard' tab before confirming");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "• Only items that can actually be discarded will appear in the preview");
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Commands"))
        {
            ImGui.TextWrapped("Available commands:");
            
            ImGui.Spacing();
            ImGui.BulletText("/idm");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Opens the main Inventory Discard Manager window");
            ImGui.Unindent();

            ImGui.BulletText("/idm config");
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Opens this window and switches to the Settings tab");
            ImGui.Unindent();
        }
    }

    internal List<(uint ItemId, string Name)> EnsureAllItemsLoaded()
    {
        if (_allItems == null)
        {
            _allItems = _itemCache.AllItems
                .Where(x => x.CanBeDiscarded(_listManager, false))
                .Select(x => (x.ItemId, x.Name.ToString()))
                .ToList();
        }

        return _allItems;
    }

    internal void Save()
    {
        _configuration.BlacklistedItems = _excludedListTab.ToSavedItems().ToList();
        _pluginInterface.SavePluginConfig(_configuration);

        ConfigSaved?.Invoke(this, EventArgs.Empty);
    }

    internal bool AddToDiscardList(uint itemId) 
    {
        // Verify the item can be discarded before adding
        if (_itemCache.TryGetItem(itemId, out var itemInfo) && itemInfo.CanBeDiscarded(_listManager))
        {
            _configuration.ItemsToDiscard.Add(itemId);
            Save();
            return true;
        }
        return false;
    }

    internal bool RemoveFromDiscardList(uint itemId) 
    {
        bool removed = _configuration.ItemsToDiscard.Remove(itemId);
        if (removed)
            Save();
        return removed;
    }

    public bool CanItemBeConfigured(uint itemId)
    {
        return EnsureAllItemsLoaded().SingleOrDefault(x => x.ItemId == itemId).ItemId == itemId;
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
        public required string CategoryName { get; init; }
        public required int Quantity { get; init; }
        public required uint ItemLevel { get; init; }
        public required bool CanBeDiscarded { get; init; }
    }

    private sealed class PreviewItem
    {
        public required uint ItemId { get; init; }
        public required string Name { get; init; }
        public required ushort IconId { get; init; }
        public required string CategoryName { get; init; }
        public required int Quantity { get; init; }
    }
} 