// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Threading;
using static Tsavorite.core.Utility;

namespace Tsavorite.core
{
    /// <summary>
    /// The base class for heap Value Objects in Tsavorite.
    /// </summary>
    public abstract class HeapObjectBase : IHeapObject
    {
        /// <inheritdoc />
        public long HeapMemorySize { get; protected set; }

        /// <summary>The current internal serialization phase of the object.</summary>
        SerializationPhase SerializationPhase
        {
            get => (SerializationPhase)serializationPhaseInt;
            set => serializationPhaseInt = (int)value;
        }

        int serializationPhaseInt;

        /// <summary>The internal serialized (v) data of the object, cached during a checkpoint.</summary>
        SerializedObjectData serializedData;

        /// <summary>Research prototype (delta checkpoints): <see cref="serializedData"/> was written for the snapshot file only (e.g. it
        /// references data stored outside the log), so a flush to any other target must not reuse it.</summary>
        bool serializedDataSnapshotOnly;

        [System.ThreadStatic]
        static bool inSnapshotFlush;

        /// <summary>Research prototype (delta checkpoints): whether the current thread is serializing objects into a snapshot checkpoint
        /// file, as opposed to the main log. Only snapshot files may hold data that refers to storage outside the log.</summary>
        public static bool InSnapshotFlush => inSnapshotFlush;

        /// <summary>Set by the allocator around object serialization for a snapshot checkpoint flush.</summary>
        internal static void SetSnapshotFlush(bool value) => inSnapshotFlush = value;

        /// <summary>
        /// Create a cloned (shallow copy) of this object
        /// </summary>
        /// <remarks>The implementation of this method should NOT copy <see cref="serializedData"/>.</remarks>
        public abstract IHeapObject Clone();

        /// <summary>
        /// Serialize to the binary writer.
        /// </summary>
        public abstract void DoSerialize(BinaryWriter writer);

        /// <summary>
        /// Transition the serialization phase of the object.
        /// </summary>
        public bool MakeTransition(SerializationPhase expectedPhase, SerializationPhase nextPhase)
            => Interlocked.CompareExchange(ref serializationPhaseInt, (int)nextPhase, (int)expectedPhase) == (int)expectedPhase;

        /// <summary>
        /// Research prototype: open an O(1) snapshot view of the object's current data, so that its (v) data can be read later
        /// while a superseding (v+1) clone keeps modifying the shared internal structures.
        /// </summary>
        /// <remarks>Called with the object held in <see cref="SerializationPhase.SERIALIZING"/>. The default supports no views.</remarks>
        /// <returns>Whether a view was opened.</returns>
        protected virtual bool TryOpenSnapshotView() => false;

        /// <summary>
        /// Research prototype: write the data of the open view as of when the view was opened, and close the view. On an exception the
        /// view must be closed as well. Outside a snapshot flush (<see cref="InSnapshotFlush"/>) it must write exactly what
        /// <see cref="DoSerialize"/> would have written; in a snapshot flush it may instead write a form only valid in the snapshot file.
        /// </summary>
        /// <returns>True if what was written is only valid in the snapshot file.</returns>
        protected virtual bool WriteSnapshotView(BinaryWriter writer) => throw new System.NotSupportedException();

        /// <summary>Research prototype: close the open view, if any, without writing it.</summary>
        protected virtual void CloseSnapshotView() { }

        /// <inheritdoc />
        public abstract void Dispose();

        /// <inheritdoc />
        public abstract void WriteType(BinaryWriter writer, bool isNull);

