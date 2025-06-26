using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARDiscard.GameData;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ImGuiNET;
using LLib.ImGui;

namespace ARDiscard.Windows;

internal sealed class ConfigWindow : LWindow
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly ItemCache _itemCache;
    private readonly IListManager _listManager;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly ExcludedListTab _excludedListTab;

    private List<(uint ItemId, string Name)>? _allItems;

    public event EventHandler? DiscardNowClicked;
    public event EventHandler? OpenInventoryBrowserClicked;
    public event EventHandler? ConfigSaved;

    public ConfigWindow(IDalamudPluginInterface pluginInterface, Configuration configuration, ItemCache itemCache,
        IListManager listManager, IClientState clientState, ICondition condition)
        : base("Auto Discard###AutoDiscardConfig")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _itemCache = itemCache;
        _listManager = listManager;
        _clientState = clientState;
        _condition = condition;

        Size = new Vector2(600, 400);
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
        ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                       ImGui.GetStyle().WindowPadding.X -
                       ImGui.CalcTextSize("Preview Discards").X -
                       ImGui.GetStyle().ItemSpacing.X);
        ImGui.BeginDisabled(!_clientState.IsLoggedIn ||
                            !(_condition[ConditionFlag.NormalConditions] || _condition[ConditionFlag.Mounted]) ||
                            DiscardNowClicked == null);
        if (ImGui.Button("Preview Discards"))
            DiscardNowClicked!.Invoke(this, EventArgs.Empty);
        ImGui.EndDisabled();

        if (ImGui.BeginTabBar("AutoDiscardTabs"))
        {
            DrawInventoryBrowser();
            DrawExcludedItems();
            DrawExperimentalSettings();

            ImGui.EndTabBar();
        }
    }

    private void DrawInventoryBrowser()
    {
        if (ImGui.BeginTabItem("Item Selection"))
        {
            ImGui.TextWrapped("Use the Inventory Browser to select items or entire categories for automatic discard.");
            ImGui.Spacing();
            
            if (ImGui.Button("Open Inventory Browser"))
            {
                // Signal to open the inventory browser window
                OpenInventoryBrowserClicked?.Invoke(this, EventArgs.Empty);
            }
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            
            // Show current selection summary
            ImGui.Text("Current Selection Summary:");
            ImGui.Indent();
            
            if (_configuration.SelectedDiscardCategories.Count > 0)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, $"Selected Categories: {_configuration.SelectedDiscardCategories.Count}");
            }
            
            if (_configuration.SelectedDiscardItems.Count > 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"Individual Items: {_configuration.SelectedDiscardItems.Count}");
            }
            
            if (_configuration.ExcludedFromCategoryDiscard.Count > 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, $"Excluded from Categories: {_configuration.ExcludedFromCategoryDiscard.Count}");
            }
            
            if (_configuration.SelectedDiscardCategories.Count == 0 && _configuration.SelectedDiscardItems.Count == 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "No items selected for discard");
            }
            
            ImGui.Unindent();
            
            ImGui.EndTabItem();
        }
    }



    private void DrawExcludedItems()
    {
        if (ImGui.BeginTabItem("Excluded Items"))
        {
            ImGui.Text(
                "Items configured here will never be discarded, and have priority over the 'Items to Discard' tab.");
            ImGui.Text("Some items (such as Ultimate weapons) can not be un-blacklisted.");

            _excludedListTab.Draw();
            ImGui.EndTabItem();
        }
    }

    private void DrawExperimentalSettings()
    {
        if (ImGui.BeginTabItem("Experimental Settings"))
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

            ImGui.Separator();

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
        _configuration.SelectedDiscardItems.Add(itemId);
        Save();
        return true;
    }

    internal bool RemoveFromDiscardList(uint itemId) 
    {
        bool removed = _configuration.SelectedDiscardItems.Remove(itemId);
        if (removed)
            Save();
        return removed;
    }

    public bool CanItemBeConfigured(uint itemId)
    {
        return EnsureAllItemsLoaded().SingleOrDefault(x => x.ItemId == itemId).ItemId == itemId;
    }
}
