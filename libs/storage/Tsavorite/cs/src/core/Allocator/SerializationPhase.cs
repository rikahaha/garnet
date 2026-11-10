// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Tsavorite.core
{
    public enum SerializationPhase : int
    {
        /// <summary>
        /// Serialization has not been started.
        /// </summary>
        REST,

        /// <summary>
        /// Serialization is in progress.
        /// </summary>
        SERIALIZING,

        /// <summary>
        /// The (v) data was serialized ahead of time by a checkpoint, while the object is still live (not superseded
        /// by a CopyUpdate). Returns to <see cref="REST"/> when that checkpoint's flush completes. Ordered below
        /// <see cref="SERIALIZED"/>, so it does not count as the terminal state.
        /// </summary>
        PRESERIALIZED,

        /// <summary>
        /// Research prototype (snapshot views): during a checkpoint, a CopyUpdate superseded the object and, instead of serializing
        /// its (v) data, opened an O(1) snapshot view of it. No thread is reading the view yet. A flush takes the view over
        /// (<see cref="VIEW_FLUSHING_SUPERSEDED"/>); the end of the checkpoint closes it if no flush did.
        /// </summary>
        VIEW_OPEN,

        /// <summary>
        /// Research prototype (snapshot views): a flush during a checkpoint opened a view of a live object and is reading it.
        /// A CopyUpdate that supersedes the object meanwhile moves it to <see cref="VIEW_FLUSHING_SUPERSEDED"/> without waiting.
        /// </summary>
        VIEW_FLUSHING,

        /// <summary>
        /// Research prototype (snapshot views): a flush is reading the view of a superseded object. When done, it keeps the
        /// (v) bytes as <see cref="SERIALIZED"/> for later flushes of the same checkpoint.
        /// </summary>
        VIEW_FLUSHING_SUPERSEDED,

        /// <summary>
        /// Serialization has been completed.
        /// </summary>
        SERIALIZED
    }
}