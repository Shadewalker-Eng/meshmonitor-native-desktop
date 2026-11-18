using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private bool trailShown;

    private bool MapSearchHasFocus()
    {
        for (var element = FocusManager.GetFocusedElement(MapSearch.XamlRoot) as DependencyObject;
            element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == MapSearch) return true;
        }
        return false;
    }

    private static bool MatchesNode(NodeRow node, string query) =>
        node.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        node.ShortName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private SourceRow? FindMapSource(NodeRow node) =>
        (SourcesList.ItemsSource as SourceRow[])?.FirstOrDefault(source =>
            !source.IsUnified && source.Id == node.SourceId);

    private string? MapSourceName(NodeRow node) => FindMapSource(node)?.Name;

    private IEnumerable<NodeRow> FindMapNodes(string text)
    {
        string query = text.Trim();
        return query.Length == 0 ? [] : mapNodes.Where(node => MatchesNode(node, query));
    }

    private void UpdateMapSearch()
    {
        if (!MapSearch.IsSuggestionListOpen) return;
        SetMapSuggestions(MapSearch);
    }

    private void SetMapSuggestions(AutoSuggestBox box)
    {
        var suggestions = FindMapNodes(box.Text)
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(node => new MapSearchResult(node, MapSourceName(node)))
            .ToArray();
        box.ItemsSource = suggestions;
        box.IsSuggestionListOpen = suggestions.Length > 0;
    }


    private void OnMapSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            SetMapSuggestions(sender);
    }

    private async void OnMapSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string query = args.QueryText.Trim();
        if (query.Length == 0)
        {
            UpdateMapSummary();
            return;
        }
        var chosen = args.ChosenSuggestion as MapSearchResult;
        if (chosen is null)
        {
            var choices = FindMapNodes(query).OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(node => new MapSearchResult(node, MapSourceName(node))).ToArray();
            if (choices.Length == 0)
            {
                MapSummary.Text = "No matching positioned nodes in this view.";
                return;
            }
            chosen = choices.Length == 1 ? choices[0] : await ChooseMapNode(choices);
        }
        if (chosen is null || closed) return;
        var node = mapNodes.FirstOrDefault(item => item.Id == chosen.Node.Id &&
            item.SourceId == chosen.Node.SourceId);
        if (node is not null)
        {
            SelectMapNode(node, center: true);
            sender.Text = "";
            return;
        }
        MapSummary.Text = "The selected node is no longer positioned in this view.";
    }

    private async Task<MapSearchResult?> ChooseMapNode(MapSearchResult[] choices)
    {
        var list = new ListView
        {
            ItemsSource = choices,
            ItemTemplate = MapSearch.ItemTemplate,
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 360
        };
        var dialog = DetailDialog("Choose a node", list);
        MapSearchResult? chosen = null;
        list.ItemClick += (_, args) =>
        {
            chosen = args.ClickedItem as MapSearchResult;
            dialog.Hide();
        };
        await dialog.ShowAsync();
        return chosen;
    }

    private void SelectMapNode(NodeRow node, bool center)
    {
        if (selectedMapNode?.Id != node.Id || selectedMapNode.SourceId != node.SourceId)
            ClearPositionTrail();
        selectedMapNode = node;
        NodeMap.SelectNode(node, center);
        UpdateMapSummary();
    }

    private void OnPositionRangeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (PositionHistoryStatus is not null) ClearPositionTrail();
    }

    private async void OnPositionHistory(object sender, RoutedEventArgs args)
    {
        if (trailShown)
        {
            ClearPositionTrail();
            return;
        }
        if (selectedMapNode is not { } node || FindMapSource(node) is not { } source) return;
        int hours = PositionHistoryRange.SelectedIndex switch { 0 => 1, 2 => 168, _ => 24 };
        await RunAsync(async client =>
        {
            SetPositionHistoryStatus("Loading position history…");
            try
            {
                var history = await client.NodePositionHistory(source, node, hours);
                if (closed) return;
                var fixes = history.Fixes;
                if (fixes.Length == 0)
                {
                    SetPositionHistoryStatus("No position fixes in this time range.");
                    return;
                }
                trailShown = true;
                PositionHistoryToggle.Content = "Hide trail";
                SetPositionHistoryStatus(fixes.Length == 1
                    ? $"1 fix · {ValueFormat.Time(fixes[0].Time)}"
                    : $"{fixes.Length:N0} fixes · Start {ValueFormat.Time(fixes[0].Time)} · End {ValueFormat.Time(fixes[^1].Time)}" +
                        (history.Limited ? " · Showing the most recent fixes" : ""));
                NodeMap.SetTrail(fixes);
            }
            catch
            {
                if (!closed) SetPositionHistoryStatus("");
                throw;
            }
        });
    }

    private void SetPositionHistoryStatus(string text)
    {
        PositionHistoryStatus.Text = text;
        PositionHistoryStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearPositionTrail()
    {
        if (trailShown) NodeMap.SetTrail([]);
        trailShown = false;
        PositionHistoryToggle.Content = "Show trail";
        SetPositionHistoryStatus("");
    }
}

public sealed record MapSearchResult(NodeRow Node, string? Source)
{
    public string Name => Node.Name;
    public string Label => string.IsNullOrEmpty(Source)
        ? $"{Name} · {Node.Id}"
        : $"{Name} · {Node.Id} · {Source}";
}
