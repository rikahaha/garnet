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
