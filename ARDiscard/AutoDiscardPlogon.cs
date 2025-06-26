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
    private readonly ConfigWindow _configWindow;
    private readonly DiscardWindow _discardWindow;
    private readonly InventoryBrowserWindow _inventoryBrowserWindow;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IChatGui _chatGui;
    private readonly IClientState _clientState;
    private readonly IPluginLog _pluginLog;
    private readonly IGameGui _gameGui;
    private readonly ICommandManager _commandManager;
    private readonly InventoryUtils _inventoryUtils;
    private readonly IconCache _iconCache;
    private readonly GameStrings _gameStrings;

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
        _commandManager.AddHandler("/discardconfig", new CommandInfo(OpenConfig)
        {
            HelpMessage = "Configures which items to automatically discard",
        });

        _commandManager.AddHandler("/discard", new CommandInfo(OpenDiscardWindow)
        {
            HelpMessage = "Show what will be discarded with your current configuration",
        });
        _commandManager.AddHandler("/inventorybrowser", new CommandInfo(OpenInventoryBrowser)
        {
            HelpMessage = "Open the inventory browser to select items for discard",
        });

        ListManager listManager = new ListManager(_configuration);
        ItemCache itemCache = new ItemCache(dataManager, listManager);
        _inventoryUtils = new InventoryUtils(_configuration, itemCache, listManager, _pluginLog);
        listManager.FinishInitialization();

        _iconCache = new IconCache(textureProvider);
        _gameStrings = new GameStrings(dataManager, pluginLog);

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += OpenInventoryBrowser;
        _pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        _discardWindow = new(_inventoryUtils, itemCache, _iconCache, clientState, condition, _configuration);
        _windowSystem.AddWindow(_discardWindow);

        _inventoryBrowserWindow = new(_inventoryUtils, itemCache, _iconCache, clientState, condition, _configuration, listManager);
        _windowSystem.AddWindow(_inventoryBrowserWindow);

        _configWindow = new(_pluginInterface, _configuration, itemCache, listManager, clientState, condition);
        _windowSystem.AddWindow(_configWindow);

        _configWindow.DiscardNowClicked += (_, _) => OpenDiscardWindow(string.Empty, string.Empty);
        _configWindow.OpenInventoryBrowserClicked += (_, _) => OpenInventoryBrowser();
        _configWindow.ConfigSaved += (_, _) => 
        {
            _discardWindow.RefreshInventory(true);
            _inventoryBrowserWindow.RefreshInventory();
        };
        _discardWindow.OpenConfigurationClicked += (_, _) => OpenConfigUi();
        _discardWindow.DiscardAllClicked += (_, filter) =>
        {
            _taskManager?.Abort();
            _taskManager?.Enqueue(() => DiscardNextItem(PostProcessType.ManuallyStarted, filter));
        };
        _inventoryBrowserWindow.OpenConfigurationClicked += (_, _) => OpenConfigUi();
        _inventoryBrowserWindow.DiscardAllClicked += (_, filter) =>
        {
            _taskManager?.Abort();
            _taskManager?.Enqueue(() => DiscardNextItem(PostProcessType.ManuallyStarted, filter));
        };

        ECommonsMain.Init(_pluginInterface, this);
        _taskManager = new();
        _contextMenuIntegration = new(_chatGui, itemCache, _configuration, listManager, _configWindow, _gameGui, contextMenu);
        _autoDiscardIpc = new(_pluginInterface, _configuration, _discardWindow);

        _clientState.Login += _discardWindow.Login;
        _clientState.Logout += _discardWindow.Logout;
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



    private void OpenConfig(string command, string arguments) => OpenConfigUi();

    private void OpenConfigUi()
    {
        _configWindow.IsOpen = !_configWindow.IsOpen;
    }



    private void OpenDiscardWindow(string command, string arguments) => OpenDiscardUi();

    private void OpenDiscardUi()
    {
        _discardWindow.IsOpen = !_discardWindow.IsOpen;
    }

    private void OpenInventoryBrowser(string command, string arguments) => OpenInventoryBrowser();

    private void OpenInventoryBrowser()
    {
        _inventoryBrowserWindow.IsOpen = !_inventoryBrowserWindow.IsOpen;
    }

    private unsafe void DiscardNextItem(PostProcessType type, ItemFilter? itemFilter)
    {
        _pluginLog.Information($"DiscardNextItem (type = {type})");
        _discardWindow.Locked = true;

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

        _discardWindow.Locked = false;
        _discardWindow.RefreshInventory(true);
    }

    public void Dispose()
    {
        _clientState.Login -= _discardWindow.Login;
        _clientState.Logout -= _discardWindow.Logout;

        _autoDiscardIpc.Dispose();
        _contextMenuIntegration.Dispose();
        ECommonsMain.Dispose();
        _iconCache.Dispose();

        _pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenInventoryBrowser;
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _commandManager.RemoveHandler("/inventorybrowser");
        _commandManager.RemoveHandler("/discard");
        _commandManager.RemoveHandler("/discardconfig");
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
