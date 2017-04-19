﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using Metrics;
using Newtonsoft.Json;
using NServiceBus;
using NServiceBus.Extensibility;
using NServiceBus.Logging;
using NServiceBus.MessageInterfaces;
using NServiceBus.Pipeline;
using NServiceBus.Transport;
using NServiceBus.Unicast;
using NServiceBus.Unicast.Messages;
using MessageContext = NServiceBus.Transport.MessageContext;

namespace Aggregates.Internal
{
    class EventMessage : IMessage { }

    class EventSubscriber : IEventSubscriber
    {

        private static readonly ILog Logger = LogManager.GetLogger("EventSubscriber");
        private static readonly Metrics.Timer EventExecution = Metric.Timer("Event Execution", Unit.Items, tags: "debug");
        private static readonly Counter EventsQueued = Metric.Counter("Events Queued", Unit.Items, tags: "debug");
        private static readonly Counter EventsHandled = Metric.Counter("Events Handled", Unit.Items, tags: "debug");
        private static readonly Meter EventErrors = Metric.Meter("Event Failures", Unit.Items);

        private static readonly BlockingCollection<Tuple<string, long, IFullEvent>> WaitingEvents = new BlockingCollection<Tuple<string, long, IFullEvent>>();

        private class ThreadParam
        {
            public IEventStoreConsumer Consumer { get; set; }
            public int Concurrency { get; set; }
            public CancellationToken Token { get; set; }
            public IMessaging Messaging { get; set; }
        }


        private Thread _pinnedThread;
        private CancellationTokenSource _cancelation;
        private string _endpoint;
        private Version _version;

        private readonly IMessaging _messaging;
        private readonly int _concurrency;

        private readonly IEventStoreConsumer _consumer;

        private bool _disposed;


        public EventSubscriber(IMessaging messaging, IEventStoreConsumer consumer, int concurrency)
        {
            _messaging = messaging;
            _consumer = consumer;
            _concurrency = concurrency;
        }

        public async Task Setup(string endpoint, CancellationToken cancelToken, Version version)
        {
            _endpoint = endpoint;
            _version = version;
            await _consumer.EnableProjection("$by_category").ConfigureAwait(false);
            _cancelation = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);

            var discoveredEvents =
                _messaging.GetMessageTypes().Where(x => typeof(IEvent).IsAssignableFrom(x)).OrderBy(x => x.FullName).ToList();

            if (!discoveredEvents.Any())
            {
                Logger.Warn($"Event consuming is enabled but we did not detect and IEvent handlers");
                return;
            }

            // Dont use - we dont need category projection projecting our projection
            var stream = $"{_endpoint}.{version}".Replace("-", "");

            // Link all events we are subscribing to to a stream
            var functions =
                discoveredEvents
                    .Select(
                        eventType => $"'{eventType.AssemblyQualifiedName}': processEvent")
                    .Aggregate((cur, next) => $"{cur},\n{next}");

            // Don't tab this '@' will create tabs in projection definition
            var definition = @"
function processEvent(s,e) {{
    linkTo('{1}.{0}', e);
}}
fromCategory('{0}').
when({{
{2}
}});";

            // Todo: replace with `fromCategories([])` when available
            var appDefinition = string.Format(definition, StreamTypes.Domain, stream, functions);
            var oobDefinition = string.Format(definition, StreamTypes.OOB, stream, functions);
            var pocoDefinition = string.Format(definition, StreamTypes.Poco, stream, functions);

            await _consumer.CreateProjection($"{stream}.app.projection", appDefinition).ConfigureAwait(false);
            await _consumer.CreateProjection($"{stream}.oob.projection", oobDefinition).ConfigureAwait(false);
            await _consumer.CreateProjection($"{stream}.poco.projection", pocoDefinition).ConfigureAwait(false);
        }

        public async Task Connect()
        {
            var group = $"{_endpoint}.{_version}";
            var appStream = $"{_endpoint}.{_version}.{StreamTypes.Domain}";
            var oobStream = $"{_endpoint}.{_version}.{StreamTypes.OOB}";
            var pocoStream = $"{_endpoint}.{_version}.{StreamTypes.Poco}";


            await _consumer.ConnectPinnedPersistentSubscription(appStream, group, _cancelation.Token, onEvent, Connect).ConfigureAwait(false);
            await _consumer.ConnectPinnedPersistentSubscription(oobStream, group, _cancelation.Token, onEvent, Connect).ConfigureAwait(false);
            await _consumer.ConnectPinnedPersistentSubscription(pocoStream, group, _cancelation.Token, onEvent, Connect).ConfigureAwait(false);

            _pinnedThread = new Thread(Threaded)
            { IsBackground = true, Name = $"Main Event Thread" };
            _pinnedThread.Start(new ThreadParam { Token = _cancelation.Token, Messaging = _messaging, Concurrency = _concurrency, Consumer = _consumer });

        }
        private void onEvent(string stream, long position, IFullEvent e)
        {
            EventsQueued.Increment();
            WaitingEvents.Add(new Tuple<string, long, IFullEvent>(stream, position, e));
        }