        /// <inheritdoc />
        public void Serialize(BinaryWriter writer)
        {
            while (true)
            {
                // This is probably called from Flush, including for checkpoints. If CopyUpdate() has already serialized the object, we will use that
                // serialized state. Otherwise, we will serialize the object directly to the writer, and not create the serialized byte[]; only
                // CopyUpdater does that, as it must ensure the object's (v1) data is not changed during the checkpoint.
                if (SerializationPhase == SerializationPhase.REST && MakeTransition(SerializationPhase.REST, SerializationPhase.SERIALIZING))
                {
                    // During a checkpoint, read a view instead of holding the object for the whole serialization, so that a
                    // CopyUpdate arriving meanwhile does not wait for this flush.
                    if (CheckpointSnapshotViews.Active && TryOpenSnapshotView())
                    {
                        SerializationPhase = SerializationPhase.VIEW_FLUSHING;
                        FlushFromView(writer);
                        return;
                    }

                    if (CheckpointPreSerializer.FlushCapture)
                    {
                        SerializeViaCapture(writer);
                        return;
                    }

                    // Directly serialize to wire, do not cache serialized state. Restore REST even if DoSerialize
                    // throws: leaving the object in SERIALIZING would make every later Serialize() and
                    // CacheSerializedObjectData() spin on it forever, wedging the checkpoint pipeline.
                    var traceStart = CheckpointObjectTrace.Enabled ? CheckpointObjectTrace.Now() : 0;
                    var traceGc = CheckpointObjectTrace.Enabled ? System.GC.GetTotalPauseDuration() : default;
                    try
                    {
                        WriteType(writer, isNull: false);
                        DoSerialize(writer);
                    }
                    finally
                    {
                        SerializationPhase = SerializationPhase.REST;
                    }
                    if (CheckpointObjectTrace.Enabled)
                    {
                        var us = CheckpointObjectTrace.Us(traceStart, CheckpointObjectTrace.Now());
                        CheckpointObjectTrace.Record("flush_serialize", this, us, 0, us, 0, traceGc, 0);
                    }
                    return;
                }

                // If we are here, SerializationPhase is one of the .SERIALIZ* states. This means that one of the following is true:
                // - Another thread is currently serializing this object (e.g. checkpoint and eviction)
                // - CopyUpdate() is serializing this object
                // - Serialization is complete. If the serialized data is null (or released), it means the checkpoint has completed and cleared it
                //   and the object has been superseded in the database so is no longer reachable, so we can write a null indicator.

                if (SerializationPhase == SerializationPhase.SERIALIZED)
                {
                    // If serialized state is cached, use that. Data released under us was cleared after the checkpoint, the same as null.
                    // Bytes valid only in a snapshot file are not written elsewhere: this object is superseded, so its record is not reachable,
                    // and the checkpoint recovers it from the snapshot file; write null as after the checkpoint.
                    var _serialized = serializedData;
                    if (serializedDataSnapshotOnly && !inSnapshotFlush)
                        _serialized = null;
                    if (_serialized != null && _serialized.TryAcquire())
                    {
                        try
                        {
                            WriteType(writer, isNull: false);
                            _serialized.WriteTo(writer);
                        }
                        finally
                        {
                            _serialized.Release();
                        }
                    }
                    else
                    {
                        // Write null object to stream
                        WriteType(writer, isNull: true);
                    }
                    return;
                }

                // A CopyUpdate opened a view of the (v) data and no flush has read it yet: this flush reads it.
                if (SerializationPhase == SerializationPhase.VIEW_OPEN && MakeTransition(SerializationPhase.VIEW_OPEN, SerializationPhase.VIEW_FLUSHING_SUPERSEDED))
                {
                    FlushFromView(writer);
                    return;
                }

                // Pre-serialized for the ongoing checkpoint. The bytes are cleared only after leaving PRESERIALIZED,
                // so if they are gone the phase has moved on; loop to observe it.
                if (SerializationPhase == SerializationPhase.PRESERIALIZED)
                {
                    var _serialized = serializedData;
                    if (_serialized != null && _serialized.TryAcquire())
                    {
                        try
                        {
                            WriteType(writer, isNull: false);
                            _serialized.WriteTo(writer);
                        }
                        finally
                        {
                            _serialized.Release();
                        }
                        return;
                    }
                }

                Thread.Yield();
            }
        }

