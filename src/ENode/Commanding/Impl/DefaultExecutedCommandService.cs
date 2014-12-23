﻿namespace ENode.Commanding.Impl
{
    public class DefaultExecutedCommandService : IExecutedCommandService
    {
        private IWaitingCommandService _waitingCommandService;

        public DefaultExecutedCommandService(IWaitingCommandService waitingCommandService)
        {
            _waitingCommandService = waitingCommandService;
        }

        public void ProcessExecutedCommand(ICommandExecuteContext context, IAggregateCommand command, CommandStatus commandStatus, string aggregateRootId, string exceptionTypeName, string errorMessage)
        {
            _waitingCommandService.NotifyCommandExecuted(aggregateRootId);
            context.OnCommandExecuted(command, commandStatus, aggregateRootId, exceptionTypeName, errorMessage);
        }
        public void ProcessExecutedCommand(ICommandExecuteContext context, ICommand command, CommandStatus commandStatus, string exceptionTypeName, string errorMessage)
        {
            context.OnCommandExecuted(command, commandStatus, null, exceptionTypeName, errorMessage);
        }
    }
}
