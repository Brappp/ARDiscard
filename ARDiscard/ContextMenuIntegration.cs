using System;
using ARDiscard.GameData;
using ARDiscard.Windows;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace ARDiscard;

internal sealed class ContextMenuIntegration : IDisposable
{
    private readonly IChatGui _chatGui;
    private readonly ItemCache _itemCache;
    private readonly Configuration _configuration;
    private readonly IListManager _listManager;
    private readonly ConfigWindow _configWindow;
    private readonly IGameGui _gameGui;
    private readonly IContextMenu _contextMenu;
    private readonly MenuItem _addInventoryItem;
    private readonly MenuItem _removeInventoryItem;

    public ContextMenuIntegration(IChatGui chatGui, ItemCache itemCache, Configuration configuration,
        IListManager listManager, ConfigWindow configWindow, IGameGui gameGui, IContextMenu contextMenu)
    {
        _chatGui = chatGui;
        _itemCache = itemCache;
        _configuration = configuration;
        _listManager = listManager;
        _configWindow = configWindow;
        _gameGui = gameGui;
        _contextMenu = contextMenu;
        _addInventoryItem = new MenuItem
        {
            Prefix = (SeIconChar)57439,
            PrefixColor = 52,
            Name = "Add to Auto Discard List",
            OnClicked = AddToDiscardList,
        };
        _removeInventoryItem = new MenuItem
        {
            Prefix = (SeIconChar)57439,
            PrefixColor = 52,
            Name = "Remove from Auto Discard List",
            OnClicked = RemoveFromDiscardList,
        };

        _contextMenu.OnMenuOpened += MenuOpened;
    }

    private void MenuOpened(IMenuOpenedArgs args)
    {
        if (!IsEnabled())
            return;

        if (args.Target is MenuTargetInventory targetInventory)
        {
            if (args.AddonName is not ("Inventory" or "InventoryExpansion" or "InventoryLarge" or "ArmouryBoard") ||
                targetInventory.TargetItem == null)
                return;

            var item = targetInventory.TargetItem.Value;
            if (!_configWindow.CanItemBeConfigured(item.ItemId))
                return;

            if (_configuration.SelectedDiscardItems.Contains(item.ItemId))
                args.AddMenuItem(_removeInventoryItem);
            else if (_itemCache.TryGetItem(item.ItemId, out ItemCache.CachedItemInfo? cachedItemInfo) &&
                     cachedItemInfo.CanBeDiscarded(_listManager))
                args.AddMenuItem(_addInventoryItem);
        }
        else
        {
            if (args.AddonName is not "ChatLog")
                return;

            uint itemId = (uint)_gameGui.HoveredItem;
            if (itemId > 1_000_000)
                itemId -= 1_000_000;

            if (itemId > 500_000)
                itemId -= 500_000;

            if (_configuration.SelectedDiscardItems.Contains(itemId))
            {
                args.AddMenuItem(new MenuItem
                {
                    Prefix = _removeInventoryItem.Prefix,
                    PrefixColor = _removeInventoryItem.PrefixColor,
                    Name = _removeInventoryItem.Name,
                    OnClicked = _ => RemoveFromDiscardList(itemId),
                });
            }
            else if (_itemCache.TryGetItem(itemId, out ItemCache.CachedItemInfo? cachedItemInfo) &&
                     cachedItemInfo.CanBeDiscarded(_listManager))
            {
                args.AddMenuItem(new MenuItem
                {
                    Prefix = _addInventoryItem.Prefix,
                    PrefixColor = _addInventoryItem.PrefixColor,
                    Name = _addInventoryItem.Name,
                    OnClicked = _ => AddToDiscardList(itemId),
                });
            }
        }
    }

    private void AddToDiscardList(IMenuItemClickedArgs args) =>
        AddToDiscardList(((MenuTargetInventory)args.Target).TargetItem!.Value.ItemId);

    private void AddToDiscardList(uint itemId)
    {
        if (_configWindow.AddToDiscardList(itemId))
        {
            _chatGui.Print(new SeString(new UIForegroundPayload(52))
                .Append($"\ue05f ")
                .Append(new UIForegroundPayload(0))
                .Append($"Added ")
                .Append(new UIForegroundPayload(52))
                .Append(new ItemPayload(itemId))
                .Append(_itemCache.GetItemName(itemId))
                .Append(RawPayload.LinkTerminator)
                .Append(new UIForegroundPayload(0))
                .Append(" to Auto Discard List."));
        }
    }

    private void RemoveFromDiscardList(IMenuItemClickedArgs args) =>
        RemoveFromDiscardList(((MenuTargetInventory)args.Target).TargetItem!.Value.ItemId);

    private void RemoveFromDiscardList(uint itemId)
    {
        if (_configWindow.RemoveFromDiscardList(itemId))
        {
            _chatGui.Print(new SeString(new UIForegroundPayload(52))
                .Append($"\ue05f ")
                .Append(new UIForegroundPayload(0))
                .Append($"Removed ")
                .Append(new UIForegroundPayload(52))
                .Append(new ItemPayload(itemId))
                .Append(_itemCache.GetItemName(itemId))
                .Append(RawPayload.LinkTerminator)
                .Append(new UIForegroundPayload(0))
                .Append(" from Auto Discard List."));
        }
    }

    private bool IsEnabled()
    {
        if (!_configuration.ContextMenu.Enabled)
            return false;

        if (_configuration.ContextMenu.OnlyWhenConfigIsOpen && !_configWindow.IsOpen)
            return false;

        return true;
    }

    public void Dispose()
    {
        _contextMenu.OnMenuOpened -= MenuOpened;
    }
}
