using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeshMonitor.Native;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, Conversation> conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Text, string Destination, double Channel)> drafts = new();
    private Conversation activeConversation = Conversation.Recent;
    private MessageRow? replyTarget;
    private string? localMessageNode;
    private bool conversationListLoaded;
    private bool updatingConversations;
    private MessageRow[] historyRows = [];
    private int historyLastOffset;
    private bool hasOlder;
    private bool showingOlder;

    private async Task LoadConversationAsync(SourceRow source)
    {
        ConversationBar.Visibility = source.IsUnified ? Visibility.Collapsed : Visibility.Visible;
        var selection = activeConversation;
        if (showingOlder)
        {
            UpdateHistoryActions();
            return;
        }
        string? previousId = (MessagesList.SelectedItem as MessageRow)?.Id;
        if (!conversationListLoaded && !source.IsUnified)
        {
            await LoadConversationChoicesAsync(source);
            if (closed) return;
            conversationListLoaded = true;
        }

        var page = await api!.ReadMessagePage(source, selection, localMessageNode);
        if (closed) return;
        historyRows = page.Rows;
        historyLastOffset = historyRows.Length - 1;
        hasOlder = page.HasMore;
        var messages = MeshApi.FormatMessages(source, historyRows);
        if (!source.IsUnified && selection.IsRecent)
        {
            int maxChannel = source.IsMeshCore ? 255 : 7;
            foreach (var message in messages)
            {
                if (message.Channel is int channel && channel >= 0 && channel <= maxChannel)
                {
                    var candidate = Conversation.ForChannel(channel);
                    conversations.TryAdd(candidate.Key, candidate);
                }
                if (!source.IsMeshCore && localMessageNode is { } local &&
                    Conversation.PeerFor(message, local) is { } peer && Conversation.ValidPeer(peer, meshCore: false))
                {
                    bool incoming = string.Equals(message.FromId, peer, StringComparison.OrdinalIgnoreCase);
                    var candidate = Conversation.ForPeer(peer, incoming ? message.Sender : null);
                    conversations.TryAdd(candidate.Key, candidate);
                }
            }
        }

        JsonElement unread = default;
        if (signedIn && source.Type == "meshtastic_tcp")
        {
            try { unread = await api.UnreadCounts(source); }
            catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Forbidden) { }
        }
        if (closed) return;
        RefreshConversationPicker(unread);
        ApplyMessages(messages, previousId);
        Composer.Visibility = signedIn && source.Enabled && source.CanTransmit
            ? Visibility.Visible : Visibility.Collapsed;
        Channel.Maximum = source.Type == "meshcore" ? 255 : 7;
        Destination.Header = source.Type == "meshcore" ? "Direct recipient public key" : "Direct recipient node ID";
        ApplyConversationTarget();
        UpdateDetailActions();
        UpdateConversationActions();
    }

    private void ApplyMessages(MessageRow[] messages, string? previousId)
    {
        MessagesList.ItemsSource = messages;
        MessagesList.SelectedItem = string.IsNullOrEmpty(previousId) ? null :
            messages.FirstOrDefault(message => message.Id == previousId);
        string count = messages.Length == 0 ? "No messages." :
            messages.Length == 1 ? "1 message" : $"{messages.Length} messages";
        MessageStatus.Text = showingOlder ? $"{count} · History view" : count;
        UpdateHistoryActions();
    }

    private async void OnLoadOlder(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (!hasOlder || activeConversation.IsRecent || historyRows.LastOrDefault() is not { } anchor ||
            SourcesList.SelectedItem is not SourceRow { IsUnified: false } source) return;
        var selection = activeConversation;
        var older = await MessageHistory.ReadOlder(anchor.Id, historyLastOffset,
            offset => client.ReadMessagePage(source, selection, localMessageNode, offset));
        if (closed) return;
        showingOlder = true;
        historyLastOffset = older.LastOffset;
        hasOlder = older.HasMore;
        string? previousId = (MessagesList.SelectedItem as MessageRow)?.Id;
        var seen = historyRows.Select(row => row.Id).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        var added = older.Rows.Where(row => row.Id.Length == 0 || seen.Add(row.Id)).ToArray();
        historyRows = [.. historyRows, .. added];
        var messages = MeshApi.FormatMessages(source, historyRows);
        ApplyMessages(messages, previousId);
        if (added.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.Text)) is { } first &&
            messages.FirstOrDefault(row => row.Id == first.Id) is { } item)
            MessagesList.ScrollIntoView(item);
    });

    private async void OnLatestMessages(object sender, RoutedEventArgs args) => await RunAsync(async _ =>
    {
        if (SourcesList.SelectedItem is not SourceRow { IsUnified: false } source) return;
        ResetMessageHistory();
        await LoadConversationAsync(source);
    });

    private void UpdateHistoryActions()
    {
        if (HistoryActions is null) return;
        HistoryActions.Visibility = !activeConversation.IsRecent && (hasOlder || showingOlder)
            ? Visibility.Visible : Visibility.Collapsed;
        LoadOlder.Visibility = hasOlder ? Visibility.Visible : Visibility.Collapsed;
        LoadOlder.IsEnabled = hasOlder && !busy;
        LatestMessages.IsEnabled = !busy;
    }

    private void ResetMessageHistory()
    {
        historyRows = [];
        historyLastOffset = 0;
        hasOlder = showingOlder = false;
        UpdateHistoryActions();
    }

    private async Task LoadConversationChoicesAsync(SourceRow source)
    {
        try
        {
            var available = source.IsMeshCore ? await api!.MeshCorePeers(source) : await api!.Channels(source);
            foreach (var item in available) conversations[item.Key] = item;
        }
        catch (HttpRequestException error) when (error.StatusCode is
            System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            // The directory and a specific conversation can have different read grants.
        }
    }

    private void RefreshConversationPicker(JsonElement unread = default)
    {
        var channelCounts = unread.Property("channels");
        var directCounts = unread.Property("directMessages");
        if (directCounts.ValueKind == JsonValueKind.Object)
        {
            foreach (var count in directCounts.EnumerateObject())
            {
                if (!Conversation.ValidPeer(count.Name, false)) continue;
                var thread = Conversation.ForPeer(count.Name);
                conversations.TryAdd(thread.Key, thread);
            }
        }
        if (unread.ValueKind == JsonValueKind.Object)
            foreach (var item in conversations.Values.ToArray())
                if (item.Channel is int channel)
                    conversations[item.Key] = item with { Unread = (int)(channelCounts.Number(channel.ToString()) ?? 0) };
                else if (item.Peer is { } peer)
                    conversations[item.Key] = item with { Unread = (int)(directCounts.Number(peer) ?? 0) };

        List<Conversation> items = [Conversation.Recent, .. conversations.Values
            .OrderBy(item => item.Peer is not null).ThenBy(item => item.Channel).ThenBy(item => item.Label)];
        bool same = ConversationPicker.ItemsSource is IEnumerable<Conversation> current && current.SequenceEqual(items);
        if (same && (ConversationPicker.SelectedItem as Conversation)?.Key == activeConversation.Key) return;
        updatingConversations = true;
        try
        {
            if (!same) ConversationPicker.ItemsSource = items;
            ConversationPicker.SelectedItem = ConversationPicker.Items.OfType<Conversation>()
                .FirstOrDefault(item => item.Key == activeConversation.Key);
        }
        finally { updatingConversations = false; }
    }

    private async void OnConversationSelected(object sender, SelectionChangedEventArgs args)
    {
        if (updatingConversations || busy || ConversationPicker.SelectedItem is not Conversation next ||
            SourcesList.SelectedItem is not SourceRow { IsUnified: false } source) return;
        await RunAsync(_ => OpenConversationAsync(source, next));
    }

    private async Task OpenConversationAsync(SourceRow source, Conversation next)
    {
        if (next.Key == activeConversation.Key) return;
        drafts[activeConversation.Key] = (MessageText.Text, Destination.Text, Channel.Value);
        activeConversation = next;
        ResetMessageHistory();
        ClearReply();
        MessagesList.ItemsSource = null;
        MessageStatus.Text = "";
        var draft = drafts.GetValueOrDefault(next.Key, ("", "", 0));
        MessageText.Text = draft.Text;
        Destination.Text = draft.Destination;
        Channel.Value = draft.Channel;
        ApplyConversationTarget();
        await LoadConversationAsync(source);
    }

    private async void OnOpenConversation(object sender, RoutedEventArgs args) => await RunAsync(async _ =>
    {
        if (SourcesList.SelectedItem is not SourceRow { IsUnified: false } source) return;
        var target = new TextBox
        {
            Header = source.IsMeshCore ? "Channel number or recipient public key" : "Channel number or recipient node ID",
            PlaceholderText = source.IsMeshCore ? "0–255 or a 64-digit hex key" : "0–7 or a node ID such as !0012abcd"
        };
        var error = DialogError();
        var dialog = Form("Open conversation", "Open", target, error);
        Conversation? next = null;
        dialog.PrimaryButtonClick += (_, action) =>
        {
            string value = target.Text.Trim();
            int maximum = source.IsMeshCore ? 255 : 7;
            if (int.TryParse(value, out int channel) && channel >= 0 && channel <= maximum)
                next = Conversation.ForChannel(channel);
            else if (Conversation.ValidPeer(value, source.IsMeshCore))
            {
                if (!source.IsMeshCore && (localMessageNode is null || !Conversation.ValidPeer(localMessageNode, meshCore: false)))
                {
                    action.Cancel = true;
                    error.Text = "This source has not reported its local node ID yet. Connect the radio first.";
                    return;
                }
                next = Conversation.ForPeer(value);
            }
            else
            {
                action.Cancel = true;
                error.Text = "Enter a valid channel number or direct recipient.";
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed || next is null) return;
        if (!conversations.TryGetValue(next.Key, out var known)) conversations[next.Key] = next;
        await OpenConversationAsync(source, known ?? next);
    });

    private void ApplyConversationTarget()
    {
        if (activeConversation.Channel is int channel)
        {
            Channel.Value = channel;
            Destination.Text = "";
        }
        else if (activeConversation.Peer is { } peer)
        {
            Channel.Value = 0;
            Destination.Text = peer;
        }
        Channel.IsEnabled = activeConversation.IsRecent;
        Destination.IsReadOnly = !activeConversation.IsRecent;
    }

    private void OnReplyMessage(object sender, RoutedEventArgs args)
    {
        if (!CanReplyToSelection() || MessagesList.SelectedItem is not MessageRow selected) return;
        replyTarget = selected;
        ReplyQuote.Text = $"Replying to {selected.Sender} · {Conversation.Quote(selected.Text)}";
        ReplyContext.Visibility = Visibility.Visible;
        MessageText.Focus(FocusState.Programmatic);
    }

    private bool CanReact(SourceRow source)
    {
        string resource = activeConversation.Channel is int channel ? $"channel_{channel}" : "messages";
        return CanReplyToSelection() && (radioAdmin || radioPermissions.Property(source.Id).Property(resource).Flag("write") == true);
    }

    private async void OnReact(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (SourcesList.SelectedItem is not SourceRow source || !CanReact(source) ||
            MessagesList.SelectedItem is not MessageRow { PacketId: uint packet } selected) return;
        var thread = activeConversation;
        var emoji = new ComboBox { Header = "Reaction", ItemsSource = new[] { "👍", "❤️", "😂", "🎉", "👎", "❓" }, SelectedIndex = 0 };
        var dialog = Form("React to message", "Send reaction", Wrapped($"{selected.Sender} · {Conversation.Quote(selected.Text)}"), emoji);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
        await client.SendReaction(source, thread, packet, (string)emoji.SelectedItem);
        if (closed) return;
        ShowNotice("The backend accepted the reaction.", InfoBarSeverity.Success);
        if (!showingOlder) await LoadConversationAsync(source);
    });

    private void OnCancelReply(object sender, RoutedEventArgs args) => ClearReply();

    private void ClearReply()
    {
        replyTarget = null;
        ReplyContext.Visibility = Visibility.Collapsed;
        ReplyQuote.Text = "";
    }

    private bool CanReplyToSelection() => signedIn && !activeConversation.IsRecent &&
        SourcesList.SelectedItem is SourceRow { Enabled: true, Type: "meshtastic_tcp" } &&
        MessagesList.SelectedItem is MessageRow { PacketId: > 0 } selected &&
        (activeConversation.Channel is int channel ? selected.Channel == channel :
            localMessageNode is { } local && string.Equals(activeConversation.Peer,
                Conversation.PeerFor(selected, local), StringComparison.OrdinalIgnoreCase));

    private async void OnMarkRead(object sender, RoutedEventArgs args) => await RunAsync(async client =>
    {
        if (!signedIn || SourcesList.SelectedItem is not SourceRow { Type: "meshtastic_tcp" } source) return;
        if (activeConversation.Channel is int channel) await client.MarkChannelRead(source, channel);
        else if (activeConversation.Peer is not null && localMessageNode is { } local)
            await client.MarkDirectRead(source, activeConversation, local);
        else return;
        if (closed) return;
        if (showingOlder)
        {
            var unread = await client.UnreadCounts(source);
            if (!closed) RefreshConversationPicker(unread);
        }
        else await LoadConversationAsync(source);
    });

    private void UpdateConversationActions()
    {
        if (ReplyMessage is null || MarkRead is null) return;
        UpdateHistoryActions();
        bool allowReply = CanReplyToSelection();
        ReplyMessage.Visibility = allowReply ? Visibility.Visible : Visibility.Collapsed;
        ReplyMessage.IsEnabled = allowReply && !busy;
        bool allowReaction = SourcesList.SelectedItem is SourceRow source && CanReact(source);
        ReactMessage.Visibility = allowReaction ? Visibility.Visible : Visibility.Collapsed;
        ReactMessage.IsEnabled = allowReaction && !busy;
        bool allowRead = signedIn && (activeConversation.Channel is not null || (activeConversation.Peer is not null && localMessageNode is not null)) &&
            SourcesList.SelectedItem is SourceRow { Type: "meshtastic_tcp" };
        MarkRead.Visibility = allowRead ? Visibility.Visible : Visibility.Collapsed;
        MarkRead.IsEnabled = allowRead && !busy;
    }

    private void ResetConversations()
    {
        conversationListLoaded = false;
        activeConversation = Conversation.Recent;
        ResetMessageHistory();
        localMessageNode = null;
        conversations.Clear();
        drafts.Clear();
        RefreshConversationPicker();
        ClearReply();
    }
}
