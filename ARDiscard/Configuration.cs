using System.Collections.Generic;
using Dalamud.Configuration;

namespace ARDiscard;

internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    
    /// <summary>
    /// Simple set of item IDs that should be discarded
    /// </summary>
    public HashSet<uint> ItemsToDiscard { get; set; } = new();
    
    public List<uint> BlacklistedItems { get; set; } = new();

    public ArmouryConfiguration Armoury { get; set; } = new();
    public ContextMenuConfiguration ContextMenu { get; set; } = new();
    public PreviewConfiguration Preview { get; set; } = new();
    public InventoryBrowserConfiguration InventoryBrowser { get; set; } = new();
    public MarketPriceConfiguration MarketPrice { get; set; } = new();



    public sealed class ArmouryConfiguration
    {
        public bool DiscardFromArmouryChest { get; set; }
        public bool CheckMainHandOffHand { get; set; }
        public bool CheckLeftSideGear { get; set; }
        public bool CheckRightSideGear { get; set; }
        public int MaximumGearItemLevel { get; set; } = 45;
    }

    public sealed class ContextMenuConfiguration
    {
        public bool Enabled { get; set; } = true;
        public bool OnlyWhenConfigIsOpen { get; set; }
    }

    public sealed class PreviewConfiguration
    {
        public bool GroupByCategory { get; set; } = true;
        public bool ShowIcons { get; set; } = true;
    }

    public sealed class InventoryBrowserConfiguration
    {
        public bool GroupByCategory { get; set; } = true;
        public bool ShowItemCounts { get; set; } = true;
        public bool ShowIcons { get; set; } = true;
        public bool ExpandAllGroups { get; set; }
    }

    public sealed class MarketPriceConfiguration
    {
        public bool ShowPrices { get; set; } = true;
        public bool ShowOnSeparateLine { get; set; } = false;
        public bool ShowTotalValue { get; set; } = true;
        
        /// <summary>
        /// Query data center instead of individual world for better price coverage
        /// </summary>
        public bool UseDataCenter { get; set; } = true;
        
        /// <summary>
        /// Fallback to data center if world query fails
        /// </summary>
        public bool FallbackToDataCenter { get; set; } = true;
        
        /// <summary>
        /// Show HQ indicator in price display
        /// </summary>
        public bool ShowHqIndicator { get; set; } = true;
        
        /// <summary>
        /// Cache timeout in minutes
        /// </summary>
        public int CacheTimeoutMinutes { get; set; } = 5;
    }

    /// <summary>
    /// Checks if an item should be discarded based on current configuration
    /// </summary>
    public bool ShouldDiscardItem(uint itemId)
    {
        return ItemsToDiscard.Contains(itemId);
    }

    public static Configuration CreateNew()
    {
        return new Configuration
        {
            BlacklistedItems = [2820]
        };
    }
}
