using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private readonly DispatcherQueueTimer playbackTimer;
    private PositionFix[] playbackFixes = [];
    private sealed record ReplayTrack(NodeRow Node, PositionFix[] Fixes);
    private ReplayTrack[] replayTracks = [];

    private async void OnReplayNodes(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (selectedMapNode is not { } marker || FindMapSource(marker) is not { Type: "meshtastic_tcp" } source) return;
        PausePlayback();
        var choices = mapNodes.Where(node => node.SourceId == source.Id).OrderBy(node => node.Name).ToArray();
        var picker = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 280,
            ItemsSource = choices.Select(node => $"{node.Name} · {node.Id}").ToArray() };
        int initial = Array.FindIndex(choices, node => node.Id == marker.Id);
        if (initial >= 0) picker.SelectedItems.Add(picker.Items[initial]);
        var error = DialogError();
        var dialog = Form("Replay nodes on " + source.Name, "Load history", picker, error);
        dialog.PrimaryButtonClick += (_, action) =>
        {
            if (picker.SelectedItems.Count >= 2) return;
            action.Cancel = true;
            error.Text = "Select at least two nodes.";
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
        int hours = PositionHistoryRange.SelectedIndex switch { 0 => 1, 2 => 168, _ => 24 };
        var tracks = new List<ReplayTrack>();
        var notes = new List<string>();
        foreach (var item in picker.SelectedItems)
        {
            var node = choices[picker.Items.IndexOf(item)];
            var history = await client.NodePositionHistory(source, node, hours);
            if (closed) return;
            if (history.Fixes.Length > 0) tracks.Add(new(node, history.Fixes));
            if (history.Limited || history.Fixes.Length == 0)
                notes.Add($"{node.Name}: {(history.Limited ? "recent fixes only" : "no fixes")}");
        }
        ClearPositionTrail();
        if (tracks.Count > 0)
        {
            replayTracks = tracks.ToArray();
            trailShown = true;
            PositionHistoryToggle.Content = "Hide replay";
            var timeline = tracks.SelectMany(track => track.Fixes).OrderBy(fix => fix.Time).DistinctBy(fix => fix.Time).ToArray();
            NodeMap.StartGroupReplay(tracks.SelectMany(track => track.Fixes), tracks.Count);
            SetPlaybackHistory(timeline);
        }
        SetPositionHistoryStatus(string.Join(" · ", new[] { $"{tracks.Count} nodes with recorded positions" }.Concat(notes)));
    });

    private void DisplayReplayClock()
    {
        PlaybackClock.Text = $"Playback time {playbackTime.ToLocalTime():G}";
        if (replayTracks.Length == 0) return;
        var labels = new List<string>();
        for (int i = 0; i < replayTracks.Length; i++)
        {
            var track = replayTracks[i];
            int lo = 0, hi = track.Fixes.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (track.Fixes[mid].Time <= playbackTime) lo = mid + 1;
                else hi = mid;
            }
            var fix = lo == 0 ? null : track.Fixes[lo - 1];
            NodeMap.SetGroupFix(i, fix);
            labels.Add(fix is null ? $"{i + 1}. {track.Node.Name} · awaiting first fix" :
                $"{i + 1}. {track.Node.Name} · {fix.Latitude:F5}, {fix.Longitude:F5} · last fix {TimeSpan.FromSeconds(Math.Floor((playbackTime - fix.Time).TotalSeconds)):c} ago");
        }
        PlaybackFix.Text = string.Join("\n", labels);
    }

    private int currentFix;
    private bool updatingFixSlider;
    private double playbackRate = 600;
    private DateTimeOffset playbackTime;
    private long previousTick;

    private void SetPlaybackHistory(PositionFix[] fixes)
    {
        PausePlayback();
        playbackFixes = fixes;
        UpdatePlaybackVisibility();
        if (fixes.Length == 0)
        {
            NodeMap.SetPlaybackFix(-1);
            return;
        }
        updatingFixSlider = true;
        FixSlider.Maximum = Math.Max(1, fixes.Length - 1);
        updatingFixSlider = false;
        PlayTrail.IsEnabled = FixSlider.IsEnabled = fixes.Length > 1;
        SeekToFix(0);
    }

    private void UpdatePlaybackVisibility()
    {
        bool visible = desktop.Trails && playbackFixes.Length > 0;
        if (!visible) PausePlayback();
        PlaybackPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PausePlayback()
    {
        playbackTimer?.Stop();
        if (PlayTrail is not null)
            PlayTrail.Content = playbackFixes.Length > 1 && currentFix == playbackFixes.Length - 1 ? "Replay" : "Play";
    }

    private void ShowFix(int index)
    {
        currentFix = index;
        updatingFixSlider = true;
        FixSlider.Value = index;
        updatingFixSlider = false;
        var fix = playbackFixes[index];
        PlaybackFix.Text = $"Fix {index + 1:N0} of {playbackFixes.Length:N0} · {fix.Time.ToLocalTime():G} · {fix.Latitude:F5}, {fix.Longitude:F5}";
        PreviousFix.IsEnabled = index > 0;
        NextFix.IsEnabled = index + 1 < playbackFixes.Length;
        if (replayTracks.Length == 0) NodeMap.SetPlaybackFix(index);
    }

    private void SeekToFix(int index)
    {
        if (index < 0 || index >= playbackFixes.Length) return;
        ShowFix(index);
        playbackTime = playbackFixes[index].Time;
        DisplayReplayClock();
        PausePlayback();
    }

    private void OnSeekFix(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!updatingFixSlider) SeekToFix((int)Math.Round(args.NewValue));
    }

    private void OnPreviousFix(object sender, RoutedEventArgs args) => SeekToFix(currentFix - 1);
    private void OnNextFix(object sender, RoutedEventArgs args) => SeekToFix(currentFix + 1);

    private void OnPlayTrail(object sender, RoutedEventArgs args)
    {
        if (playbackFixes.Length < 2) return;
        if (playbackTimer.IsRunning)
        {
            AdvancePlayback();
            PausePlayback();
            return;
        }
        if (currentFix == playbackFixes.Length - 1) SeekToFix(0);
        previousTick = Stopwatch.GetTimestamp();
        PlayTrail.Content = "Pause";
        playbackTimer.Start();
    }

    private void OnPlaybackSpeedChanged(object sender, SelectionChangedEventArgs args)
    {
        if (playbackTimer?.IsRunning == true) AdvancePlayback();
        playbackRate = ((ComboBox)sender).SelectedIndex switch { 0 => 1, 1 => 60, 3 => 3600, _ => 600 };
    }

    private void AdvancePlayback()
    {
        if (playbackFixes.Length < 2 || !playbackTimer.IsRunning) return;
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(previousTick, now).TotalSeconds * playbackRate;
        previousTick = now;
        var end = playbackFixes[^1].Time;
        playbackTime = seconds >= (end - playbackTime).TotalSeconds ? end : playbackTime.AddSeconds(seconds);
        int index = currentFix;
        while (index + 1 < playbackFixes.Length && playbackFixes[index + 1].Time <= playbackTime) index++;
        if (index != currentFix) ShowFix(index);
        DisplayReplayClock();
        if (index == playbackFixes.Length - 1) PausePlayback();
    }
}
