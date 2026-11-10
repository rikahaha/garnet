// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.Threading;

namespace Tsavorite.core
{
    /// <summary>
    /// Research prototype: the heap objects that hold a snapshot view, or (v) bytes read from one, for the snapshot checkpoints in
    /// progress. A CopyUpdate opens a view only while a checkpoint is in progress, and every view and its bytes are released when
    /// the last checkpoint in progress ends or aborts.
    /// </summary>
    /// <remarks>
    /// Several stores (databases) may checkpoint at overlapping times; their objects share one list, released when the last of them
    /// ends. This keeps views open longer than needed in that case, which costs memory but not correctness.
    /// </remarks>
    internal static class CheckpointSnapshotViews
    {
        static readonly object sync = new();
        static int active;
        static List<HeapObjectBase> registered = new();

        /// <summary>Whether a snapshot checkpoint is in progress.</summary>
        public static bool Active => Volatile.Read(ref active) > 0;

        /// <summary>A snapshot checkpoint begins (PREPARE).</summary>
        public static void Begin()
        {
            lock (sync)
                active++;
        }

        /// <summary>Track <paramref name="heapObject"/> until the checkpoints in progress end.</summary>
        /// <returns>False if no checkpoint is in progress; the caller then must not keep a view or bytes.</returns>
        public static bool TryRegister(HeapObjectBase heapObject)
        {
            lock (sync)
            {
                if (active == 0)
                    return false;
                registered.Add(heapObject);
                return true;
            }
        }

        /// <summary>
        /// A snapshot checkpoint ends or aborts. Call only after its flush has completed, while sessions do not yet modify (v)
        /// objects in place. The last one to end closes unread views and releases the bytes read from views.
        /// </summary>
        public static void End()
        {
            List<HeapObjectBase> objects;
            lock (sync)
            {
                if (active == 0 || --active > 0)
                    return;
                objects = registered;
                registered = new();
            }
            foreach (var heapObject in objects)
                heapObject.EndSnapshotView();
        }
    }
}
