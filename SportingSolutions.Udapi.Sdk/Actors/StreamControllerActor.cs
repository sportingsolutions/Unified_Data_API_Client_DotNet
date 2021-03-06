﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using log4net;
using RabbitMQ.Client;
using SportingSolutions.Udapi.Sdk.Interfaces;
using SportingSolutions.Udapi.Sdk.Model.Message;

namespace SportingSolutions.Udapi.Sdk.Actors
{
    /// <summary>
    /// 
    ///     The StreamController is responsible for managing the connection
    ///     to the RabbitMQ streaming server.
    /// 
    ///     There is only ONE streaming connection, independently of how
    ///     many resources/consumers are added.
    /// 
    ///     Each consumer has its own queue, but the connection is shared
    ///     among all the consumers. If the connection goes down, all
    ///     the consumers get disconnected. 
    /// 
    ///     There is no automatic re-connection. A connection is (re)-established
    ///     when the first consumer is added.
    /// 
    ///     Once a connection is established the StreamSubscriber object
    ///     is set to read from the connection for any up coming messages.
    /// 
    ///     The StreamSubscriber then passed this object to the IDispatch
    ///     object whose task it to dispatch the messages to the correct
    ///     consumer.
    /// 
    /// </summary>
    internal class StreamControllerActor : ReceiveActor, IWithUnboundedStash
    {


        internal enum ConnectionState
        {
            DISCONNECTED = 0,
            CONNECTING = 1,
            CONNECTED = 2
        }


