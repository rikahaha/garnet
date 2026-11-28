// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Garnet.common;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    /// <summary>Research prototype (ckpt-delta): chain files of the sorted set side store.</summary>
    [TestFixture]
    public class SortedSetSideStoreTests
    {
        string dir;
        const long Uid = 0x1234;

        [SetUp]
        public void Setup()
        {
            dir = Path.Combine(TestUtils.MethodTestDir, "sortedset-members");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            SortedSetSideStore.Configure(dir, allowWrite: true);
        }

        [TearDown]
        public void TearDown()
        {
            SortedSetSideStore.Configure(null, allowWrite: false);
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir);
        }

        static byte[] K(string s) => Encoding.ASCII.GetBytes(s);

        static void Write(long uid, long seq, bool full, long baseSeq, long chainStart, (string Key, double Score)[] upserts, string[] deletes = null)
        {
            using var w = SortedSetSideStore.CreateWriter(uid, seq, full, baseSeq, chainStart);
            foreach (var d in deletes ?? [])
                w.Delete(K(d));
            foreach (var (key, score) in upserts)
                w.Upsert(K(key), score);
            w.Commit();
        }

        static Dictionary<string, double> Load(long uid, long seq)
            => SortedSetSideStore.LoadChain(uid, seq).ToDictionary(kv => Encoding.ASCII.GetString(kv.Key), kv => kv.Value);

        void WriteChain()
        {
            Write(Uid, 1, full: true, 0, 1, [("a", 1), ("b", 2), ("c", 3)]);
            Write(Uid, 2, full: false, 1, 1, [("b", 5), ("d", 4)], ["a"]);
            // Delete and add again within one delta: present afterwards. Delete of a member that is not there: no effect.
            Write(Uid, 3, full: false, 2, 1, [("a", 9), ("c", 7)], ["d", "c", "zz"]);
        }

        [Test]
        public void ChainAppliesDeltasInOrder()
        {
            WriteChain();
            CollectionAssert.AreEquivalent(new Dictionary<string, double> { ["a"] = 1, ["b"] = 2, ["c"] = 3 }, Load(Uid, 1));
            CollectionAssert.AreEquivalent(new Dictionary<string, double> { ["b"] = 5, ["c"] = 3, ["d"] = 4 }, Load(Uid, 2));
            CollectionAssert.AreEquivalent(new Dictionary<string, double> { ["a"] = 9, ["b"] = 5, ["c"] = 7 }, Load(Uid, 3));

            // A new full file starts a new chain.
            Write(Uid, 4, full: true, 0, 4, [("x", 1)]);
            CollectionAssert.AreEquivalent(new Dictionary<string, double> { ["x"] = 1 }, Load(Uid, 4));
        }

        [Test]
        public void MissingFileThrows()
        {
            WriteChain();
            File.Delete(SortedSetSideStore.PathFor(Uid, 2));
            _ = Assert.Throws<GarnetException>(() => Load(Uid, 3));
            _ = Assert.Throws<GarnetException>(() => Load(Uid, 7));
        }

        [Test]
        public void CorruptFileThrows()
        {
            WriteChain();
            var path = SortedSetSideStore.PathFor(Uid, 2);
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 0x40;
            File.WriteAllBytes(path, bytes);
            _ = Assert.Throws<GarnetException>(() => Load(Uid, 3));
        }

        [Test]
        public void TruncatedFileThrows()
        {
            WriteChain();
            var path = SortedSetSideStore.PathFor(Uid, 3);
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^5]);
            _ = Assert.Throws<GarnetException>(() => Load(Uid, 3));
        }

        [Test]
        public void FileOfAnotherStoreThrows()
        {
            WriteChain();
            const long other = 0x9999;
            _ = Directory.CreateDirectory(Path.GetDirectoryName(SortedSetSideStore.PathFor(other, 1)));
            File.Copy(SortedSetSideStore.PathFor(Uid, 1), SortedSetSideStore.PathFor(other, 1));
            _ = Assert.Throws<GarnetException>(() => Load(other, 1));
        }

        [Test]
        public void UncommittedFileIsDeleted()
        {
            using (var w = SortedSetSideStore.CreateWriter(Uid, 1, full: true, 0, 1))
                w.Upsert(K("a"), 1);
            ClassicAssert.IsFalse(File.Exists(SortedSetSideStore.PathFor(Uid, 1)));
        }

        [Test]
        public void ExistingChainFileIsNeverOverwritten()
        {
            Write(Uid, 1, full: true, 0, 1, [("a", 1)]);
            _ = Assert.Throws<IOException>(() => SortedSetSideStore.CreateWriter(Uid, 1, full: true, 0, 1));
            CollectionAssert.AreEquivalent(new Dictionary<string, double> { ["a"] = 1 }, Load(Uid, 1));
        }

        /// <summary>
        /// The last completed checkpoint's chain stays recoverable when the base is dropped and a new chain is being written but not
        /// committed (e.g. a crash): the new chain takes new file numbers instead of reusing 1, 2, ... Compared member by member.
        /// </summary>
        [Test]
        public void DroppedBaseDoesNotOverwriteTheLastCompletedChain()
        {
            var store = new SortedSetMemberStore(memberMode: true, trackChanges: true);
            var model = new SortedSetMemberStoreDeltaTests.Model(store, seed: 11);
            var uid = store.Uid;
            model.Ops(3000, hotKeys: 30);

            // Checkpoint A, part 1: full file.
            var view = store.TryOpenView(owner: this);
            var seq1 = store.AllocateSeq();
            WriteFull(uid, seq1, SortedSetMemberStoreDeltaTests.ReadAll(store, view));
            store.CommitChainFile(view, seq1, full: true);
            store.Close(view);

            // Checkpoint A, part 2: a delta from the change list. Its boundary contents are what A must recover.
            model.Ops(200, hotKeys: 30);
            view = store.TryOpenView(owner: this);
            var expected = new Dictionary<string, double>(model.State);
            ClassicAssert.IsTrue(store.BaseIntact(view));
            ClassicAssert.IsTrue(store.TryBeginChangeListRead(view));
            var seq2 = store.AllocateSeq();
            using (var w = SortedSetSideStore.CreateWriter(uid, seq2, full: false, store.BaseSeq, store.ChainStart))
            {
                var keys = new List<byte[]>();
                while (true) { store.ReadDeletedKeys(view, keys); if (keys.Count == 0) break; foreach (var k in keys) w.Delete(k); }
                var rows = new List<(byte[] Key, double Score)>();
                while (!store.Done(view)) { store.ReadChanged(view, rows); foreach (var (k, v) in rows) w.Upsert(k, v); }
                w.Commit();
            }
            store.CommitChainFile(view, seq2, full: false);
            store.Close(view);
            CollectionAssert.AreEquivalent(expected, Load(uid, seq2));

            // Checkpoint B writes the set inline (high change): the chain is reset, the base dropped.
            model.Ops(3000, hotKeys: 30);
            view = store.TryOpenView(owner: this);
            _ = SortedSetMemberStoreDeltaTests.ReadAll(store, view);
            store.ResetChain(view);
            store.Close(view);
            ClassicAssert.AreEqual(0, store.BaseSeq);

            // Checkpoint C starts a new chain with a full file and crashes before committing it. Its number must be new.
            model.Ops(100, hotKeys: 30);
            view = store.TryOpenView(owner: this);
            ClassicAssert.IsFalse(store.BaseIntact(view));
            var seq3 = store.AllocateSeq();
            ClassicAssert.Greater(seq3, seq2);
            var current = SortedSetMemberStoreDeltaTests.ReadAll(store, view);
            using (var w = SortedSetSideStore.CreateWriter(uid, seq3, full: true, 0, seq3))
            {
                foreach (var (k, v) in current.Take(current.Count / 2))
                    w.Upsert(Encoding.ASCII.GetBytes(k), v);
                // no Commit: the crash
            }
            store.Close(view);

            // Recovering A still gives exactly A's contents.
            CollectionAssert.AreEquivalent(expected, Load(uid, seq2));
        }

        static void WriteFull(long uid, long seq, Dictionary<string, double> members)
        {
            using var w = SortedSetSideStore.CreateWriter(uid, seq, full: true, 0, seq);
            foreach (var (k, v) in members)
                w.Upsert(K(k), v);
            w.Commit();
        }

        [Test]
        public void CompletedCheckpointKeepsOnlyReferencedChains()
        {
            WriteChain();
            Write(Uid, 4, full: true, 0, 4, [("x", 1)]);
            Write(Uid, 5, full: false, 4, 4, [("y", 2)]);
            const long gone = 0x5555;
            Write(gone, 1, full: true, 0, 1, [("g", 1)]);

            SortedSetSideStore.RecordCheckpointReference(Uid, 4);
            SortedSetSideStore.OnCheckpointCompleted();
            CollectionAssert.AreEqual(new[] { (Uid, 4L), (Uid, 5L) }, SortedSetSideStore.ListFiles());
            CollectionAssert.AreEquivalent(new Dictionary<string, double> { ["x"] = 1, ["y"] = 2 }, Load(Uid, 5));

            // Recovery keeps exactly what the recovered stubs loaded.
            Write(gone, 1, full: true, 0, 1, [("g", 1)]);
            _ = SortedSetSideStore.LoadChain(Uid, 5);
            SortedSetSideStore.OnRecoveryCompleted();
            CollectionAssert.AreEqual(new[] { (Uid, 4L), (Uid, 5L) }, SortedSetSideStore.ListFiles());

            // A checkpoint with no stubs leaves nothing.
            SortedSetSideStore.OnCheckpointCompleted();
            CollectionAssert.IsEmpty(SortedSetSideStore.ListFiles());
        }
    }
}
