using System.Collections.Generic;
using System.Collections.Immutable;
using ARDiscard.Windows;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace ARDiscard.External;

internal sealed class AutoDiscardIpc
{
    private const string ItemsToDiscard = "ARDiscard.GetItemsToDiscard";
    private const string IsRunning = "ARDiscard.IsRunning";

    private readonly Configuration _configuration;
    private readonly UnifiedInventoryWindow _unifiedInventoryWindow;
    private readonly ICallGateProvider<IReadOnlySet<uint>> _getItemsToDiscard;
    private readonly ICallGateProvider<bool> _isRunning;

    public AutoDiscardIpc(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        UnifiedInventoryWindow unifiedInventoryWindow)
    {
        _configuration = configuration;
        _unifiedInventoryWindow = unifiedInventoryWindow;

        _getItemsToDiscard = pluginInterface.GetIpcProvider<IReadOnlySet<uint>>(ItemsToDiscard);
        _getItemsToDiscard.RegisterFunc(GetItemsToDiscard);

        _isRunning = pluginInterface.GetIpcProvider<bool>(IsRunning);
        _isRunning.RegisterFunc(CheckIsRunning);
    }

    public void Dispose()
    {
        _isRunning.UnregisterFunc();
        _getItemsToDiscard.UnregisterFunc();
    }

    private IReadOnlySet<uint> GetItemsToDiscard()
    {
        return _configuration.ItemsToDiscard.ToImmutableHashSet();
    }

    private bool CheckIsRunning() => _unifiedInventoryWindow.Locked;
}
