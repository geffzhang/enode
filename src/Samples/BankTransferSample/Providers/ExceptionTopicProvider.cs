﻿using ECommon.Components;
using ENode.EQueue;
using ENode.Exceptions;

namespace BankTransferSample.Providers
{
    [Component]
    public class ExceptionTopicProvider : AbstractTopicProvider<IPublishableException>
    {
        public override string GetTopic(IPublishableException source)
        {
            return "BankTransferExceptionTopic";
        }
    }
}
