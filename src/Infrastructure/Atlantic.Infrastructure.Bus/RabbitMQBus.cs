﻿using Atlantic.Domain.Core.Bus;
using Atlantic.Domain.Core.Commands;
using Atlantic.Domain.Core.Events;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
namespace Atlantic.Infrastructure.Bus {
    public sealed class RabbitMQBus : IEventBus {
        private readonly IMediator _mediator;
        private readonly Dictionary<string, List<Type>> _handlers;
        private readonly List<Type> _eventTypes;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        public RabbitMQBus(IMediator mediator, IServiceScopeFactory serviceScopeFactory) {
            _mediator = mediator;
            _handlers = new Dictionary<string, List<Type>>();
            _eventTypes = new List<Type>();
            _serviceScopeFactory = serviceScopeFactory;
        }
        public Task SendCommand<T>(T command) where T : Command {
            return _mediator.Send(command);
        }
        public void Publish<T>(T @event) where T : Event {
            ConnectionFactory connectionFactory = new ConnectionFactory() { HostName = "localhost" };
            using (IConnection connection = connectionFactory.CreateConnection()) {
                using (IModel channel = connection.CreateModel()) {
                    string eventName = @event.GetType().Name;
                    channel.QueueDeclare(eventName, false, false, false, null);
                    var message = JsonSerializer.Serialize(@event);
                    var body = Encoding.UTF8.GetBytes(message);
                    channel.BasicPublish("", eventName, null, body);
                }
            }
        }
        public void Subscribe<T, TH>()
            where T : Event
            where TH : IEventHandler<T> {
            var eventName = typeof(T).Name;
            var handlerType = typeof(TH);
            if (!_eventTypes.Contains(typeof(T))) {
                _eventTypes.Add(typeof(T));
            }
            if (!_handlers.ContainsKey(eventName)) {
                _handlers.Add(eventName, new List<Type>());
            }
            if (_handlers[eventName].Any(s => s.GetType() == handlerType)) {
                throw new ArgumentException($"Handler Type {handlerType.Name} already registered for '{eventName}'", nameof(handlerType));
            }
            _handlers[eventName].Add(handlerType);
            StartBasicConsume<T>();
        }
        private void StartBasicConsume<T>() where T : Event {
            ConnectionFactory connectionFactory = new ConnectionFactory() { HostName = "localhost", DispatchConsumersAsync = true };
            IConnection connection = connectionFactory.CreateConnection();
            IModel channel = connection.CreateModel();
            string eventName = typeof(T).Name;
            channel.QueueDeclare(eventName, false, false, false, null);
            AsyncEventingBasicConsumer consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += Consumer_Received;
            channel.BasicConsume(eventName, true, consumer);
        }
        private async Task Consumer_Received(object sender, BasicDeliverEventArgs e) {
            string eventName = e.RoutingKey;
            string message = Encoding.UTF8.GetString(e.Body.ToArray());
            try {
                await ProcessEvent(eventName, message).ConfigureAwait(false);
            }
            catch (Exception) {
                throw;
            }
        }
        private async Task ProcessEvent(string eventName, string message) {
            if (_handlers.ContainsKey(eventName)) {
                using (var scope = _serviceScopeFactory.CreateScope()) {
                    var subscriptions = _handlers[eventName];
                    foreach (var subscription in subscriptions) {
                        var handler = scope.ServiceProvider.GetService(subscription);
                        if (handler == null) continue;
                        var eventType = _eventTypes.SingleOrDefault(t => t.Name == eventName);
                        var @event = JsonSerializer.Deserialize(message, eventType);
                        var concretetype = typeof(IEventHandler<>).MakeGenericType(eventType);
                        await (Task)concretetype.GetMethod("Handle").Invoke(handler, new object[] { @event });
                    }
                }
            }
        }
    }
}