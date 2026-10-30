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
        /// Serialization has been completed.
        /// </summary>
        SERIALIZED
    }
}