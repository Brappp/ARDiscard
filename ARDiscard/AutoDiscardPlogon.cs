using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ARDiscard.External;
using ARDiscard.GameData;
using ARDiscard.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib;

namespace ARDiscard;

[SuppressMessage("ReSharper", "UnusedType.Global")]
public sealed class AutoDiscardPlogon : IDalamudPlugin
{
    private readonly WindowSystem _windowSystem = new(nameof(AutoDiscardPlogon));
    private readonly Configuration _configuration;
    private readonly UnifiedInventoryWindow _unifiedInventoryWindow;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IChatGui _chatGui;
    private readonly IClientState _clientState;
    private readonly IPluginLog _pluginLog;
    private readonly IGameGui _gameGui;
    private readonly ICommandManager _commandManager;
    private readonly ICondition _condition;
    private readonly InventoryUtils _inventoryUtils;
    private readonly IconCache _iconCache;
    private readonly GameStrings _gameStrings;
    private readonly UniversalisClient _universalisClient;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Obsolete in ECommons")]
    private readonly TaskManager _taskManager;

    private readonly ContextMenuIntegration _contextMenuIntegration;
    private readonly AutoDiscardIpc _autoDiscardIpc;

    private DateTime _cancelDiscardAfter = DateTime.MaxValue;

    [SuppressMessage("Maintainability", "CA1506")]
    public AutoDiscardPlogon(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IChatGui chatGui,
        IDataManager dataManager, IClientState clientState, ICondition condition, IPluginLog pluginLog,
        IGameGui gameGui, ITextureProvider textureProvider, IContextMenu contextMenu)
    {
        ArgumentNullException.ThrowIfNull(dataManager);

        _pluginInterface = pluginInterface;
        _configuration = (Configuration?)_pluginInterface.GetPluginConfig() ?? Configuration.CreateNew();
        MigrateConfiguration(_configuration);
        _chatGui = chatGui;
        _clientState = clientState;
        _pluginLog = pluginLog;
        _gameGui = gameGui;
        _commandManager = commandManager;
        _condition = condition;
        _commandManager.AddHandler("/idm", new CommandInfo(HandleIdmCommand)
        {
            HelpMessage = "Inventory Discard Manager - Use '/idm' to open main window, '/idm config' for settings",
        });

        ListManager listManager = new ListManager(_configuration);
        ItemCache itemCache = new ItemCache(dataManager, listManager);
        _inventoryUtils = new InventoryUtils(_configuration, itemCache, listManager, _pluginLog);
        listManager.FinishInitialization();

        _iconCache = new IconCache(textureProvider);
        _gameStrings = new GameStrings(dataManager, pluginLog);
        _universalisClient = new UniversalisClient(_configuration);

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += OpenInventoryManager;
        _pluginInterface.UiBuilder.OpenConfigUi += OpenInventoryManager;

        _unifiedInventoryWindow = new(_inventoryUtils, itemCache, _iconCache, clientState, _configuration, listManager, _pluginInterface, condition, _universalisClient);
        _windowSystem.AddWindow(_unifiedInventoryWindow);

        _unifiedInventoryWindow.ConfigSaved += (_, _) => 
        {
            _unifiedInventoryWindow.RefreshInventory();
        };
        _unifiedInventoryWindow.DiscardSelectedClicked += (_, itemIds) =>
        {
            _taskManager?.Abort();
            _taskManager?.Enqueue(() => DiscardNextItem(PostProcessType.ManuallyStarted, new ItemFilter { ItemIds = itemIds }));
        };

        ECommonsMain.Init(_pluginInterface, this);
        _taskManager = new();
        _contextMenuIntegration = new(_chatGui, itemCache, _configuration, listManager, _unifiedInventoryWindow, _gameGui, contextMenu);
        _autoDiscardIpc = new(_pluginInterface, _configuration, _unifiedInventoryWindow);
    }

    private void MigrateConfiguration(Configuration configuration)
    {
        // Simplified migration - just ensure we're on the latest version
        if (configuration.Version < 4)
        {
            if (!configuration.BlacklistedItems.Contains(2820))
                configuration.BlacklistedItems.Add(2820);
            configuration.Version = 4;
            _pluginInterface.SavePluginConfig(configuration);
        }
    }



    private void HandleIdmCommand(string command, string arguments)
    {
        var args = arguments.Trim().ToUpperInvariant();
        
        switch (args)
        {
            case "CONFIG":
                // Open unified window and switch to settings tab
                OpenInventoryManager();
                break;
            default:
                OpenInventoryManager();
                break;
        }
    }

    private void OpenInventoryManager()
    {
        _unifiedInventoryWindow.IsOpen = !_unifiedInventoryWindow.IsOpen;
    }

