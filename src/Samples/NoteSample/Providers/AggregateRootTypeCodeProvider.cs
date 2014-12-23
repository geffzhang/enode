﻿using ECommon.Components;
using ENode.Domain;
using ENode.Infrastructure.Impl;
using NoteSample.Domain;

namespace NoteSample.Providers
{
    [Component]
    public class AggregateRootTypeCodeProvider : DefaultTypeCodeProvider<IAggregateRoot>
    {
        public AggregateRootTypeCodeProvider()
        {
            RegisterType<Note>(100);
        }
    }
}
