using System;
using NServiceBus.ObjectBuilder;
using System.Reflection;
using Common.Logging;
using System.Collections.Generic;
using NServiceBus.Saga;

namespace NServiceBus.Sagas.Impl
{
    using System.Linq;
    using Unicast;

    /// <summary>
    /// A message handler central to the saga infrastructure.
    /// </summary>
    public class SagaMessageHandler : IMessageHandler<object>
    {
        /// <summary>
        /// Used to notify timeout manager of sagas that have completed.
        /// </summary>
        public IUnicastBus Bus { get; set; }

        /// <summary>
        /// Handles a message.
        /// </summary>
        /// <param name="message">The message to handle.</param>
        /// <remarks>
        /// If the message received needs to start a new saga, then a new
        /// saga instance will be created and will be saved using the <see cref="ISagaPersister"/>
        /// implementation provided in the configuration.  Any other message implementing 
        /// <see cref="ISagaMessage"/> will cause the existing saga instance with which it is
        /// associated to continue.</remarks>
        public void Handle(object message)
        {
            if (!NeedToHandle(message))
                return;

            var entitiesHandled = new List<ISagaEntity>();
            var sagaTypesHandled = new List<Type>();

            foreach (IFinder finder in Configure.GetFindersFor(message))
            {
                ISaga saga;
                bool sagaEntityIsPersistent = true;
                ISagaEntity sagaEntity = UseFinderToFindSaga(finder, message);

                if (sagaEntity == null)
                {
                    Type sagaToCreate = Configure.GetSagaTypeToStartIfMessageNotFoundByFinder(message, finder);
                    if (sagaToCreate == null)
                        continue;

                    if (sagaTypesHandled.Contains(sagaToCreate))
                        continue; // don't create the same saga type twice for the same message

                    sagaTypesHandled.Add(sagaToCreate);

                    Type sagaEntityType = Configure.GetSagaEntityTypeForSagaType(sagaToCreate);
                    sagaEntity = Activator.CreateInstance(sagaEntityType) as ISagaEntity;

                    if (sagaEntity != null)
                    {
                        if (message is ISagaMessage)
                            sagaEntity.Id = (message as ISagaMessage).SagaId;
                        else
                            sagaEntity.Id = GenerateSagaId();

                        sagaEntity.Originator = Bus.CurrentMessageContext.ReplyToAddress.ToString();
                        sagaEntity.OriginalMessageId = Bus.CurrentMessageContext.Id;

                        sagaEntityIsPersistent = false;
                    }

                    saga = Builder.Build(sagaToCreate) as ISaga;

                }
                else
                {
                    if (entitiesHandled.Contains(sagaEntity))
                        continue; // don't call the same saga twice

                    saga = Builder.Build(Configure.GetSagaTypeForSagaEntityType(sagaEntity.GetType())) as ISaga;
                }

                if (saga != null)
                {
                    saga.Entity = sagaEntity;

                    HaveSagaHandleMessage(saga, message, sagaEntityIsPersistent);

                    sagaTypesHandled.Add(saga.GetType());
                }

                entitiesHandled.Add(sagaEntity);
            }

            if (entitiesHandled.Count == 0)
            {
                logger.InfoFormat("Could not find a saga for the message type {0} with id {1}. Going to invoke SagaNotFoundHandlers.", message.GetType().FullName, Bus.CurrentMessageContext.Id);
                foreach (var handler in Builder.BuildAll<IHandleSagaNotFound>())
                {
                    logger.DebugFormat("Invoking SagaNotFoundHandler: {0}", handler.GetType().FullName);
                    handler.Handle(message);
                }
            }
        }

        /// <summary>
        /// Decides whether the given message should be handled by the saga infrastructure
        /// </summary>
        /// <param name="message">The message being processed</param>
        /// <returns></returns>
        bool NeedToHandle(object message)
        {
            if (message.IsTimeoutMessage())
            {
                var expired = message.TimeoutHasExpired();

                if (!expired)
                    Bus.HandleCurrentMessageLater();

                return expired;
            }

            if (message is ISagaMessage)
                return true;

            return Configure.IsMessageTypeHandledBySaga(message.GetType());
        }

