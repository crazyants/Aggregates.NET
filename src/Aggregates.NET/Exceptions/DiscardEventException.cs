﻿using System;

namespace Aggregates.Exceptions
{
    /// <summary>
    /// Used by conflict resolvers to indicate that the event should just be discarded, not merged into the stream
    /// </summary>
    public class DiscardEventException : Exception
    {
        public DiscardEventException() { }
        public DiscardEventException(string message) : base(message) { }
    }
}