        /// <summary>
        /// Research prototype (TSAVORITE_FLUSH_CAPTURE=1): serialize into memory while holding SERIALIZING, release the object, and only
        /// then write to <paramref name="writer"/>, so that a CopyUpdate does not wait for device I/O. During a checkpoint the capture is
        /// also published as PRESERIALIZED, so that the CopyUpdate reuses it instead of serializing the object again.
        /// </summary>
        /// <remarks>The caller has moved the object from REST to SERIALIZING.</remarks>
        void SerializeViaCapture(BinaryWriter writer)
        {
            var traceStart = CheckpointObjectTrace.Enabled ? CheckpointObjectTrace.Now() : 0;
            var traceGc = CheckpointObjectTrace.Enabled ? System.GC.GetTotalPauseDuration() : default;

            SerializedObjectData data;
            try
            {
                data = SerializedObjectData.Capture(this);
            }
            catch
            {
                SerializationPhase = SerializationPhase.REST;
                throw;
            }

            // Hold a reader reference across the write: once published, the data's owner is the object, and a reset may release it.
            _ = data.TryAcquire();
            var published = CheckpointPreSerializer.Current?.TryPublishFlushCapture(this, data) ?? false;
            if (!published)
                SerializationPhase = SerializationPhase.REST;
            var traceReleased = CheckpointObjectTrace.Enabled ? CheckpointObjectTrace.Now() : 0;

            try
            {
                WriteType(writer, isNull: false);
                data.WriteTo(writer);
            }
            finally
            {
                data.Release();
                if (!published)
                    data.Release();
            }

            if (CheckpointObjectTrace.Enabled)
            {
                var end = CheckpointObjectTrace.Now();
                CheckpointObjectTrace.Record(published ? "flush_capture_shared" : "flush_capture", this,
                    totalUs: CheckpointObjectTrace.Us(traceStart, end), waitUs: 0,
                    serializeUs: CheckpointObjectTrace.Us(traceStart, traceReleased),
                    writeUs: CheckpointObjectTrace.Us(traceReleased, end), traceGc, data.Length);
            }
        }

        /// <summary>
        /// Research prototype (snapshot views): read the open view into memory and write it to <paramref name="writer"/>. The view is read
        /// in batches that let writers of the shared structures through, so a CopyUpdate never waits for this. If the object has been
        /// superseded by the end, the bytes are kept as <see cref="SerializationPhase.SERIALIZED"/> for later flushes of the same
        /// checkpoint (the main log and the snapshot file may both flush it); otherwise the object returns to REST.
        /// </summary>
        /// <remarks>The caller has moved the object to VIEW_FLUSHING or VIEW_FLUSHING_SUPERSEDED.</remarks>
        void FlushFromView(BinaryWriter writer)
        {
            var traceStart = CheckpointObjectTrace.Enabled ? CheckpointObjectTrace.Now() : 0;
            var traceGc = CheckpointObjectTrace.Enabled ? System.GC.GetTotalPauseDuration() : default;

            SerializedObjectData data;
            var snapshotOnly = false;
            try
            {
                data = SerializedObjectData.Capture(w => snapshotOnly = WriteSnapshotView(w));
            }
            catch
            {
                try { CloseSnapshotView(); } catch { /* report the original exception */ }
                // A live object returns to REST. A superseded object has lost its (v) data; it can only write null from now on,
                // and this exception fails the flush, so the checkpoint does not complete with it.
                if (!MakeTransition(SerializationPhase.VIEW_FLUSHING, SerializationPhase.REST))
                    _ = Interlocked.Exchange(ref serializationPhaseInt, (int)SerializationPhase.SERIALIZED);
                throw;
            }

            // The view is closed now. Hold a reader reference across the write: once published, the object owns the data.
            _ = data.TryAcquire();
            var kept = !MakeTransition(SerializationPhase.VIEW_FLUSHING, SerializationPhase.REST);
            if (kept)
            {
                serializedDataSnapshotOnly = snapshotOnly;
                serializedData = data;
                _ = Interlocked.Exchange(ref serializationPhaseInt, (int)SerializationPhase.SERIALIZED);
                if (!CheckpointSnapshotViews.TryRegister(this))
                    Interlocked.Exchange(ref serializedData, null)?.Release();  // the checkpoint has ended: nobody needs them
            }
            var traceRead = CheckpointObjectTrace.Enabled ? CheckpointObjectTrace.Now() : 0;

            try
            {
                WriteType(writer, isNull: false);
                data.WriteTo(writer);
            }
            finally
            {
                data.Release();
                if (!kept)
                    data.Release();
            }

            if (CheckpointObjectTrace.Enabled)
            {
                var end = CheckpointObjectTrace.Now();
                CheckpointObjectTrace.Record(kept ? "flush_view_kept" : "flush_view", this,
                    totalUs: CheckpointObjectTrace.Us(traceStart, end), waitUs: 0,
                    serializeUs: CheckpointObjectTrace.Us(traceStart, traceRead),
                    writeUs: CheckpointObjectTrace.Us(traceRead, end), traceGc, data.Length);
            }
        }

