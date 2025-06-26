using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace ARDiscard.GameData;

internal sealed class InventoryUtils
{
    private static readonly InventoryType[] DefaultInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4
    ];

    private static readonly InventoryType[] MainHandOffHandInventoryTypes =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand
    ];

    private static readonly InventoryType[] LeftSideGearInventoryTypes =
    [
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets
    ];

    private static readonly InventoryType[] RightSideGearInventoryTypes =
    [
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings
    ];

    private static readonly IReadOnlyList<uint> NoGearsetItems = new List<uint>();

    private readonly Configuration _configuration;
    private readonly ItemCache _itemCache;
    private readonly IListManager _listManager;
    private readonly IPluginLog _pluginLog;

    public InventoryUtils(Configuration configuration, ItemCache itemCache, IListManager listManager, IPluginLog pluginLog)
    {
        _configuration = configuration;
        _itemCache = itemCache;
        _listManager = listManager;
        _pluginLog = pluginLog;
    }

    public unsafe List<ItemWrapper> GetAllItemsToDiscard()
    {
        List<ItemWrapper> toDiscard = new List<ItemWrapper>();
        Dictionary<uint, int> itemCounts = new();

        InventoryManager* inventoryManager = InventoryManager.Instance();
        foreach (InventoryType inventoryType in DefaultInventoryTypes)
            toDiscard.AddRange(GetItemsToDiscard(inventoryManager, inventoryType, itemCounts, NoGearsetItems));

        if (_configuration.Armoury.DiscardFromArmouryChest)
        {
            var gearsetItems = GetAllGearsetItems();
            toDiscard.AddRange(GetArmouryItemsToDiscard(_configuration.Armoury.CheckMainHandOffHand, inventoryManager,
                MainHandOffHandInventoryTypes, itemCounts, gearsetItems));
            toDiscard.AddRange(GetArmouryItemsToDiscard(_configuration.Armoury.CheckLeftSideGear, inventoryManager,
                LeftSideGearInventoryTypes, itemCounts, gearsetItems));
            toDiscard.AddRange(GetArmouryItemsToDiscard(_configuration.Armoury.CheckRightSideGear, inventoryManager,
                RightSideGearInventoryTypes, itemCounts, gearsetItems));
        }

        return toDiscard;
    }

    public unsafe List<ItemWrapper> GetAllInventoryItems()
    {
        List<ItemWrapper> allItems = new List<ItemWrapper>();
        Dictionary<uint, int> itemCounts = new();

        InventoryManager* inventoryManager = InventoryManager.Instance();
        foreach (InventoryType inventoryType in DefaultInventoryTypes)
            allItems.AddRange(GetAllItemsInContainer(inventoryManager, inventoryType, itemCounts, NoGearsetItems));

        if (_configuration.Armoury.DiscardFromArmouryChest)
        {
            var gearsetItems = GetAllGearsetItems();
            allItems.AddRange(GetAllArmouryItems(_configuration.Armoury.CheckMainHandOffHand, inventoryManager,
                MainHandOffHandInventoryTypes, itemCounts, gearsetItems));
            allItems.AddRange(GetAllArmouryItems(_configuration.Armoury.CheckLeftSideGear, inventoryManager,
                LeftSideGearInventoryTypes, itemCounts, gearsetItems));
            allItems.AddRange(GetAllArmouryItems(_configuration.Armoury.CheckRightSideGear, inventoryManager,
                RightSideGearInventoryTypes, itemCounts, gearsetItems));
        }

        return allItems;
    }

    private unsafe ReadOnlyCollection<ItemWrapper> GetArmouryItemsToDiscard(bool condition, InventoryManager* inventoryManager,
        InventoryType[] inventoryTypes, Dictionary<uint, int> itemCounts, List<uint>? gearsetItems)
    {
        List<ItemWrapper> items = new();
        if (condition)
        {
            foreach (InventoryType inventoryType in inventoryTypes)
                items.AddRange(GetItemsToDiscard(inventoryManager, inventoryType, itemCounts, gearsetItems));
        }

        return items.AsReadOnly();
    }

    private unsafe ReadOnlyCollection<ItemWrapper> GetAllArmouryItems(bool condition, InventoryManager* inventoryManager,
        InventoryType[] inventoryTypes, Dictionary<uint, int> itemCounts, List<uint>? gearsetItems)
    {
        List<ItemWrapper> items = new();
        if (condition)
        {
            foreach (InventoryType inventoryType in inventoryTypes)
                items.AddRange(GetAllItemsInContainer(inventoryManager, inventoryType, itemCounts, gearsetItems));
        }

        return items.AsReadOnly();
    }

    private unsafe ReadOnlyCollection<ItemWrapper> GetAllItemsInContainer(InventoryManager* inventoryManager,
        InventoryType inventoryType, Dictionary<uint, int> itemCounts,
        IReadOnlyList<uint>? gearsetItems)
    {
        List<ItemWrapper> allItems = new List<ItemWrapper>();
        InventoryContainer* container = inventoryManager->GetInventoryContainer(inventoryType);
        
        for (int i = 0; i < container->Size; ++i)
        {
            var item = container->GetInventorySlot(i);
            if (item != null && item->ItemId != 0)
            {
                if (itemCounts.TryGetValue(item->ItemId, out int itemCount))
                    itemCounts[item->ItemId] = itemCount + item->Quantity;
                else
                    itemCounts[item->ItemId] = item->Quantity;

                // Add all items to the list (not just ones marked for discard)
                allItems.Add(new ItemWrapper { InventoryItem = item });
            }
        }

        return allItems.AsReadOnly();
    }

    public unsafe InventoryItem* GetNextItemToDiscard(ItemFilter? itemFilter)
    {
        List<ItemWrapper> allItemsToDiscard = GetAllItemsToDiscard();
        ItemWrapper? toDiscard = allItemsToDiscard.FirstOrDefault(x =>
            itemFilter == null || itemFilter.ItemIds.Contains(x.InventoryItem->ItemId));
        return toDiscard != null ? toDiscard.InventoryItem : null;
    }

    private unsafe ReadOnlyCollection<ItemWrapper> GetItemsToDiscard(InventoryManager* inventoryManager,
        InventoryType inventoryType, Dictionary<uint, int> itemCounts,
        IReadOnlyList<uint>? gearsetItems)
    {
        List<ItemWrapper> toDiscard = new List<ItemWrapper>();
        InventoryContainer* container = inventoryManager->GetInventoryContainer(inventoryType);
        //PluginLog.Verbose($"Checking {inventoryType}, {container->Size}");
        for (int i = 0; i < container->Size; ++i)
        {
            var item = container->GetInventorySlot(i);
            if (item != null && item->ItemId != 0)
            {
                if (itemCounts.TryGetValue(item->ItemId, out int itemCount))
                    itemCounts[item->ItemId] = itemCount + item->Quantity;
                else
                    itemCounts[item->ItemId] = item->Quantity;

                if (_listManager.IsBlacklisted(item->ItemId))
                    continue;

                if (!_itemCache.TryGetItem(item->ItemId, out ItemCache.CachedItemInfo? itemInfo) ||
                    !itemInfo.CanBeDiscarded(_listManager))
                    continue; // no info, who knows what that item is

                // skip gear if we're unable to load gearsets or it is used in a gearset
                if (itemInfo.EquipSlotCategory > 0 && (gearsetItems == null || gearsetItems.Contains(item->ItemId)))
                    continue;

                if (itemInfo is { EquipSlotCategory: > 0, CanBeBoughtFromCalamitySalvager: false } &&
                    itemInfo.ILvl >= _configuration.Armoury.MaximumGearItemLevel)
                    continue;



                //PluginLog.Verbose($"{i} → {item->ItemID}");
                if (_configuration.ShouldDiscardItem(item->ItemId, itemInfo.UiCategory))
                {
                    _pluginLog.Verbose(
                        $"Found item {item->ItemId} to discard in inventory {inventoryType} in slot {i}");
                    toDiscard.Add(new ItemWrapper { InventoryItem = item });
                }
            }
            else
            {
                //PluginLog.Verbose($"{i} → none");
            }
        }

        return toDiscard.AsReadOnly();
    }

    private unsafe List<uint>? GetAllGearsetItems()
    {
        var gearsetModule = RaptureGearsetModule.Instance();
        if (gearsetModule == null)
            return null;

        List<uint> allGearsetItems = new();
        for (int i = 0; i < 100; ++i)
        {
            var gearset = gearsetModule->GetGearset(i);
            if (gearset != null && gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
            {
                var gearsetItems = new[]
                {
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.MainHand),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.OffHand),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.Head),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.Body),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.Hands),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.Legs),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.Feet),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.Ears),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.Neck),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.Wrists),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.RingLeft),
                    gearset->GetItem(RaptureGearsetModule.GearsetItemIndex.RingRight),
                };
                foreach (var gearsetItem in gearsetItems)
                {
                    if (gearsetItem.ItemId != 0)
                        allGearsetItems.Add(gearsetItem.ItemId);
                }
            }
        }

        return allGearsetItems;
    }

    public unsafe void Discard(InventoryItem* item)
    {
        if (_listManager.IsBlacklisted(item->ItemId))
            throw new ArgumentException($"Can't discard {item->ItemId}", nameof(item));

        AgentInventoryContext.Instance()->DiscardItem(item, item->Container, item->Slot, 0);
    }

    public sealed unsafe class ItemWrapper
    {
        public required InventoryItem* InventoryItem { get; init; }
    }
}
