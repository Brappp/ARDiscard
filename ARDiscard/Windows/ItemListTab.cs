using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARDiscard.GameData;
using ImGuiNET;

namespace ARDiscard.Windows;

internal abstract class ItemListTab
{
    private const int ResultLimit = 200;

    private readonly UnifiedInventoryWindow _parent;

    private List<(uint ItemId, string Name)> _searchResults = new();
    private string _itemName = string.Empty;
    private bool _resetKeyboardFocus = true;

    protected ItemListTab(UnifiedInventoryWindow parent, ItemCache itemCache, List<uint> initialItems)
    {
        _parent = parent;
        SelectedItems.AddRange(initialItems.Select(x => (x, itemCache.GetItemName(x))).ToList());
    }

    protected abstract string RightSideLabel { get; }
    protected List<(uint ItemId, string Name)> SelectedItems { get; } = new();

    public void Draw()
    {
        var ws = ImGui.GetWindowSize();
        if (ImGui.BeginChild("Left", new Vector2(Math.Max(10, ws.X / 2), -1), true))
        {
            if (!string.IsNullOrEmpty(_itemName))
            {
                if (_searchResults.Count > ResultLimit)
                    ImGui.Text($"Search ({ResultLimit:N0} out of {_searchResults.Count:N0} matches)");
                else
                    ImGui.Text($"Search ({_searchResults.Count:N0} matches)");
            }
            else
                ImGui.Text("Search");

            ImGui.SetNextItemWidth(ws.X / 2 - 20);
            if (_resetKeyboardFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _resetKeyboardFocus = false;
            }

            string previousName = _itemName;
            if (ImGui.InputText("", ref _itemName, 256, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                _resetKeyboardFocus = true;
                if (_searchResults.Count > 0)
                {
                    var itemToAdd = _searchResults.FirstOrDefault();
                    if (SelectedItems.All(x => x.ItemId != itemToAdd.ItemId))
                    {
                        SelectedItems.Add(itemToAdd);
                    }
                    else
                    {
                        SelectedItems.Remove(itemToAdd);
                    }

                    Save();
                }
            }

            if (previousName != _itemName)
                UpdateResults();

            ImGui.Separator();

            if (string.IsNullOrEmpty(_itemName))
            {
                ImGui.Text("Type item name...");
            }

            foreach (var (id, name) in _searchResults.Take(ResultLimit))
            {
                bool selected = SelectedItems.Any(x => x.Item1 == id);

                var display = AsLeftSideDisplay(id, name);
                if (!display.Enabled)
                    ImGui.BeginDisabled();

                if (ImGui.Selectable($"{display.Name}##Item{id}", selected))
                {
                    if (!selected)
                    {
                        SelectedItems.Add((id, name));
                    }
                    else
                    {
                        SelectedItems.Remove((id, name));
                    }

                    Save();
                }

                if (!display.Enabled)
                   ImGui.EndDisabled();
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("Right", new Vector2(-1, -1), true, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.Text(RightSideLabel);
            ImGui.Separator();

            List<(uint, string)> toRemove = new();
            foreach (var (id, name) in SelectedItems.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var display = AsLeftSideDisplay(id, name);
                if (!display.Enabled)
                    ImGui.BeginDisabled();

                if (ImGui.Selectable($"{display.Name}##Item{id}", true))
                    toRemove.Add((id, name));

                if (!display.Enabled)
                    ImGui.EndDisabled();
            }

            if (toRemove.Count > 0)
            {
                foreach (var tr in toRemove)
                    SelectedItems.Remove(tr);

                Save();
            }
        }

        ImGui.EndChild();
    }

    protected virtual (string Name, bool Enabled) AsLeftSideDisplay(uint itemId, string name) => (name, true);

    protected void Save() => _parent.Save();

    private void UpdateResults()
    {
        if (string.IsNullOrEmpty(_itemName))
            _searchResults = new();
        else
        {
            _searchResults = EnsureAllItemsLoaded().Where(x =>
                    x.Name.Contains(_itemName, StringComparison.CurrentCultureIgnoreCase)
                    || (uint.TryParse(_itemName, out uint itemId) && x.ItemId == itemId))
                .OrderBy(x => _itemName.Equals(x.Name, StringComparison.OrdinalIgnoreCase) ? string.Empty : x.Name)
                .ToList();
        }
    }

    protected List<(uint ItemId, string Name)> EnsureAllItemsLoaded() => _parent.EnsureAllItemsLoaded();
}
