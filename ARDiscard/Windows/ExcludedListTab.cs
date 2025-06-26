using System.Collections.Generic;
using System.Linq;
using ARDiscard.GameData;

namespace ARDiscard.Windows;

internal sealed class ExcludedListTab : ItemListTab
{
    private readonly IListManager _listManager;

    public ExcludedListTab(UnifiedInventoryWindow parent, ItemCache itemCache, List<uint> initialItems, IListManager listManager)
        : base(parent, itemCache, initialItems)
    {
        _listManager = listManager;
        SelectedItems.AddRange(
            listManager.GetInternalBlacklist()
                .Select(x => (x, itemCache.GetItemName(x)))
                .Where(x => x.Item1 >= 100 && !string.IsNullOrEmpty(x.Item2)));
    }

    protected override string RightSideLabel => "Items that will never be discarded";

    public IEnumerable<uint> ToSavedItems()
    {
        return SelectedItems
            .Select(x => x.ItemId)
            .Where(x => !_listManager.IsBlacklisted(x, false));
    }

    public bool IsBlacklistedInConfiguration(uint itemId)
        => !_listManager.IsBlacklisted(itemId, false) && SelectedItems.Any(x => x.ItemId == itemId);

    protected override (string Name, bool Enabled) AsLeftSideDisplay(uint itemId, string name)
        => (name, !_listManager.IsBlacklisted(itemId, false));
}
