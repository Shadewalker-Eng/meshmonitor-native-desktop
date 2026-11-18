namespace MeshMonitor.Native;

internal static class MessageAcknowledgment
{
    internal static bool IsMeshtasticDirect(MessageRow message) =>
        message.Channel == -1 &&
        message.FromId is { } from && Conversation.ValidPeer(from, meshCore: false) &&
        message.ToId is { } to && Conversation.ValidPeer(to, meshCore: false) &&
        !string.Equals(from, to, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(to, "!ffffffff", StringComparison.OrdinalIgnoreCase);

    internal static string Summary(MessageRow message)
    {
        if (message.RoutingFailed || message.DeliveryState == "failed") return "Routing failed";
        if (IsMeshtasticDirect(message))
        {
            return message.DeliveryState switch
            {
                "confirmed" when SenderMatchesRecipient(message) => "Recipient acknowledgment reported",
                "confirmed" => "Recipient acknowledgment could not be verified",
                "delivered" when message.WantsAcknowledgment => "Transmit acknowledged · Waiting for recipient",
                "delivered" => "Sent to radio",
                "pending" when message.WantsAcknowledgment => "Waiting for recipient acknowledgment",
                _ => ""
            };
        }

        return message.ProtocolDeliveryStatus switch
        {
            "failed" => "Delivery failed",
            "delivered" => "Recipient acknowledgment received",
            "sent" => "Sent to radio",
            "sending" => "Sending",
            _ => message.DeliveryState switch
            {
                "confirmed" => "Delivery reported",
                "delivered" => "Transmitted to mesh",
                _ => ""
            }
        };
    }

    internal static string TimelineSummary(MessageRow message, DeliveryEvent[] events)
    {
        if (!IsMeshtasticDirect(message)) return message.Delivery;
        if (events.Any(entry => IsRecipientReport(message, entry))) return RecipientSummary(message);
        if (message.RoutingFailed || message.DeliveryState is "confirmed" or "failed") return message.Delivery;

        return LastProgress(events) switch
        {
            "delivered" => message.WantsAcknowledgment
                ? "Transmit acknowledged · Waiting for recipient" : "Transmit acknowledged",
            "timeout" => "Recipient acknowledgment timed out",
            "routing_error" => "Routing failed",
            "retry" or "sent_to_radio" when message.WantsAcknowledgment => "Waiting for recipient acknowledgment",
            _ => message.Delivery
        };
    }

    internal static string TimelineLabel(MessageRow message, DeliveryEvent entry)
    {
        if (!IsMeshtasticDirect(message)) return entry.Description;
        return entry.Type switch
        {
            "delivered" => "Local transmit acknowledgment",
            "confirmed" => IsRecipientReport(message, entry) ? RecipientSummary(message)
                : "Unverified acknowledgment",
            "timeout" => "Recipient acknowledgment timed out",
            _ => entry.Description
        };
    }

    internal static bool AwaitingRecipient(MessageRow message, DeliveryEvent[] events)
    {
        if (!IsMeshtasticDirect(message) || !message.WantsAcknowledgment ||
            message.RoutingFailed || message.DeliveryState is not ("pending" or "delivered"))
            return false;
        if (events.Any(entry => IsRecipientReport(message, entry))) return false;
        return LastProgress(events) is not ("timeout" or "routing_error");
    }

    private static bool SenderMatchesRecipient(MessageRow message) =>
        message.AcknowledgmentSender is not uint sender ||
        string.Equals($"!{sender:x8}", message.ToId, StringComparison.OrdinalIgnoreCase);

    private static bool IsRecipientReport(MessageRow message, DeliveryEvent entry) =>
        entry.Type == "confirmed" && entry.Provenance == "reported" && SenderMatchesRecipient(message);

    private static string? LastProgress(DeliveryEvent[] events) =>
        events.LastOrDefault(entry => entry.Type is
            "delivered" or "timeout" or "routing_error" or "retry" or "sent_to_radio")?.Type;

    private static string RecipientSummary(MessageRow message) => $"Recipient {message.ToId} acknowledged";
}