        /// <summary>
        /// Research prototype (snapshot views): the checkpoints this object was registered with have ended. Close a view that no flush
        /// read (its (v) data was not needed, e.g. the record was below the snapshot's flush range), and release bytes read from one.
        /// A flush still reading a view releases its own bytes when it sees no checkpoint in progress.
        /// </summary>
        internal void EndSnapshotView()
        {
            while (true)
            {
                var phase = SerializationPhase;
                if (phase == SerializationPhase.VIEW_OPEN)
                {
                    if (!MakeTransition(SerializationPhase.VIEW_OPEN, SerializationPhase.SERIALIZING))
                        continue;
                    try
                    {
                        CloseSnapshotView();
                    }
                    finally
                    {
                        // Superseded, as for a CopyUpdate's bytes after ClearSerializedObjectData: later flushes write null.
                        SerializationPhase = SerializationPhase.SERIALIZED;
                    }
                    return;
                }
                if (phase == SerializationPhase.SERIALIZED)
                    Interlocked.Exchange(ref serializedData, null)?.Release();
                return;
            }
        }

        /// <summary>
        /// Publish <paramref name="data"/> as this object's pre-serialized (v) data. The caller holds the object in SERIALIZING and is
        /// responsible for returning it to REST via <see cref="ResetPreSerialized"/> before the checkpoint ends.
        /// </summary>
        internal void PublishPreSerialized(SerializedObjectData data)
        {
            serializedDataSnapshotOnly = false;
            serializedData = data;
            SerializationPhase = SerializationPhase.PRESERIALIZED;
        }

