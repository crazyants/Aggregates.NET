﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aggregates.Exceptions;
using Aggregates.Messages;
using NServiceBus;
using NServiceBus.ObjectBuilder;
using NServiceBus.Pipeline;
using NServiceBus.Pipeline.Contexts;
using NServiceBus.Logging;
using Metrics;

namespace Aggregates.Internal
{
    internal class ExceptionFilter : IBehavior<IncomingContext>
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(ExceptionFilter));

        private static Meter _errorsMeter = Metric.Meter("Business Exceptions", Unit.Errors);
        private readonly IBus _bus;

        public ExceptionFilter(IBus bus)
        {
            _bus = bus;
        }

        public void Invoke(IncomingContext context, Action next)
        {
            if (context.IncomingLogicalMessage.Instance is ICommand)
            {
                try
                {
                    next();
                    // Tell the sender the command was accepted
                    var acceptance = context.Builder.Build<Func<Accept>>();
                    _bus.Return(0);
                }
                catch (BusinessException e)
                {
                    _errorsMeter.Mark();
                    Logger.InfoFormat("Command {0} was rejected\nException: {1}", context.IncomingLogicalMessage.MessageType.FullName, e);
                    // Tell the sender the command was rejected due to a business exception
                    var rejection = context.Builder.Build<Func<Exception, Reject>>();
                    _bus.Reply(rejection(e));
                    // Don't throw exception to NServicebus because we don't wish to retry this command

                }
                return;

            }

            next();
        }
    }

    internal class ExceptionFilterRegistration : RegisterStep
    {
        public ExceptionFilterRegistration()
            : base("ExceptionFilter", typeof(ExceptionFilter), "Filters [BusinessException] from processing failures")
        {
            InsertBefore(WellKnownStep.LoadHandlers);

        }
    }
}
