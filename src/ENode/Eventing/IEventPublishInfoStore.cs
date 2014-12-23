﻿namespace ENode.Eventing
{
    /// <summary>Represents a storage to store the event publish information of aggregate.
    /// </summary>
    public interface IEventPublishInfoStore
    {
        /// <summary>Insert the first published event version of aggregate.
        /// </summary>
        void InsertPublishedVersion(string eventProcessorName, string aggregateRootId);
        /// <summary>Update the published event version of aggregate.
        /// </summary>
        void UpdatePublishedVersion(string eventProcessorName, string aggregateRootId, int version);
        /// <summary>Get the current event published version for the specified aggregate.
        /// </summary>
        int GetEventPublishedVersion(string eventProcessorName, string aggregateRootId);
    }
}