    private unsafe void DiscardNextItem(PostProcessType type, ItemFilter? itemFilter)
    {
        _pluginLog.Information($"DiscardNextItem (type = {type})");
        _unifiedInventoryWindow.Locked = true;

        InventoryItem* nextItem = _inventoryUtils.GetNextItemToDiscard(itemFilter);
        if (nextItem == null)
        {
            _pluginLog.Information("No item to discard found");
            FinishDiscarding(type);
        }
        else
        {
            var (inventoryType, slot) = (nextItem->Container, nextItem->Slot);

            _pluginLog.Information(
                $"Discarding itemId {nextItem->ItemId} in slot {nextItem->Slot} of container {nextItem->Container}.");
            _inventoryUtils.Discard(nextItem);
            _cancelDiscardAfter = DateTime.Now.AddSeconds(15);

            _taskManager.EnqueueDelay(20);
            _taskManager.Enqueue(() => ConfirmDiscardItem(type, itemFilter, inventoryType, slot));
        }
    }

    private unsafe void ConfirmDiscardItem(PostProcessType type, ItemFilter? itemFilter, InventoryType inventoryType,
        short slot)
    {
        var addon = GetDiscardAddon();
        if (addon != null)
        {
            _pluginLog.Verbose("Addon is visible, clicking 'yes'");
            ((AddonSelectYesno*)addon)->YesButton->AtkComponentBase.SetEnabledState(true);
            addon->FireCallbackInt(0);

            _taskManager.EnqueueDelay(20);
            _taskManager.Enqueue(() => ContinueAfterDiscard(type, itemFilter, inventoryType, slot));
        }
        else
        {
            InventoryItem* nextItem = _inventoryUtils.GetNextItemToDiscard(itemFilter);
            if (nextItem == null)
            {
                _pluginLog.Information("Addon is not visible, but next item is also no longer set");
                FinishDiscarding(type);
            }
            else if (nextItem->Container == inventoryType && nextItem->Slot == slot)
            {
                _pluginLog.Information(
                    $"Addon is not (yet) visible, still trying to discard item in slot {slot} in inventory {inventoryType}");
                _taskManager.EnqueueDelay(100);
                _taskManager.Enqueue(() => ConfirmDiscardItem(type, itemFilter, inventoryType, slot));
            }
            else
            {
                _pluginLog.Information(
                    $"Addon is not (yet) visible, but slot or inventory type changed, retrying from start");
                _taskManager.EnqueueDelay(100);
                _taskManager.Enqueue(() => DiscardNextItem(type, itemFilter));
            }
        }
    }

    private unsafe void ContinueAfterDiscard(PostProcessType type, ItemFilter? itemFilter, InventoryType inventoryType,
        short slot)
    {
        InventoryItem* nextItem = _inventoryUtils.GetNextItemToDiscard(itemFilter);
        if (nextItem == null)
        {
            _pluginLog.Information($"Continuing after discard: no next item (type = {type})");
            FinishDiscarding(type);
        }
        else if (nextItem->Container == inventoryType && nextItem->Slot == slot)
        {
            if (_cancelDiscardAfter < DateTime.Now)
            {
                _pluginLog.Information("No longer waiting for plugin to pop up, assume discard failed");
                FinishDiscarding(type, "Discarding probably failed due to an error.");
            }
            else
            {
                _pluginLog.Verbose(
                    $"ContinueAfterDiscard: Waiting for server response until {_cancelDiscardAfter}");
                _taskManager.EnqueueDelay(20);
                _taskManager.Enqueue(() => ContinueAfterDiscard(type, itemFilter, inventoryType, slot));
            }
        }
        else
        {
            _pluginLog.Information("ContinueAfterDiscard: Discovered different item to discard");
            _taskManager.Enqueue(() => DiscardNextItem(type, itemFilter));
        }
    }

    private void FinishDiscarding(PostProcessType type, string? error = null)
    {
        if (string.IsNullOrEmpty(error))
            _chatGui.Print("Done discarding.");
        else
            _chatGui.PrintError(error);

        _unifiedInventoryWindow.Locked = false;
        _unifiedInventoryWindow.RefreshInventory();
    }

    public void Dispose()
    {
        _autoDiscardIpc.Dispose();
        _contextMenuIntegration.Dispose();
        _universalisClient.Dispose();
        ECommonsMain.Dispose();
        _iconCache.Dispose();

        _pluginInterface.UiBuilder.OpenConfigUi -= OpenInventoryManager;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenInventoryManager;
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _commandManager.RemoveHandler("/idm");
    }

    private unsafe AtkUnitBase* GetDiscardAddon()
    {
        for (int i = 1; i < 100; i++)
        {
            try
            {
                var addon = (AtkUnitBase*)_gameGui.GetAddonByName("SelectYesno", i);
                if (addon == null) return null;
                if (addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded)
                {
                    var textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
                    var text = MemoryHelper.ReadSeString(&textNode->NodeText).GetText();
                    _pluginLog.Information($"YesNo prompt: {text}");
                    if (_gameStrings.DiscardItem.IsMatch(text) || _gameStrings.DiscardCollectable.IsMatch(text))
                    {
                        return addon;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    public enum PostProcessType
    {
        ManuallyStarted,
    }
}
