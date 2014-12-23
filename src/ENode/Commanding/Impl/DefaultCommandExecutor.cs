﻿using System;
using System.Collections.Generic;
using System.Linq;
using ECommon.Logging;
using ENode.Domain;
using ENode.Eventing;
using ENode.Exceptions;
using ENode.Infrastructure;

namespace ENode.Commanding.Impl
{
    public class DefaultCommandExecutor : ICommandExecutor
    {
        #region Private Variables

        private readonly ICommandStore _commandStore;
        private readonly IEventStore _eventStore;
        private readonly IWaitingCommandService _waitingCommandService;
        private readonly IExecutedCommandService _executedCommandService;
        private readonly IHandlerProvider<ICommandHandler> _commandHandlerProvider;
        private readonly ITypeCodeProvider<IAggregateRoot> _aggregateRootTypeProvider;
        private readonly IEventService _eventService;
        private readonly IEventPublishInfoStore _eventPublishInfoStore;
        private readonly IPublisher<IPublishableException> _exceptionPublisher;
        private readonly IRetryCommandService _retryCommandService;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger _logger;

        #endregion

        #region Constructors

        public DefaultCommandExecutor(
            ICommandStore commandStore,
            IEventStore eventStore,
            IWaitingCommandService waitingCommandService,
            IExecutedCommandService executedCommandService,
            IHandlerProvider<ICommandHandler> commandHandlerProvider,
            ITypeCodeProvider<IAggregateRoot> aggregateRootTypeProvider,
            IEventService eventService,
            IEventPublishInfoStore eventPublishInfoStore,
            IPublisher<IPublishableException> exceptionPublisher,
            IRetryCommandService retryCommandService,
            IMemoryCache memoryCache,
            ILoggerFactory loggerFactory)
        {
            _commandStore = commandStore;
            _eventStore = eventStore;
            _waitingCommandService = waitingCommandService;
            _executedCommandService = executedCommandService;
            _commandHandlerProvider = commandHandlerProvider;
            _aggregateRootTypeProvider = aggregateRootTypeProvider;
            _eventService = eventService;
            _eventPublishInfoStore = eventPublishInfoStore;
            _exceptionPublisher = exceptionPublisher;
            _retryCommandService = retryCommandService;
            _memoryCache = memoryCache;
            _logger = loggerFactory.Create(GetType().FullName);
            _waitingCommandService.SetCommandExecutor(this);
            _retryCommandService.SetCommandExecutor(this);
        }

        #endregion

        #region Public Methods

        public void Execute(ProcessingCommand processingCommand)
        {
            var command = processingCommand.Command;
            var context = processingCommand.CommandExecuteContext;
            var commandHandler = default(ICommandHandler);

            //如果是非操作聚合根的command，且该command已检测到被处理过，则直接忽略。
            if (!(command is IAggregateCommand) && _commandStore.Get(command.Id) != null)
            {
                return;
            }

            //首先验证command handler是否存在
            try
            {
                var commandHandlers = _commandHandlerProvider.GetHandlers(command.GetType());
                if (!commandHandlers.Any())
                {
                    throw new CommandHandlerNotFoundException(command);
                }
                else if (commandHandlers.Count() > 1)
                {
                    throw new CommandHandlerTooManyException(command);
                }
                commandHandler = commandHandlers.Single();
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                NotifyCommandExecuteFailedOrNothingChanged(processingCommand, CommandStatus.Failed, ex.GetType().Name, ex.Message);
                return;
            }

            //判断当前command是否需要排队，如果需要排队，则会先排队；
            //框架会对每个被执行的command按照聚合根id为分组进行必要的排队，如果发现一个聚合根有两个或以上的command要执行，
            //那会将第二个command以及之后的command全部排队；
            //这样的设计可以确保同一个聚合根不会有两个command在执行，从而可以确保EventStore不会产生并发冲突；
            if (context.CheckCommandWaiting && _waitingCommandService.RegisterCommand(processingCommand))
            {
                return;
            }

            //调用command handler执行当前command
            var handleSuccess = false;
            try
            {
                commandHandler.Handle(context, command);
                _logger.DebugFormat("Handle command success. commandHandlerType:{0}, commandType:{1}, commandId:{2}, aggregateRootId:{3}",
                    commandHandler.GetInnerHandler().GetType().Name,
                    command.GetType().Name,
                    command.Id,
                    processingCommand.AggregateRootId);
                handleSuccess = true;
            }
            catch (IOException ex)
            {
                _logger.Error(ex);
                _retryCommandService.RetryCommand(processingCommand);
                return;
            }
            catch (Exception ex)
            {
                HandleCommandException(processingCommand, commandHandler, ex);
            }

            //如果command执行成功，则提交执行后的结果
            if (handleSuccess)
            {
                if (command is IAggregateCommand)
                {
                    CommitAggregateChanges(processingCommand);
                }
                else
                {
                    CommitChanges(processingCommand);
                }
            }
        }

