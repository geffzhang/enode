﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ECommon.Logging;
using ECommon.Retring;
using ECommon.Scheduling;
using ENode.Commanding;
using ENode.Configurations;
using ENode.Domain;
using ENode.Infrastructure;
using ENode.Infrastructure.Impl;

namespace ENode.Exceptions.Impl
{
    public class DefaultExceptionProcessor : IProcessor<IPublishableException>
    {
        #region Private Variables

        private int _workerCount = 1;
        private readonly ITypeCodeProvider<IExceptionHandler> _exceptionHandlerTypeCodeProvider;
        private readonly ITypeCodeProvider<ICommand> _commandTypeCodeProvider;
        private readonly IHandlerProvider<IExceptionHandler> _exceptionHandlerProvider;
        private readonly ICommandService _commandService;
        private readonly IRepository _repository;
        private readonly IActionExecutionService _actionExecutionService;
        private readonly ILogger _logger;
        private readonly IList<BlockingCollection<IProcessingContext>> _queueList;
        private readonly IList<Worker> _workerList;
        private bool _isStarted;

        #endregion

        public string Name { get; set; }

        #region Constructors

        public DefaultExceptionProcessor(
            ITypeCodeProvider<IExceptionHandler> exceptionHandlerTypeCodeProvider,
            ITypeCodeProvider<ICommand> commandTypeCodeProvider,
            IHandlerProvider<IExceptionHandler> exceptionHandlerProvider,
            ICommandService commandService,
            IRepository repository,
            IActionExecutionService actionExecutionService,
            ILoggerFactory loggerFactory)
        {
            Name = GetType().Name;
            _exceptionHandlerTypeCodeProvider = exceptionHandlerTypeCodeProvider;
            _commandTypeCodeProvider = commandTypeCodeProvider;
            _exceptionHandlerProvider = exceptionHandlerProvider;
            _commandService = commandService;
            _repository = repository;
            _actionExecutionService = actionExecutionService;
            _logger = loggerFactory.Create(GetType().FullName);
            _queueList = new List<BlockingCollection<IProcessingContext>>();
            _workerCount = ENodeConfiguration.Instance.Setting.ExceptionProcessorParallelThreadCount;
            _workerList = new List<Worker>();
            for (var index = 0; index < _workerCount; index++)
            {
                _queueList.Add(new BlockingCollection<IProcessingContext>());
            }
        }

        #endregion

        public void Start()
        {
            if (_isStarted) return;

            for (var index = 0; index < _workerCount; index++)
            {
                var queue = _queueList[index];
                var worker = new Worker("ProcessException", () =>
                {
                    ProcessException(queue.Take());
                });
                _workerList.Add(worker);
                worker.Start();
            }
            _isStarted = true;
        }
        public void Process(IPublishableException publishableException, IProcessContext<IPublishableException> context)
        {
            QueueProcessingContext(publishableException.UniqueId, new PublishableExceptionProcessingContext(this, publishableException, context));
        }

        #region Private Methods

        private void QueueProcessingContext(object hashKey, IProcessingContext processingContext)
        {
            var queueIndex = hashKey.GetHashCode() % _workerCount;
            if (queueIndex < 0)
            {
                queueIndex = Math.Abs(queueIndex);
            }
            _queueList[queueIndex].Add(processingContext);
        }
        private void ProcessException(IProcessingContext context)
        {
            _actionExecutionService.TryAction(context.Name, context.Process, 3, new ActionInfo(context.Name + "Callback", context.Callback, null, null));
        }
        private bool DispatchExceptionToHandler(IPublishableException exception, IExceptionHandler exceptionHandler)
        {
            var handlerType = exceptionHandler.GetInnerHandler().GetType();
            var handlerTypeCode = _exceptionHandlerTypeCodeProvider.GetTypeCode(handlerType);
            var handlingContext = new DefaultHandlingContext(_repository);

            try
            {
                exceptionHandler.Handle(handlingContext, exception);
                var commands = handlingContext.GetCommands();
                if (commands.Any())
                {
                    foreach (var command in commands)
                    {
                        command.Id = BuildCommandId(command, exception, handlerTypeCode);
                        _commandService.Send(command, exception.UniqueId, "Exception");
                        _logger.DebugFormat("Send command success, commandType:{0}, commandId:{1}, exceptionHandlerType:{2}, exceptionType:{3}, exceptionId:{4}",
                            command.GetType().Name,
                            command.Id,
                            handlerType.Name,
                            exception.GetType().Name,
                            exception.UniqueId);
                    }
                }
                _logger.DebugFormat("Handle exception success. exceptionId:{0}, exceptionType:{1}, handlerType:{2}",
                    exception.UniqueId,
                    exception.GetType().Name,
                    handlerType.Name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Exception raised when [{0}] handling [{1}].", handlerType.Name, exception.GetType().Name), ex);
                return false;
            }
        }
        private string BuildCommandId(ICommand command, IPublishableException exception, int exceptionHandlerTypeCode)
        {
            var key = command.GetKey();
            var commandKey = key == null ? string.Empty : key.ToString();
            var commandTypeCode = _commandTypeCodeProvider.GetTypeCode(command.GetType());
            return string.Format("{0}{1}{2}{3}", exception.UniqueId, commandKey, exceptionHandlerTypeCode, commandTypeCode);
        }

        #endregion

        class PublishableExceptionProcessingContext : ProcessingContext<IPublishableException>
        {
            private DefaultExceptionProcessor _processor;

            public PublishableExceptionProcessingContext(DefaultExceptionProcessor processor, IPublishableException publishableException, IProcessContext<IPublishableException> exceptionProcessContext)
                : base("ProcessPublishableException", publishableException, exceptionProcessContext)
            {
                _processor = processor;
            }
            public override bool Process()
            {
                var publishableException = Message;
                var success = true;

                foreach (var exceptionHandler in _processor._exceptionHandlerProvider.GetHandlers(publishableException.GetType()))
                {
                    if (!_processor.DispatchExceptionToHandler(publishableException, exceptionHandler))
                    {
                        success = false;
                    }
                }

                return success;
            }
        }
    }
}