        /// <inheritdoc />
        public void CacheSerializedObjectData(ref LogRecord dstLogRecord, ref RMWInfo rmwInfo, bool srcIsOnMemoryLog)
        {
            // We'll want to clone the source object to the destination log record so PostCopyUpdater can modify it.
            // Note that this does a shallow copy of the object's internal structures (e.g. List<>), which means subsequent modifications of newValue
            // in the (v+1) version of the record will modify data seen from the 'this' in the (v) record. Normally this is OK because the (v) version
            // of the record is not reachable once the (v+1) version is inserted, but if a checkpoint is ongoing, the (v) version is part of that.
            // (If this was an Overflow instead of an Object, then PostCopyUpdater will follow the normal RCU logic, creating a new ValueSpan which will
            // probably (but not necessarily) be another Overflow.)
            Debug.Assert(dstLogRecord.DataHeader.ValueIsObject, $"{GetCurrentMethodName()} must be called for non-object {nameof(dstLogRecord)}.");

            // CopyUpdater may have already set the ValueObject so only set if not already set.
            if (!dstLogRecord.ValueObjectIsSet)
                _ = dstLogRecord.TrySetValueObject(Clone());

            // For pending-IO (DiskLogRecord) sources, skip the checkpoint-serialization state machine:
            //   - the (v) data is already persisted on disk from a prior flush, so there's nothing to capture;
            //   - 'this' is an ephemeral deserialized object that will be disposed up the pending chain, so
            //     driving its SerializationPhase and allocating serialized data would be pure waste;
            //   - ClearSourceValueObject is ignored by InternalRMW for non-memory sources anyway.
            if (!srcIsOnMemoryLog)
                return;

            // If we are not currently taking a checkpoint, we can delete the old version
            // since the new version of the object is already created.
            if (!dstLogRecord.Info.IsInNewVersion)
            {
                // Wait for any concurrent ongoing serialization of oldValue to complete
                while (true)
                {
                    if (SerializationPhase == (int)SerializationPhase.REST && MakeTransition(SerializationPhase.REST, SerializationPhase.SERIALIZED))
                        break;

                    if ((int)SerializationPhase >= (int)SerializationPhase.SERIALIZED)
                        break;

                    // Pre-serialized by a checkpoint whose flush has completed; the object is now superseded.
                    if (SerializationPhase == SerializationPhase.PRESERIALIZED && MakeTransition(SerializationPhase.PRESERIALIZED, SerializationPhase.SERIALIZED))
                    {
                        Interlocked.Exchange(ref serializedData, null)?.Release();
                        break;
                    }

                    _ = Thread.Yield();
                }
                rmwInfo.ClearSourceValueObject = true;
                return;
            }

            // Create a serialized version for checkpoint version (v). This is only done for CopyUpdate during a checkpoint, to preserve the (v) data
            // of the object during a checkpoint while the (v+1) version of the record may modify the shallow-copied internal structures.
            var traceStart = CheckpointObjectTrace.Enabled ? CheckpointObjectTrace.Now() : 0;
            var traceGc = CheckpointObjectTrace.Enabled ? System.GC.GetTotalPauseDuration() : default;
            long traceSerializeStart = 0, traceSerializeEnd = 0;
            while (true)
            {
                if (SerializationPhase == (int)SerializationPhase.REST && MakeTransition(SerializationPhase.REST, SerializationPhase.SERIALIZING))
                {
                    // Snapshot views: preserve the (v) data with an O(1) view instead of serializing the whole object here, under
                    // the record lock, on the request thread. The view is registered so that the end of the checkpoint closes it.
                    var viewOpened = false;
                    try
                    {
                        if (CheckpointSnapshotViews.Active && TryOpenSnapshotView())
                        {
                            if (CheckpointSnapshotViews.TryRegister(this))
                                viewOpened = true;
                            else
                                CloseSnapshotView();
                        }
                    }
                    catch
                    {
                        SerializationPhase = SerializationPhase.REST;
                        throw;
                    }
                    if (viewOpened)
                    {
                        SerializationPhase = SerializationPhase.VIEW_OPEN;
                        if (CheckpointObjectTrace.Enabled)
                        {
                            var us = CheckpointObjectTrace.Us(traceStart, CheckpointObjectTrace.Now());
                            CheckpointObjectTrace.Record("cou_view", this, us, 0, us, 0, traceGc, 0);
                        }
                        return;
                    }

                    try
                    {
                        if (CheckpointObjectTrace.Enabled)
                            traceSerializeStart = CheckpointObjectTrace.Now();
                        serializedDataSnapshotOnly = false;
                        serializedData = SerializedObjectData.Capture(this);
                        if (CheckpointObjectTrace.Enabled)
                            traceSerializeEnd = CheckpointObjectTrace.Now();
                    }
                    catch
                    {
                        // Publish no partial capture, and do not strand the object in SERIALIZING.
                        serializedData = null;
                        SerializationPhase = SerializationPhase.REST;
                        throw;
                    }

                    SerializationPhase = SerializationPhase.SERIALIZED;    // This is the only place .SERIALIZED is set
                    break;
                }

                // If we're here, serializationState is one of the .SERIALIZ* states. CopyUpdate has a lock on the tag chain, so no other thread will
                // be running CopyUpdate. Therefore there are two possibilities:
                // 1. CopyUpdate has been called before and the state is .SERIALIZED and '_serialized' is created. We're done.
                // 2. Serialize() is running (likely in a Flush()) and the state is .SERIALIZING. We will Yield and loop to wait for it to finish.
                if (SerializationPhase >= SerializationPhase.SERIALIZED)
                    break;

                // Snapshot views: a flush is reading a view of this object. Mark it superseded, so that the flush keeps the (v) bytes,
                // and go on without waiting for the flush. (VIEW_OPEN and VIEW_FLUSHING_SUPERSEDED mean it was already superseded.)
                if (SerializationPhase == SerializationPhase.VIEW_FLUSHING
                        && MakeTransition(SerializationPhase.VIEW_FLUSHING, SerializationPhase.VIEW_FLUSHING_SUPERSEDED))
                    break;
                if (SerializationPhase is SerializationPhase.VIEW_OPEN or SerializationPhase.VIEW_FLUSHING_SUPERSEDED)
                    break;

                // 3. The checkpoint pre-serialized the (v) data; adopt those bytes as this CopyUpdate's capture.
                if (SerializationPhase == SerializationPhase.PRESERIALIZED && MakeTransition(SerializationPhase.PRESERIALIZED, SerializationPhase.SERIALIZED))
                    break;

                _ = Thread.Yield();
            }

            if (CheckpointObjectTrace.Enabled)
            {
                var end = CheckpointObjectTrace.Now();
                var serialized = traceSerializeStart != 0;
                CheckpointObjectTrace.Record(serialized ? "cou_serialize" : "cou_reuse", this,
                    totalUs: CheckpointObjectTrace.Us(traceStart, end),
                    waitUs: CheckpointObjectTrace.Us(traceStart, serialized ? traceSerializeStart : end),
                    serializeUs: serialized ? CheckpointObjectTrace.Us(traceSerializeStart, traceSerializeEnd) : 0,
                    writeUs: 0,
                    traceGc, serializedData?.Length ?? 0);
            }
        }