        #endregion

        #region Private Methods

        private void CommitAggregateChanges(ProcessingCommand processingCommand)
        {
            var command = processingCommand.Command;
            var context = processingCommand.CommandExecuteContext;
            var trackedAggregateRoots = context.GetTrackedAggregateRoots();
            var dirtyAggregateRootChanges = trackedAggregateRoots.ToDictionary(x => x, x => x.GetChanges()).Where(x => x.Value.Any());
            var dirtyAggregateRootCount = dirtyAggregateRootChanges.Count();

            //如果当前command没有对任何聚合根做修改，则认为当前command已经处理结束，返回command的结果为NothingChanged
            if (dirtyAggregateRootCount == 0)
            {
                _logger.DebugFormat("No aggregate created or modified by command. commandType:{0}, commandId:{1}",
                    command.GetType().Name,
                    command.Id);
                NotifyCommandExecuteFailedOrNothingChanged(processingCommand, CommandStatus.NothingChanged, null, null);
                return;
            }
            //如果被创建或修改的聚合根多于一个，则认为当前command处理失败，一个command只能创建或修改一个聚合根；
            else if (dirtyAggregateRootCount > 1)
            {
                var dirtyAggregateTypes = string.Join("|", dirtyAggregateRootChanges.Select(x => x.Key.GetType().Name));
                var errorMessage = string.Format("Detected more than one aggregate created or modified by command. commandType:{0}, commandId:{1}, dirty aggregate types:{2}",
                    command.GetType().Name,
                    command.Id,
                    dirtyAggregateTypes);
                _logger.ErrorFormat(errorMessage);
                NotifyCommandExecuteFailedOrNothingChanged(processingCommand, CommandStatus.Failed, null, errorMessage);
                return;
            }

            //获取当前被修改的聚合根
            var dirtyAggregateRootChange = dirtyAggregateRootChanges.Single();
            var dirtyAggregateRoot = dirtyAggregateRootChange.Key;
            var changedEvents = dirtyAggregateRootChange.Value;

            //构造出一个事件流对象
            var eventStream = BuildDomainEventStream(dirtyAggregateRoot, changedEvents, processingCommand);

            //如果是重试中的command，则直接提交该command产生的事件，因为该command肯定已经在command store中了
            if (processingCommand.RetriedCount > 0)
            {
                _eventService.CommitEvent(new EventCommittingContext(dirtyAggregateRoot, eventStream, processingCommand));
                return;
            }

            //尝试将当前已执行的command添加到commandStore
            string sourceId;
            string sourceType;
            processingCommand.Items.TryGetValue("SourceId", out sourceId);
            processingCommand.Items.TryGetValue("SourceType", out sourceType);
            var commandAddResult = _commandStore.Add(new HandledAggregateCommand(command, sourceId, sourceType, eventStream.AggregateRootId, eventStream.AggregateRootTypeCode));

            //如果command添加成功，则提交该command产生的事件
            if (commandAddResult == CommandAddResult.Success)
            {
                _eventService.CommitEvent(new EventCommittingContext(dirtyAggregateRoot, eventStream, processingCommand));
            }
            //如果添加的结果是command重复，则做如下处理
            else if (commandAddResult == CommandAddResult.DuplicateCommand)
            {
                var existingHandledCommand = _commandStore.Get(command.Id) as HandledAggregateCommand;
                if (existingHandledCommand != null)
                {
                    var existingEventStream = _eventStore.Find(existingHandledCommand.AggregateRootId, command.Id);
                    if (existingEventStream != null)
                    {
                        //如果当前command已经被持久化过了，且该command产生的事件也已经被持久化了，则只要再做一遍发布事件的操作
                        _eventService.PublishDomainEvent(processingCommand, existingEventStream);
                    }
                    else
                    {
                        //如果当前command已经被持久化过了，但事件没有被持久化，则需要重新提交当前command所产生的事件；
                        _eventService.CommitEvent(new EventCommittingContext(dirtyAggregateRoot, eventStream, processingCommand));
                    }
                }
                else
                {
                    //到这里，说明当前command想添加到commandStore中时，提示command重复，但是尝试从commandStore中取出该command时却找不到该command。
                    //出现这种情况，我们就无法再做后续处理了，这种错误理论上不会出现，除非commandStore的Add接口和Get接口出现读写不一致的情况；
                    //我们记录错误日志，然后认为当前command已被处理为失败。
                    var errorMessage = string.Format("Command exist in the command store, but it cannot be found from the command store. commandType:{0}, commandId:{1}",
                        command.GetType().Name,
                        command.Id);
                    NotifyCommandExecuteFailedOrNothingChanged(processingCommand, CommandStatus.Failed, null, errorMessage);
                }
            }
        }
        private DomainEventStream BuildDomainEventStream(IAggregateRoot aggregateRoot, IEnumerable<IDomainEvent> changedEvents, ProcessingCommand processingCommand)
        {
            var command = processingCommand.Command;
            var aggregateRootTypeCode = _aggregateRootTypeProvider.GetTypeCode(aggregateRoot.GetType());

            return new DomainEventStream(
                command.Id,
                aggregateRoot.UniqueId,
                aggregateRootTypeCode,
                aggregateRoot.Version + 1,
                DateTime.Now,
                changedEvents,
                processingCommand.Items);
        }
        private void HandleCommandException(ProcessingCommand processingCommand, ICommandHandler commandHandler, Exception exception)
        {
            //判断当前command之前有没有被执行过。
            //如果有执行过，则继续判断是否后续所有的步骤都执行成功了，对每一种情况做相应的处理；
            //如果未执行过，则也做相应处理。
            var command = processingCommand.Command;

            try
            {
                var existingHandledCommand = _commandStore.Get(command.Id);
                if (existingHandledCommand != null)
                {
                    if (command is IAggregateCommand)
                    {
                        var existingHandledAggregateCommand = (HandledAggregateCommand)existingHandledCommand;
                        var existingEventStream = _eventStore.Find(existingHandledAggregateCommand.AggregateRootId, command.Id);
                        if (existingEventStream != null)
                        {
                            _eventService.PublishDomainEvent(processingCommand, existingEventStream);
                        }
                        else
                        {
                            //到这里，说明当前command执行遇到异常，然后该command在commandStore中存在，
                            //但是在eventStore中不存在，此时可以理解为该command还没被执行，此时做如下操作：
                            //1.将command从commandStore中移除
                            //2.记录command执行的错误日志
                            //3.根据EventStore里的事件刷新缓存，目的是为了还原聚合根状态，因为该聚合根的状态有可能已经被污染
                            //4.重试该command
                            _commandStore.Remove(command.Id);
                            LogCommandExecuteException(processingCommand, commandHandler, exception);
                            _memoryCache.RefreshAggregateFromEventStore(existingHandledAggregateCommand.AggregateRootTypeCode, existingHandledAggregateCommand.AggregateRootId);
                            _retryCommandService.RetryCommand(processingCommand);
                        }
                    }
                }
                else
                {
                    //到这里，说明当前command执行遇到异常，然后当前command之前也没执行过，是第一次被执行。
                    //那就判断当前异常是否是需要被发布出去的异常，如果是，则发布该异常给所有消费者；否则，就记录错误日志，然后认为该command处理失败即可。
                    var publishableException = exception as IPublishableException;
                    if (publishableException != null)
                    {
                        _exceptionPublisher.Publish(publishableException);
                        NotifyCommandExecuteFailedOrNothingChanged(processingCommand, CommandStatus.Success, null, null);
                    }
                    else
                    {
                        LogCommandExecuteException(processingCommand, commandHandler, exception);
                        NotifyCommandExecuteFailedOrNothingChanged(processingCommand, CommandStatus.Failed, exception.GetType().Name, exception.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                var commandHandlerType = commandHandler.GetInnerHandler().GetType();
                var errorMessage = string.Format("{0} raised when {1} handling the exception of {2}. commandId:{3}, aggregateRootId:{4}, originalExceptionMessage:{5}",
                    ex.GetType().Name,
                    commandHandlerType.Name,
                    command.GetType().Name,
                    command.Id,
                    processingCommand.AggregateRootId,
                    exception.Message);
                _logger.Error(errorMessage, ex);
                _retryCommandService.RetryCommand(processingCommand);
            }
        }
        private void NotifyCommandExecuteFailedOrNothingChanged(ProcessingCommand processingCommand, CommandStatus commandStatus, string exceptionTypeName, string errorMessage)
        {
            var aggregateCommand = processingCommand.Command as IAggregateCommand;
            if (aggregateCommand != null)
            {
                _executedCommandService.ProcessExecutedCommand(
                    processingCommand.CommandExecuteContext,
                    aggregateCommand,
                    commandStatus,
                    processingCommand.AggregateRootId,
                    exceptionTypeName,
                    errorMessage);
            }
            else
            {
                _executedCommandService.ProcessExecutedCommand(
                    processingCommand.CommandExecuteContext,
                    processingCommand.Command,
                    commandStatus,
                    exceptionTypeName,
                    errorMessage);
            }
        }
        private void CommitChanges(ProcessingCommand processingCommand)
        {
            var command = processingCommand.Command;
            string sourceId;
            string sourceType;
            processingCommand.Items.TryGetValue("SourceId", out sourceId);
            processingCommand.Items.TryGetValue("SourceType", out sourceType);
            var evnts = processingCommand.CommandExecuteContext.GetEvents().ToList();
            var commandAddResult = _commandStore.Add(new HandledCommand(command, sourceId, sourceType, evnts));

            if (commandAddResult == CommandAddResult.Success)
            {
                _eventService.PublishEvent(processingCommand, new EventStream(command.Id, evnts, processingCommand.Items));
            }
            else if (commandAddResult == CommandAddResult.DuplicateCommand)
            {
                var existingHandledCommand = _commandStore.Get(command.Id);
                if (existingHandledCommand != null)
                {
                    _eventService.PublishEvent(processingCommand, new EventStream(command.Id, existingHandledCommand.Events, processingCommand.Items));
                }
                else
                {
                    //到这里，说明当前command想添加到commandStore中时，提示command重复，但是尝试从commandStore中取出该command时却找不到该command。
                    //出现这种情况，我们就无法再做后续处理了，这种错误理论上不会出现，除非commandStore的Add接口和Get接口出现读写不一致的情况；
                    //我们记录错误日志，然后认为当前command已被处理为失败。
                    var errorMessage = string.Format("Command exist in the command store, but it cannot be found from the command store. commandType:{0}, commandId:{1}",
                        command.GetType().Name,
                        command.Id);
                    NotifyCommandExecuteFailedOrNothingChanged(processingCommand, CommandStatus.Failed, null, errorMessage);
                }
            }
        }
        private void LogCommandExecuteException(ProcessingCommand processingCommand, ICommandHandler commandHandler, Exception exception)
        {
            var errorMessage = string.Format("{0} raised when {1} handling {2}. commandId:{3}, aggregateRootId:{4}",
                exception.GetType().Name,
                commandHandler.GetInnerHandler().GetType().Name,
                processingCommand.Command.GetType().Name,
                processingCommand.Command.Id,
                processingCommand.AggregateRootId);
            _logger.Error(errorMessage, exception);
        }

        #endregion
    }
}