        /// <summary>
        /// Generates a new id for a saga.
        /// </summary>
        /// <returns></returns>
        Guid GenerateSagaId()
        {
            return GuidCombGenerator.Generate();
        }

      
        /// <summary>
        /// Asks the given finder to find the saga entity using the given message.
        /// </summary>
        /// <param name="finder"></param>
        /// <param name="message"></param>
        /// <returns>The saga entity if found, otherwise null.</returns>
        static ISagaEntity UseFinderToFindSaga(IFinder finder, object message)
        {
            MethodInfo method = Configure.GetFindByMethodForFinder(finder, message);

            if (method != null)
                return method.Invoke(finder, new object[] { message }) as ISagaEntity;

            return null;
        }

        /// <summary>
        /// Dispatches the message to the saga and, based on the saga's state
        /// persists it or notifies of its completion to interested parties.
        /// </summary>
        /// <param name="saga"></param>
        /// <param name="message"></param>
        /// <param name="sagaIsPersistent"></param>
        void HaveSagaHandleMessage(ISaga saga, object message, bool sagaIsPersistent)
        {
            if (message.IsTimeoutMessage())
                DispatchTimeoutMessageToSaga(saga, message);
            else
                CallHandleMethodOnSaga(saga, message);

            if (!saga.Completed)
            {
                if (!sagaIsPersistent)
                    Persister.Save(saga.Entity);
                else
                    Persister.Update(saga.Entity);
            }
            else
            {
                if (sagaIsPersistent)
                    Persister.Complete(saga.Entity);

                NotifyTimeoutManagerThatSagaHasCompleted(saga);
            }

            LogIfSagaIsFinished(saga);
        }

        void DispatchTimeoutMessageToSaga(ITimeoutable saga, object message)
        {
            var tm = message as TimeoutMessage;
            if (tm != null)
            {
                saga.Timeout(tm.State);
                return;
            }

            var messageType = message.GetType();

            // search for the timeout method using reflection
            var methodInfo = saga.GetType().GetMethod("Timeout", new[] { messageType });

            if (methodInfo == null || methodInfo.GetParameters().First().ParameterType != messageType)
                throw new InvalidOperationException(string.Format("Timeout arrived with state {0}, but no method with signature void Timeout({0}). Please implement IHandleTimeouts<{0}> on your {1} class", messageType, saga.GetType()));
            
            methodInfo.Invoke(saga, new[] { message });
        }

        void NotifyTimeoutManagerThatSagaHasCompleted(ISaga saga)
        {
            Bus.ClearTimeoutsFor(saga.Entity.Id);
        }

        /// <summary>
        /// Logs that a saga has completed.
        /// </summary>
        void LogIfSagaIsFinished(ISaga saga)
        {
            if (saga.Completed)
                logger.Debug(string.Format("{0} {1} has completed.", saga.GetType().FullName, saga.Entity.Id));
        }

        /// <summary>
        /// Invokes the handler method on the saga for the message.
        /// </summary>
        /// <param name="saga">The saga on which to call the handle method.</param>
        /// <param name="message">The message to pass to the handle method.</param>
        void CallHandleMethodOnSaga(object saga, object message)
        {
            var method = Configure.GetHandleMethodForSagaAndMessage(saga, message);

            if (method != null)
                method.Invoke(saga, new [] { message });
        }

        
        /// <summary>
        /// Get or Set Builder for Saga Message Handler
        /// </summary>
        public IBuilder Builder { get; set; }

        /// <summary>
        /// Get or Set Saga Persister
        /// </summary>
        public ISagaPersister Persister { get; set; }

        
        readonly ILog logger = LogManager.GetLogger(typeof(SagaMessageHandler));
    }
}