        public const string ActorName = "StreamControllerActor";
        private int _processNewConsumerErrorCounter = 0;
        public const int NewConsumerErrorLimit = 10;
        public const int NewConsumerErrorLimitForConsumer = 3;
        private static ICancelable _validateCancellation;
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamControllerActor));
        protected IConnection _streamConnection;
        private IModel _model = null;
        private bool _isModelDisposed = true;
        private volatile ConnectionState _state;
        private readonly ICancelable _connectionCancellation = new Cancelable(Context.System.Scheduler);
        private Dictionary<string, int> _newConsumerErrorsCount = new Dictionary<string, int>();


        public StreamControllerActor(IActorRef dispatcherActor)
        {
            Dispatcher = dispatcherActor ?? throw new ArgumentNullException("dispatcher");
            _processNewConsumerErrorCounter = 0;
            //Start in Disconnected state
            DisconnectedState();

            AutoReconnect = UDAPI.Configuration.AutoReconnect;
            CancelValidationMessages();
            _validateCancellation = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(10000, 10000, Self, new ValidateStateMessage(), Self);

            _logger.DebugFormat("StreamController initialised, AutoReconnect={0}", AutoReconnect);
        }

        private static void CancelValidationMessages()
        {

            if (_validateCancellation == null)
                return;
            _logger.Debug("CancelValidationMessages triggered");
            _validateCancellation.Cancel();
            _validateCancellation = null;
        }

        protected override void PreRestart(Exception reason, object message)
        {
            _logger.Error(
                $"Actor restart reason exception={reason?.ToString() ?? "null"}." +
                (message != null
                    ? $" last processing messageType={message.GetType().Name}"
                    : ""));

            CancelValidationMessages();

            base.PreRestart(reason, new DisconnectedMessage { IDConnection = _streamConnection?.GetHashCode() });
        }

        /// <summary>
        /// This is the state when the connection is closed
        /// </summary>
        private void DisconnectedState()
        {
            _logger.Info("Moved to DisconnectedState");

            Receive<ValidateConnectionMessage>(x => ValidateConnection(x));
            Receive<ConnectedMessage>(x => Become(ConnectedState));

            Receive<NewConsumerMessage>(x =>
            {
                Stash.Stash();
                GetQueueDetailsAndEstablisConnection(x.Consumer);

            });
            Receive<RemoveConsumerMessage>(x => RemoveConsumer(x.Consumer));
            Receive<DisposeMessage>(x => Dispose());
            Receive<ValidateStateMessage>(x => ValidateState(x));
            Receive<DisconnectedMessage>(x => DisconnecteOnDisconnectedHandler(x));
            Receive<CreateModelMessage>(x => CreateModel());
            State = ConnectionState.DISCONNECTED;
        }




        /// <summary>
        /// this is the state when the connection is being automatically recovered by RabbitMQ
        /// </summary>
        private void ValidationState()
        {
            _logger.Info("Moved to ValidationState");

            Receive<DisconnectedMessage>(x => DisconnectedHandler(x));
            Receive<ValidateConnectionMessage>(x => ValidateConnection(x));
            Receive<ValidationSucceededMessage>(x => Become(ConnectedState));
            Receive<NewConsumerMessage>(x => Stash.Stash());
            Receive<RemoveConsumerMessage>(x => Stash.Stash());
            Receive<DisposeMessage>(x => Dispose());
            Receive<ValidateStateMessage>(x => ValidateState(x));
            Receive<CreateModelMessage>(x => CreateModel());

            State = ConnectionState.DISCONNECTED;
        }



        /// <summary>
        /// this is the state when the connection is open
        /// </summary>
        private void ConnectedState()
        {
            _logger.Info("Moved to ConnectedState");

            Receive<ValidationStartMessage>(x => ValidationStart(x));
            Receive<NewConsumerMessage>(x =>
            {
                if (ValidateNewConsumerCanBeProcessed(x.Consumer))
                {
                    NewConsumerHandler(x);
                }
                else
                {
                    Stash.Stash();
                }
            });
            Receive<DisconnectedMessage>(x => DisconnectedHandler(x));
            Receive<RemoveConsumerMessage>(x => RemoveConsumer(x.Consumer));
            Receive<DisposeMessage>(x => Dispose());
            Receive<ValidateConnectionMessage>(x => ValidateConnection(x));
            Receive<ValidateStateMessage>(x => ValidateState(x));

            State = ConnectionState.CONNECTED;
            Stash.UnstashAll();

        }

        private void ValidationStart(ValidationStartMessage validationStartMessage)
        {
            _logger.Debug("ValidationStart triggered");
            Become(ValidationState);

            //schedule check in the near future (10s by default) whether the connection has recovered
            //DO NOT use Context in here as this code is likely going to be called as a result of event being raised on a separate thread 
            //Calling Context.Scheduler will result in exception as it's not run within Actor context - this code communicates with the actor via ActorSystem instead
            SdkActorSystem.ActorSystem.Scheduler.ScheduleTellOnce(
                TimeSpan.FromSeconds(UDAPI.Configuration.DisconnectionDelay)
                , SdkActorSystem.ActorSystem.ActorSelection(SdkActorSystem.StreamControllerActorPath)
                , new ValidateConnectionMessage()
                , ActorRefs.NoSender);
        }



        private DisconnectedMessage DefaultDisconnectedMessage => new DisconnectedMessage { IDConnection = _streamConnection?.GetHashCode() };




        private void NewConsumerHandler(NewConsumerMessage newConsumerMessage)
        {
            if (ProcessNewConsumer(newConsumerMessage.Consumer))
            {
                HandleNewConsumerMessageProcessed(newConsumerMessage);
            }
            else
            {
                HandleNewConsumerMessageUnprocessed(newConsumerMessage);
            }
        }

        private void HandleNewConsumerMessageProcessed(NewConsumerMessage newConsumerMessage)
        {
            var fixtureId = newConsumerMessage?.Consumer?.Id;

            if (fixtureId == null)
            {
                _logger.Warn("HandleNewConsumerMessageProcessed failed as fixtureId=NULL");
                return;
            }

            if (_newConsumerErrorsCount.ContainsKey(fixtureId))
            {
                _newConsumerErrorsCount.Remove(fixtureId);
            }
        }

        private void HandleNewConsumerMessageUnprocessed(NewConsumerMessage newConsumerMessage)
        {
            var fixtureId = newConsumerMessage?.Consumer?.Id;

            if (fixtureId == null)
            {
                _logger.Warn("HandleNewConsumerMessageUnprocessed failed as fixtureId=NULL");
                return;
            }

            if (_newConsumerErrorsCount.ContainsKey(fixtureId))
            {
                _newConsumerErrorsCount[fixtureId] = _newConsumerErrorsCount[fixtureId] + 1;
            }
            else
            {
                _newConsumerErrorsCount[fixtureId] = 1;
            }


            if (_newConsumerErrorsCount[fixtureId] > NewConsumerErrorLimitForConsumer)
            {
                _logger.Warn($"HandleNewConsumerMessageUnprocessed message will not be resend for fixtureId={fixtureId}");
            }
            else
            {
                _logger.Warn($"HandleNewConsumerMessageUnprocessed message will be resend for fixtureId={fixtureId}");
                SdkActorSystem.ActorSystem.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(10), Self, newConsumerMessage, Self);

            }
        }

        private bool ProcessNewConsumer(IConsumer consumer)
        {
            _logger.Debug($"Method=ProcessNewConsumer triggered fixtureId={consumer?.Id} currentState={State.ToString()} connectionStatus={ConnectionStatus}");
            var queue = consumer.GetQueueDetails();
            _logger.Debug($"Method=ProcessNewConsumer queueName={queue?.Name}");

            if (string.IsNullOrEmpty(queue?.Name))
            {
                _logger.Warn($"Method=ProcessNewConsumer Invalid queue details, fixtureId={consumer?.Id}");
                return false;
            }

            if (!IsModelValid)
            {
                _logger.Warn($"Method=ProcessNewConsumer AMQP model not initialized, fixtureId={consumer?.Id}");
                Self.Tell(new CreateModelMessage());
                return false;
            }

            StreamSubscriber subscriber = null;

            try
            {
                subscriber = new StreamSubscriber(_model, consumer, Dispatcher);
                subscriber.StartConsuming(queue.Name);
            }
            catch (Exception e)
            {
                _processNewConsumerErrorCounter++;
                _logger.Warn(
                    $"Method=ProcessNewConsumer StartConsuming errored errorsCout={_processNewConsumerErrorCounter} for fixtureId={consumer.Id} {e}");
                if (_processNewConsumerErrorCounter > NewConsumerErrorLimit)
                    ProcessNewConsumerErrorHandler(e);
                return false;
            }

            _logger.Debug($"Method=ProcessNewConsumer successfully executed fixtureId={consumer.Id}");

            _processNewConsumerErrorCounter = 0;
            return true;
        }

        private bool ValidateNewConsumerCanBeProcessed(IConsumer consumer)
        {
            if (consumer == null)
            {
                _logger.Warn("Method=ProcessNewConsumer Consumer is null");
                return false;
            }

            if (_streamConnection == null || !_streamConnection.IsOpen)
            {
                _logger.Warn(
                    $"Method=ProcessNewConsumer connectionStatus={ConnectionStatus} {(_streamConnection == null ? "this should not happening" : "")}");
                DisconnectedHandler(DefaultDisconnectedMessage);
                return false;
            }

            return true;
        }


        private void ProcessNewConsumerErrorHandler(Exception e)
        {
            _logger.Error($"ProcessNewConsumer limit exceeded with errorsCout={_processNewConsumerErrorCounter}  disconnected event will be raised  {e}");
            DisconnectedHandler(DefaultDisconnectedMessage);
            _processNewConsumerErrorCounter = 0;
        }

        /// <summary>
        /// Is AutoReconnect enabled
        /// </summary>
        public bool AutoReconnect { get; private set; }



        /// <summary>
        /// 
        ///     Returns the IDispatcher object that is responsible
        ///     of dispatching messages to the consumers.
        /// 
        /// </summary>
        internal IActorRef Dispatcher { get; private set; }

        public IStash Stash { get; set; }

        internal Exception ConnectionError;
        private ConnectionFactory _connectionFactory;

        internal ConnectionState State
        {
            get => _state;
            private set => _state = value;
        }

        #region Connection management

        protected virtual void CloseConnection()
        {
            if (_streamConnection != null)
            {
                _logger.Debug($"CloseConnection triggered {_streamConnection}");
                try
                {
                    {
                        _streamConnection.ConnectionShutdown -= OnConnectionShutdown;
                        if (_streamConnection.IsOpen)
                        {
                            _streamConnection.Close();
                            _logger.Debug("Connection Closed");
                        }

                    }
                }
                catch (Exception e)
                {
                    _logger.Warn($"Failed to close connection {e}");
                }

                try
                {
                    {
                        DisposeModel();
                        _streamConnection.Dispose();
                        _logger.Debug("Connection Disposed");
                    }
                }
                catch (Exception e)
                {
                    _logger.Warn($"Failed to dispose connection {e}");
                }
                _streamConnection = null;

            }
            else
            {
                _logger.Debug("No need to CloseConnection");
            }



        }

        protected void NotifyDispatcherConnectionError()
        {
            try
            {
                Dispatcher.Tell(new RemoveAllSubscribers());
            }
            catch (Exception e)
            {
                _logger.Warn($"Failed to tell diapstcher RemoveAllSubscribers diapstcher={Dispatcher}");
            }
        }

        protected virtual void EstablishConnection(ConnectionFactory factory)
        {
            // this method doesn't quit until
            // 1) a connection is established
            // 2) Dispose() is called
            //
            // therefore we will NOT quit from this method
            // when the StreamController has State = CONNECTING
            //
            // it must be called in mutual exclusion:
            // _connectionLock must be acquire before calling
            // this method.

            CloseConnection();

            _logger.DebugFormat("Connecting to the streaming server");

            if (factory == null)
            {
                _logger.Warn("Connecting to the streaming server Failed as connectionFactory=NULL");
                return;
            }

            State = ConnectionState.CONNECTING;

            long attempt = 1;
            while (!_connectionCancellation.IsCancellationRequested)
            {
                _logger.DebugFormat("Establishing connection to the streaming server, attempt={0}", attempt);

                try
                {
                    _streamConnection = factory.CreateConnection();
                    _streamConnection.ConnectionShutdown += OnConnectionShutdown;
                    _logger.Info("Connection to the streaming server correctly established");
                    CreateModel();
                    Self.Tell(new ConnectedMessage());
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error connecting to the streaming server", ex);
                    Thread.Sleep(100);
                }

                attempt++;
            }

            _logger.Warn("EstablishConnection connection Cancelled");

            State = ConnectionState.DISCONNECTED;


        }



        private void GetQueueDetailsAndEstablisConnection(IConsumer consumer)
        {
            _logger.Debug($"GetQueueDetailsAndEstablisConnection triggered state={State} isCancellationRequested={_connectionCancellation.IsCancellationRequested}");


            if (State == ConnectionState.CONNECTED || State == ConnectionState.CONNECTING ||
                _connectionCancellation.IsCancellationRequested)
            {
                _logger.Info($"GetQueueDetailsAndEstablisConnection will not be executed state={State} isCancellationRequested={_connectionCancellation.IsCancellationRequested}");
                return;
            }

            CreateConectionFactory(consumer);
            EstablishConnection(_connectionFactory);
        }

        private void CreateConectionFactory(IConsumer consumer)
        {
            QueueDetails queue = null;
            try
            {
                queue = consumer.GetQueueDetails();
                if (queue == null || string.IsNullOrEmpty(queue.Name))
                {
                    var e = new Exception("queue's name is not valid for fixtureId=" + consumer.Id);
                    ConnectionError = e;
                    throw e;
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error acquiring queue details for fixtureId=" + consumer.Id, e);
                ConnectionError = e;
                throw;
            }

            //_logger.Info($"ConnectionFactory h={queue.Host} u={queue.UserName} p={queue.Password} ch={queue.VirtualHost}");

            _connectionFactory = new ConnectionFactory
            {
                RequestedHeartbeat = UDAPI.Configuration.AMQPMissedHeartbeat,
                HostName = queue.Host,
                AutomaticRecoveryEnabled = AutoReconnect,
                Port = queue.Port,
                UserName = queue.UserName,
                Password = queue.Password,
                VirtualHost = "/" + queue.VirtualHost // this is not used anymore, we keep it for retro-compatibility
            };
        }

        internal virtual void OnConnectionShutdown(object sender, ShutdownEventArgs sea)
        {
            _logger.Error($"The AMQP connection was shutdown. AutoReconnect is enabled={AutoReconnect}, sender={sender} {sea}");


            if (!AutoReconnect)
            {
                SdkActorSystem.ActorSystem.ActorSelection(SdkActorSystem.StreamControllerActorPath).Tell(DefaultDisconnectedMessage);
            }
            else
            {
                SdkActorSystem.ActorSystem.ActorSelection(SdkActorSystem.StreamControllerActorPath).Tell(new ValidationStartMessage());
            }
        }


        private void ValidateState(ValidateStateMessage validateStateMessage)
        {
            var message = $"Method=ValidateState  currentState={State.ToString()} connectionStatus={ConnectionStatus} ";

            if (NeedRaiseDisconnect)
            {
                _logger.Warn($"{message} disconnected event will be raised");
                DisconnectedHandler(DefaultDisconnectedMessage);
            }
            else if (State != ConnectionState.DISCONNECTED && !IsModelValid)
            {
                _logger.Info($"{message}. AMQP model will be recreated");
                ReCreateModel();
            }
            else
            {
                _logger.Debug(message);
            }
        }

        private bool NeedRaiseDisconnect => State == ConnectionState.CONNECTED && (_streamConnection == null || !_streamConnection.IsOpen);
        private bool IsModelValid => _model != null && !_isModelDisposed && _model.IsOpen;

        private string ConnectionStatus => _streamConnection == null ? "NULL" : (_streamConnection.IsOpen ? "open" : "closed");

        private void DisconnectedHandler(DisconnectedMessage disconnectedMessage)
        {
            _logger.Info($"Disconnect message received");
            if (State == ConnectionState.DISCONNECTED || State == ConnectionState.CONNECTING)
            {
                _logger.Warn($"DisconnectedHandler will not be executed as currentState={State}");
            }

            if (disconnectedMessage.IDConnection != null && disconnectedMessage.IDConnection != _streamConnection?.GetHashCode())
            {
                _logger.Warn($"DisconnectedHandler will not be executed as we are already in connection with connectionHash={_streamConnection?.GetHashCode()}, messageConnectionHash={disconnectedMessage?.IDConnection}");
            }


            Become(DisconnectedState);
            NotifyDispatcherConnectionError();
            EstablishConnection(_connectionFactory);
        }

        private void DisconnecteOnDisconnectedHandler(DisconnectedMessage disconnectedMessage)
        {
            _logger.Warn($"Disconnect message On Disconnected state received messageConnectionHash={disconnectedMessage.IDConnection}");
        }

        private void ValidateConnection(ValidateConnectionMessage validateConnectionMessage)
        {
            //validate whether the reconnection was successful 
            _logger.InfoFormat("Starting validation for reconnection connHash={0}",
                _streamConnection?.GetHashCode().ToString() ?? "null");

            //in case the connection is swapped by RMQ library while the check is running
            var testConnection = _streamConnection;

            if (testConnection == null)
            {
                _logger.WarnFormat("Reconnection failed, connection has been disposed, the disconnection event needs to be raised");
                Self.Tell(new DisconnectedMessage { IDConnection = _streamConnection?.GetHashCode() });

                return;
            }

            _logger.InfoFormat("Veryfing that connection is open ? {0}", testConnection.IsOpen);

            if (testConnection.IsOpen)
            {
                Context.System.ActorSelection(SdkActorSystem.EchoControllerActorPath).Tell(new ResetAllEchoesMessage());
                _logger.InfoFormat("Reconnection successful, disconnection event will not be raised");

                Self.Tell(new ValidationSucceededMessage());
            }
            else
            {
                _logger.Warn("Connection validation failed, connection is not open - calling CloseConnection() to dispose it");
                Self.Tell(new DisconnectedMessage { IDConnection = _streamConnection?.GetHashCode() });
            }
        }

        private void ReCreateModel()
        {
            DisposeModel();
            CreateModel();
        }

        private void CreateModel()
        {
            if (_streamConnection == null || !_streamConnection.IsOpen)
            {
                _logger.Warn($"Connection is closed");
                return;
            }

            try
            {
                if (_isModelDisposed)
                {
                    _model = _streamConnection.CreateModel();
                    _isModelDisposed = false;
                    _logger.Info($"AMQP model sucessfully created, channelNo={_model.ChannelNumber}");
                }
                else
                    _logger.Debug($"AMQP model already created, channelNo={_model.ChannelNumber}");
            }
            catch (Exception e)
            {
                throw new Exception($"Creating AMQP model errored, errorsCout={_processNewConsumerErrorCounter} {e}", e);
            }
        }

        private void DisposeModel()
        {
            if (!_isModelDisposed)
            {
                _model.Dispose();
                _model = null;
                _isModelDisposed = true;
                _logger.Debug("AMQP model sucessfully disposed");
            }
            else
                _logger.Debug("AMQP model has already disposed");
        }

        #endregion

        #region Consumer

        protected virtual void AddConsumerToQueue(IConsumer consumer)
        {
            if (consumer == null)
            {
                _logger.Warn("Method=AddConsumerToQueue Consumer is null");
                return;
            }
            _logger.Debug($"Method=AddConsumerToQueue triggered fixtureId={consumer.Id}");

            var queue = consumer.GetQueueDetails();

            if (string.IsNullOrEmpty(queue?.Name))
            {
                _logger.Warn("Method=AddConsumerToQueue Invalid queue details");
                return;
            }

            if (_streamConnection == null)
            {
                _logger.Warn($"Method=AddConsumerToQueue StreamConnection is null currentState={State.ToString()}");
                Self.Tell(new DisconnectedMessage { IDConnection = _streamConnection?.GetHashCode() });
                Stash.Stash();
                return;
            }

            if (!_streamConnection.IsOpen)
            {
                _logger.Warn($"Method=AddConsumerToQueue StreamConnection is closed currentState={State.ToString()}");
                Self.Tell(new DisconnectedMessage { IDConnection = _streamConnection?.GetHashCode() });
                Stash.Stash();
                return;
            }

            if (!IsModelValid)
            {
                _logger.Warn($"Method=ProcessNewConsumer AMQP model not initialized, fixtureId={consumer?.Id}");
                Self.Tell(new CreateModelMessage());
                return;
            }

            StreamSubscriber subscriber = null;

            try
            {
                subscriber = new StreamSubscriber(_model, consumer, Dispatcher);
                subscriber.StartConsuming(queue.Name);
                _logger.Debug($"Consumer with id={consumer.Id} added to queueName={queue.Name}");
            }
            catch (Exception e)
            {
                _processNewConsumerErrorCounter++;
                _logger.Warn($"Method=AddConsumerToQueue StartConsuming errored for fixtureId={consumer.Id} {e}");
                if (_processNewConsumerErrorCounter > NewConsumerErrorLimit)
                    throw;
            }
            _logger.Debug($"Method=AddConsumerToQueue successfully executed fixtureId={consumer.Id}");
        }

        public void RemoveConsumer(IConsumer consumer)
        {
            if (consumer == null)
                throw new ArgumentNullException("consumer");

            RemoveConsumerFromQueue(consumer);
        }

        protected virtual void RemoveConsumerFromQueue(IConsumer consumer)
        {
            var subscriber = Dispatcher.Ask(new RetrieveSubscriberMessage { Id = consumer.Id }).Result as IStreamSubscriber;
            if (subscriber != null)
            {
                subscriber.StopConsuming();
                Dispatcher.Tell(new RemoveSubscriberMessage { Subscriber = subscriber });
            }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _logger.Debug("Shutting down StreamController");
            _connectionCancellation.Cancel();
            DisposeModel();
            CancelValidationMessages();
            Dispatcher.Tell(new DisposeMessage());
            Self.Tell(new DisconnectedMessage { IDConnection = _streamConnection?.GetHashCode() });

            _logger.Info("StreamController correctly disposed");
        }

        #endregion

        #region Private messages
        public class ConnectedMessage
        {

        }

        public class DisconnectedMessage
        {
            public int? IDConnection { get; set; }
        }

        private class ValidateConnectionMessage
        {
        }

        private class ValidationStartMessage
        {
        }

        private class ValidationSucceededMessage
        {
        }

        private class ValidateStateMessage
        {
        }

        private class CreateModelMessage
        {
        }

        #endregion


    }
}

