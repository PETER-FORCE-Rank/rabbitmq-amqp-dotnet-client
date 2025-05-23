// This source code is dual-licensed under the Apache License, version 2.0,
// and the Mozilla Public License, version 2.0.
// Copyright (c) 2017-2024 Broadcom. All Rights Reserved. The term "Broadcom" refers to Broadcom Inc. and/or its subsidiaries.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RabbitMQ.AMQP.Client;
using RabbitMQ.AMQP.Client.Impl;
using Xunit;
using Xunit.Abstractions;

namespace Tests.Recovery;

public class CustomPublisherConsumerRecoveryTests(ITestOutputHelper testOutputHelper)
    : IntegrationTest(testOutputHelper, setupConnectionAndManagement: false)
{
    /// <summary>
    /// The consumer and the publisher should not restart if the recovery is disabled
    /// </summary>
    [Fact]
    public async Task PublisherAndConsumerShouldNotRestartIfRecoveryIsDisabled()
    {
        Assert.Null(_connection);
        Assert.Null(_management);

        var recoveryConfiguration = new RecoveryConfiguration();
        recoveryConfiguration.Activated(false);

        ConnectionSettings connectionSettings =
            ConnectionSettingsBuilder.Create().RecoveryConfiguration(recoveryConfiguration)
            .ContainerId(_containerId).Build();

        _connection = await AmqpConnection.CreateAsync(connectionSettings);
        _management = _connection.Management();

        IQueueSpecification queueSpec = _management.Queue().Name(_queueName);
        await queueSpec.DeclareAsync();

        IPublisher publisher = await _connection.PublisherBuilder().Queue(queueSpec).BuildAsync();

        List<(State, State)> statesProducer = [];
        publisher.ChangeState += (sender, fromState, toState, e) =>
        {
            statesProducer.Add((fromState, toState));
        };

        IConsumer consumer = await _connection.ConsumerBuilder()
            .InitialCredits(100)
            .Queue(queueSpec)
            .MessageHandler((context, message) =>
            {
                try
                {
                    context.Accept();
                }
                catch (Exception)
                {
                    // ignored
                }
                return Task.CompletedTask;
            }).BuildAndStartAsync();

        List<(State, State)> statesConsumer = [];
        consumer.ChangeState += (sender, fromState, toState, e) =>
        {
            statesConsumer.Add((fromState, toState));
        };

        Assert.Equal(State.Open, publisher.State);
        Assert.Equal(State.Open, consumer.State);

        await WaitUntilConnectionIsKilled(_containerId);

        await WaitUntilFuncAsync(() => publisher.State == State.Closed);
        await WaitUntilFuncAsync(() => consumer.State == State.Closed);

        Assert.Equal(State.Closed, _connection.State);
        Assert.Equal(State.Closed, _management.State);

        Assert.DoesNotContain((State.Open, State.Closing), statesProducer);
        Assert.DoesNotContain((State.Closing, State.Closed), statesProducer);
        Assert.Contains((State.Open, State.Closed), statesProducer);

        Assert.DoesNotContain((State.Open, State.Closing), statesConsumer);
        Assert.DoesNotContain((State.Closing, State.Closed), statesConsumer);
        Assert.Contains((State.Open, State.Closed), statesConsumer);

        // Here we need a second _connection since the RecoveryConfiguration is disabled
        // and the _connection is closed. So we can't use the same _connection to delete the queue

        var recoveryConfiguration1 = new RecoveryConfiguration();
        recoveryConfiguration1.Activated(false);

        IConnection connection2 = await AmqpConnection.CreateAsync(
            ConnectionSettingsBuilder.Create()
                .RecoveryConfiguration(recoveryConfiguration1)
                .ContainerId(_containerId).Build());

        IQueueSpecification queueSpec2 = connection2.Management().Queue(_queueName);
        await queueSpec2.DeleteAsync();

        await publisher.CloseAsync();
        publisher.Dispose();
        await consumer.CloseAsync();
        consumer.Dispose();

        await connection2.CloseAsync();
        connection2.Dispose();
    }
}