        /// <summary>
        /// Serialize the (v) data of a live object ahead of a CopyUpdate during a checkpoint, so that the CopyUpdate (on a request
        /// thread) and the checkpoint flush can both reuse the bytes instead of serializing the object themselves.
        /// </summary>
        /// <remarks>
        /// Only call this while no thread can modify (v) objects in place: after all (v) sessions and transactions have drained,
        /// and before the checkpoint returns to REST. <see cref="ResetPreSerialized"/> must be called before that.
        /// </remarks>
        /// <returns>Whether the object was pre-serialized; false if it was already being (or had been) serialized.</returns>
        internal bool TryPreSerialize()
        {
            if (SerializationPhase != SerializationPhase.REST || !MakeTransition(SerializationPhase.REST, SerializationPhase.SERIALIZING))
                return false;

            var traceStart = CheckpointObjectTrace.Enabled ? CheckpointObjectTrace.Now() : 0;
            var traceGc = CheckpointObjectTrace.Enabled ? System.GC.GetTotalPauseDuration() : default;
            try
            {
                serializedDataSnapshotOnly = false;
                serializedData = SerializedObjectData.Capture(this);
            }
            catch
            {
                serializedData = null;
                SerializationPhase = SerializationPhase.REST;
                return false;
            }
            SerializationPhase = SerializationPhase.PRESERIALIZED;

            if (CheckpointObjectTrace.Enabled)
            {
                var us = CheckpointObjectTrace.Us(traceStart, CheckpointObjectTrace.Now());
                CheckpointObjectTrace.Record("pre_serialize", this, us, 0, us, 0, traceGc, serializedData.Length);
            }
            return true;
        }

        /// <summary>
        /// Return a pre-serialized object that no CopyUpdate superseded to <see cref="SerializationPhase.REST"/>, releasing its bytes.
        /// Objects a CopyUpdate adopted are <see cref="SerializationPhase.SERIALIZED"/> and are left to <see cref="ClearSerializedObjectData"/>.
        /// </summary>
        internal void ResetPreSerialized()
        {
            // Hold SERIALIZING while clearing so that no reader sees PRESERIALIZED without bytes, or REST with stale ones.
            if (SerializationPhase != SerializationPhase.PRESERIALIZED || !MakeTransition(SerializationPhase.PRESERIALIZED, SerializationPhase.SERIALIZING))
                return;
            var data = serializedData;
            serializedData = null;
            SerializationPhase = SerializationPhase.REST;
            data?.Release();
        }

        /// <inheritdoc />
        public void ClearSerializedObjectData()
        {
            // Release the cached (v) bytes so they can be GC'd, but deliberately leave the phase terminal.
            //
            // Bytes are only ever cached by CacheSerializedObjectData for a source object that a CopyUpdate
            // has just superseded, and Clone() is a shallow copy: the (v+1) record that replaced this one
            // shares this object's internal collections and keeps mutating them. Returning to REST would let
            // a later checkpoint take the direct-serialize path and enumerate those still-shared, still-live
            // collections with no synchronization against the writer.
            //
            // SERIALIZED with null bytes is the state Serialize() already documents as "superseded after
            // checkpoint completion": it writes a null indicator. That is safe because the superseding record
            // always sits at a higher address and carries the live data.
            //
            // A pre-serialized object is still live, so it must go back to REST rather than keep a terminal phase.
            if (SerializationPhase == SerializationPhase.PRESERIALIZED)
            {
                ResetPreSerialized();
                return;
            }
            Interlocked.Exchange(ref serializedData, null)?.Release();
        }
    }
}