        private static void Threaded(object state)
        {
            var param = (ThreadParam)state;

            while (Bus.OnMessage == null || Bus.OnError == null)
            {
                Logger.Warn($"Could not find NSBs onMessage handler yet - if this persists there is a problem.");
                Thread.Sleep(500);
            }

            var semaphore = new SemaphoreSlim(param.Concurrency);

            try
            {
                while (true)
                {
                    param.Token.ThrowIfCancellationRequested();

                    if (semaphore.CurrentCount == 0)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    var @event = WaitingEvents.Take(param.Token);
                    EventsQueued.Decrement();
                    semaphore.Wait();

                    try
                    {
                        Task.Run(async () =>
                        {

                            await
                                ProcessEvent(param.Messaging, @event.Item1, @event.Item2, @event.Item3, param.Token)
                                    .ConfigureAwait(false);

                            Logger.Write(LogLevel.Debug,
                                () =>
                                        $"Acknowledge event {@event.Item3.Descriptor.EventId} stream [{@event.Item1}] number {@event.Item2}");
                            await param.Consumer.Acknowledge(@event.Item3).ConfigureAwait(false);

                            semaphore.Release();
                        }, param.Token).Wait();
                    }
                    catch (System.AggregateException e)
                    {
                        if (e.InnerException is OperationCanceledException)
                            throw e.InnerException;

                        // If not a canceled exception, just write to log and continue
                        // we dont want some random unknown exception to kill the whole event loop
                        Logger.Error(
                            $"Received exception in main event thread: {e.InnerException.GetType()}: {e.InnerException.Message}", e);
                    }

                }
            }
            catch (OperationCanceledException)
            {
            }


        }

        // A fake message that will travel through the pipeline in order to process events from the context bag
        private static readonly byte[] Marker = new EventMessage().Serialize(new JsonSerializerSettings()).AsByteArray();

        private static async Task ProcessEvent(IMessaging messaging, string stream, long position, IFullEvent @event, CancellationToken token)
        {
            Logger.Write(LogLevel.Debug, () => $"Processing event from stream [{@event.Descriptor.StreamId}] bucket [{@event.Descriptor.Bucket}] entity [{@event.Descriptor.EntityType}] event id {@event.EventId}");


            var contextBag = new ContextBag();
            // Hack to get all the events to invoker without NSB deserializing 
            contextBag.Set(Defaults.EventHeader, @event.Event);


            using (var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var processed = false;
                var numberOfDeliveryAttempts = 0;

                while (!processed)
                {
                    var transportTransaction = new TransportTransaction();

                    var messageId = Guid.NewGuid().ToString();
                    var headers = new Dictionary<string, string>(@event.Descriptor.Headers ?? new Dictionary<string, string>())
                    {
                        [Headers.MessageIntent] = MessageIntentEnum.Send.ToString(),
                        [Headers.EnclosedMessageTypes] = SerializeEnclosedMessageTypes(messaging, @event.Event.GetType()),
                        [Headers.MessageId] = messageId,
                        [Defaults.EventHeader] = "",
                        ["EventId"] = @event.EventId.ToString(),
                        ["EventStream"] = stream,
                        ["EventPosition"] = position.ToString()
                    };

                    using (var ctx = EventExecution.NewContext())
                    {
                        try
                        {
                            // If canceled, this will throw the number of time immediate retry requires to send the message to the error queue
                            token.ThrowIfCancellationRequested();

                            // Don't re-use the event id for the message id
                            var messageContext = new MessageContext(messageId,
                                headers,
                                Marker, transportTransaction, tokenSource,
                                contextBag);
                            await Bus.OnMessage(messageContext).ConfigureAwait(false);
                            EventsHandled.Increment();
                            processed = true;
                        }
                        catch (ObjectDisposedException)
                        {
                            // NSB transport has been disconnected
                            break;
                        }
                        catch (Exception ex)
                        {

                            EventErrors.Mark($"{ex.GetType().Name} {ex.Message}");
                            ++numberOfDeliveryAttempts;

                            // Don't retry a cancelation
                            if (tokenSource.IsCancellationRequested)
                                numberOfDeliveryAttempts = Int32.MaxValue;

                            var errorContext = new ErrorContext(ex, headers,
                                messageId,
                                Marker, transportTransaction,
                                numberOfDeliveryAttempts);
                            if (await Bus.OnError(errorContext).ConfigureAwait(false) ==
                                ErrorHandleResult.Handled)
                                break;
                            await Task.Delay((numberOfDeliveryAttempts / 5) * 200, token).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cancelation?.Cancel();
            _pinnedThread?.Join();
        }

        static string SerializeEnclosedMessageTypes(IMessaging messaging, Type messageType)
        {
            var assemblyQualifiedNames = new HashSet<string>();
            foreach (var type in messaging.GetMessageHierarchy(messageType))
            {
                assemblyQualifiedNames.Add(type.AssemblyQualifiedName);
            }

            return string.Join(";", assemblyQualifiedNames);
        }
    }
}
