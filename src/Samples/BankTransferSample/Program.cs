﻿using System;
using System.Reflection;
using BankTransferSample.Commands;
using BankTransferSample.Domain;
using BankTransferSample.EventHandlers;
using ECommon.Autofac;
using ECommon.Components;
using ECommon.Configurations;
using ECommon.JsonNet;
using ECommon.Log4Net;
using ENode.Commanding;
using ENode.Configurations;

namespace BankTransferSample
{
    class Program
    {
        static ENodeConfiguration _configuration;

        static void Main(string[] args)
        {
            InitializeENodeFramework();

            var commandService = ObjectContainer.Resolve<ICommandService>();
            var syncHelper = ObjectContainer.Resolve<SyncHelper>();

            Console.WriteLine(string.Empty);

            //创建两个银行账户
            commandService.Execute(new CreateAccountCommand("00001", "雪华"), CommandReturnType.EventHandled).Wait();
            commandService.Execute(new CreateAccountCommand("00002", "凯锋"), CommandReturnType.EventHandled).Wait();

            Console.WriteLine(string.Empty);

            //每个账户都存入1000元
            commandService.Send(new StartDepositTransactionCommand("00001", 1000));
            syncHelper.WaitOne();
            commandService.Send(new StartDepositTransactionCommand("00002", 1000));
            syncHelper.WaitOne();

            Console.WriteLine(string.Empty);

            //账户1向账户3转账300元，交易会失败，因为账户3不存在
            commandService.Send(new StartTransferTransactionCommand(new TransferTransactionInfo("00001", "00003", 300D)));
            syncHelper.WaitOne();
            Console.WriteLine(string.Empty);

            //账户1向账户2转账1200元，交易会失败，因为余额不足
            commandService.Send(new StartTransferTransactionCommand(new TransferTransactionInfo("00001", "00002", 1200D)));
            syncHelper.WaitOne();
            Console.WriteLine(string.Empty);

            //账户2向账户1转账500元，交易成功
            commandService.Send(new StartTransferTransactionCommand(new TransferTransactionInfo("00002", "00001", 500D)));
            syncHelper.WaitOne();
            Console.WriteLine(string.Empty);

            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
            _configuration.ShutdownEQueue();
        }

        static void InitializeENodeFramework()
        {
            var assemblies = new[] { Assembly.GetExecutingAssembly() };

            _configuration = Configuration
                .Create()
                .UseAutofac()
                .RegisterCommonComponents()
                .UseLog4Net()
                .UseJsonNet()
                .CreateENode()
                .RegisterENodeComponents()
                .RegisterBusinessComponents(assemblies)
                .UseEQueue()
                .InitializeBusinessAssemblies(assemblies)
                .StartENode(NodeType.CommandProcessor | NodeType.EventProcessor | NodeType.ExceptionProcessor)
                .StartEQueue();

            Console.WriteLine(string.Empty);
            Console.WriteLine("ENode started...");
        }
    }
}
