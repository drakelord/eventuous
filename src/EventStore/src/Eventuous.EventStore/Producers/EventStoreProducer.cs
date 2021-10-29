﻿using System.Diagnostics;
using Eventuous.Diagnostics;
using Eventuous.Producers;

namespace Eventuous.EventStore.Producers;

/// <summary>
/// Producer for EventStoreDB
/// </summary>
[PublicAPI]
public class EventStoreProducer : BaseProducer<EventStoreProduceOptions> {
    readonly EventStoreClient    _client;
    readonly IEventSerializer    _serializer;
    readonly IMetadataSerializer _metaSerializer;

    /// <summary>
    /// Create a new EventStoreDB producer instance
    /// </summary>
    /// <param name="eventStoreClient">EventStoreDB gRPC client</param>
    /// <param name="serializer">Optional: event serializer instance</param>
    /// <param name="metaSerializer"></param>
    public EventStoreProducer(
        EventStoreClient     eventStoreClient,
        IEventSerializer?    serializer     = null,
        IMetadataSerializer? metaSerializer = null
    ) {
        _client         = Ensure.NotNull(eventStoreClient, nameof(eventStoreClient));
        _serializer     = serializer ?? DefaultEventSerializer.Instance;
        _metaSerializer = metaSerializer ?? DefaultMetadataSerializer.Instance;

        ReadyNow();
    }

    /// <summary>
    /// Create a new EventStoreDB producer instance
    /// </summary>
    /// <param name="clientSettings">EventStoreDB gRPC client settings</param>
    /// <param name="serializer">Optional: event serializer instance</param>
    /// <param name="metaSerializer"></param>
    public EventStoreProducer(
        EventStoreClientSettings clientSettings,
        IEventSerializer?        serializer     = null,
        IMetadataSerializer?     metaSerializer = null
    )
        : this(
            new EventStoreClient(Ensure.NotNull(clientSettings, nameof(clientSettings))),
            serializer,
            metaSerializer
        ) { }

    static readonly KeyValuePair<string, object?>[] DefaultTags = {
        new(TelemetryTags.Messaging.System, "eventstoredb"),
        new(TelemetryTags.Messaging.DestinationKind, "stream"),
    };

    protected override async Task ProduceMessages(
        string                       stream,
        IEnumerable<ProducedMessage> messages,
        EventStoreProduceOptions?    produceOptions,
        CancellationToken            cancellationToken = default
    ) {
        var options = produceOptions ?? EventStoreProduceOptions.Default;

        foreach (var chunk in Ensure.NotNull(messages, nameof(messages)).Chunks(options.MaxAppendEventsCount)) {
            await Trace(
                ProduceChunk,
                chunk,
                DefaultTags,
                activity => activity
                    .SetTag(TelemetryTags.Messaging.Destination, stream)
                    .SetTag(TelemetryTags.Messaging.Operation, "append")
            ).NoContext();
        }

        Task ProduceChunk(IEnumerable<ProducedMessage> chunk)
            => _client.AppendToStreamAsync(
                stream,
                options.ExpectedState,
                chunk.Select(CreateMessage),
                options.ConfigureOperation,
                options.Credentials,
                cancellationToken
            );
    }

    EventData CreateMessage(ProducedMessage producedMessage) {
        var (message, metadata) = producedMessage;
        var msg = Ensure.NotNull(message, nameof(message));
        var (eventType, payload) = _serializer.SerializeEvent(msg);
        metadata!.Remove(MetaTags.MessageId);
        var metaBytes = _metaSerializer.Serialize(metadata);

        return new EventData(
            Uuid.FromGuid(metadata.GetMessageId()),
            eventType,
            payload,
            metaBytes,
            _serializer.ContentType
        );
    }
}