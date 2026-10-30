// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;

namespace Tsavorite.core
{
    /// <summary>
    /// A Snapshot persists a version by making a copy for every entry of that version separate from the log. It is
    /// slower and more complex than a foldover, but more space-efficient on the log, and retains in-place
    /// update performance as it does not advance the readonly marker unnecessarily.
    /// </summary>
    internal sealed class SnapshotCheckpointSMTask<TStoreFunctions, TAllocator> : HybridLogCheckpointSMTask<TStoreFunctions, TAllocator>
        where TStoreFunctions : IStoreFunctions
        where TAllocator : IAllocator<TStoreFunctions>
    {
        CheckpointPreSerializer preSerializer;

        public SnapshotCheckpointSMTask(TsavoriteKV<TStoreFunctions, TAllocator> store, Guid guid)
            : base(store, guid)
        {
        }

        /// <summary>
        /// Start pre-serializing large (v) objects. The phase after IN_PROGRESS is the first point at which no (v) session or
        /// transaction can still modify (v) objects in place: the state machine leaves IN_PROGRESS only once they have drained.
        /// </summary>
        void StartPreSerializer()
        {
            if (preSerializer is not null || !CheckpointPreSerializer.Enabled)
                return;
            var hlogBase = store.hlogBase;
            var beginAddress = hlogBase.HeadAddress;
            var endAddress = hlogBase.GetTailAddress();
            preSerializer = CheckpointPreSerializer.Start(candidates =>
                hlogBase.CollectPreSerializeCandidates(beginAddress, endAddress, CheckpointPreSerializer.MinBytes, candidates));
        }

        void StopPreSerializer()
        {
            preSerializer?.StopAndReset();
            preSerializer = null;
        }

        /// <inheritdoc />
        public override void OnAbort(StateMachineDriver stateMachineDriver, Exception exception)
        {
            StopPreSerializer();
            base.OnAbort(stateMachineDriver, exception);
        }

        /// <inheritdoc />
        public override void GlobalBeforeEnteringState(SystemState next, StateMachineDriver stateMachineDriver)
        {
            switch (next.Phase)
            {
                case Phase.WAIT_INDEX_CHECKPOINT:
                    StartPreSerializer();
                    base.GlobalBeforeEnteringState(next, stateMachineDriver);
                    break;

                case Phase.PREPARE:
                    store._hybridLogCheckpointToken = guid;
                    store.InitializeHybridLogCheckpoint(store._hybridLogCheckpointToken, next.Version);
                    store._hybridLogCheckpoint.info.useSnapshotFile = 1;
                    ObjectLog_OnPrepare();
                    base.GlobalBeforeEnteringState(next, stateMachineDriver);
                    break;

                case Phase.WAIT_FLUSH:
                    StartPreSerializer();
                    base.GlobalBeforeEnteringState(next, stateMachineDriver);

                    store._hybridLogCheckpoint.info.snapshotFinalLogicalAddress = store._hybridLogCheckpoint.info.finalLogicalAddress;
                    store._hybridLogCheckpoint.snapshotFileDevice = store.checkpointManager.GetSnapshotLogDevice(store._hybridLogCheckpointToken);
                    store._hybridLogCheckpoint.snapshotFileObjectLogDevice = store.checkpointManager.GetSnapshotObjectLogDevice(store._hybridLogCheckpointToken);
                    store._hybridLogCheckpoint.snapshotFileDevice.Initialize(store.hlogBase.GetMainLogSegmentSize());
                    store._hybridLogCheckpoint.snapshotFileObjectLogDevice.Initialize(store.hlogBase.GetObjectLogSegmentSize());

                    // If we are using a NullDevice then storage tier is not enabled and FlushedUntilAddress may be ReadOnlyAddress; get all records in memory.
                    store._hybridLogCheckpoint.info.snapshotStartFlushedLogicalAddress = store.hlogBase.IsNullDevice ? store.hlogBase.HeadAddress : store.hlogBase.FlushedUntilAddress;

                    if (store._hybridLogCheckpoint.info.finalLogicalAddress <= store._hybridLogCheckpoint.info.snapshotStartFlushedLogicalAddress)
                    {
                        // Nothing to flush because the flushed region already contains everything up to finalLogicalAddress.
                        break;
                    }

                    var startPage = store.hlogBase.GetPage(store._hybridLogCheckpoint.info.snapshotStartFlushedLogicalAddress);
                    var endPage = store.hlogBase.GetPage(store._hybridLogCheckpoint.info.finalLogicalAddress);
                    if (store._hybridLogCheckpoint.info.finalLogicalAddress > store.hlogBase.GetLogicalAddressOfStartOfPage(endPage))
                        endPage++;

                    // We are writing pages outside epoch protection, so callee should be able to
                    // handle corrupted or unexpected concurrent page changes during the flush, e.g., by
                    // resuming epoch protection if necessary. Correctness is not affected as we will
                    // only read safe pages during recovery.
                    store.hlogBase.AsyncFlushPagesForSnapshot(ObjectLog_OnWaitFlush(),
                        startPage, endPage,
                        startLogicalAddress: store._hybridLogCheckpoint.info.snapshotStartFlushedLogicalAddress,
                        endLogicalAddress: store._hybridLogCheckpoint.info.finalLogicalAddress,
                        fuzzyStartLogicalAddress: store._hybridLogCheckpoint.info.startLogicalAddress,
                        logDevice: store._hybridLogCheckpoint.snapshotFileDevice,
                        objectLogDevice: store._hybridLogCheckpoint.snapshotFileObjectLogDevice,
                        out store._hybridLogCheckpoint.flushedTask,
                        store.ThrottleCheckpointFlushDelayMs);
                    if (store._hybridLogCheckpoint.flushedTask != null)
                        stateMachineDriver.AddToWaitingList(store._hybridLogCheckpoint.flushedTask, StateMachineTaskType.SnapshotCheckpointSMTaskHybridLogFlushed);
                    break;

                case Phase.PERSISTENCE_CALLBACK:
                    // The snapshot flush has completed, and sessions are still in WAIT_FLUSH, where (v) objects are not modified in place.
                    // Return untouched pre-serialized objects to REST before PERSISTENCE_CALLBACK, where in-place updates resume.
                    StopPreSerializer();

                    // Set actual FlushedUntil to the latest possible data in main log that is on disk
                    // If we are using a NullDevice then storage tier is not enabled and FlushedUntilAddress may be ReadOnlyAddress; get all records in memory.
                    ObjectLog_OnPersistenceCallback();
                    store._hybridLogCheckpoint.info.flushedLogicalAddress = store.hlogBase.IsNullDevice ? store.hlogBase.HeadAddress : store.hlogBase.FlushedUntilAddress;
                    base.GlobalBeforeEnteringState(next, stateMachineDriver);
                    store._hybridLogCheckpoint.snapshotFileDevice?.Dispose();
                    store._hybridLogCheckpoint.snapshotFileDevice = null;
                    store._hybridLogCheckpoint.snapshotFileObjectLogDevice?.Dispose();
                    store._hybridLogCheckpoint.snapshotFileObjectLogDevice = null;
                    break;

                default:
                    base.GlobalBeforeEnteringState(next, stateMachineDriver);
                    break;
            }
        }
    }
}