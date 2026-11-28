// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Garnet.test
{
    /// <summary>
    /// Research prototype (ckpt-delta): a delta read from the change list, applied to the previous view's contents, must give exactly
    /// the contents at the new view's boundary, with writes (score changes, deletes, re-inserts of the same member) before the
    /// boundary, after it, and while the delta is being read. Compared member by member, not only by count.
    /// </summary>
    [TestFixture]
    public class SortedSetMemberStoreDeltaTests
    {
        const int Keys = 2000;

        static byte[] K(int i) => Encoding.ASCII.GetBytes($"k{i}");

        static Dictionary<string, double> ReadAll(SortedSetMemberStore store, SortedSetMemberStore.View view)
        {
            var result = new Dictionary<string, double>();
            var rows = new List<(byte[] Key, double Score)>();
            while (!store.Done(view))
            {
                store.ReadInto(view, rows);
                foreach (var (k, v) in rows) result.Add(Encoding.ASCII.GetString(k), v);
            }
            return result;
        }

        static void AssertSame(Dictionary<string, double> expected, Dictionary<string, double> actual, string what)
        {
            var missing = expected.Where(kv => !actual.ContainsKey(kv.Key)).Take(5).ToList();
            var extra = actual.Where(kv => !expected.ContainsKey(kv.Key)).Take(5).ToList();
            var wrong = expected.Where(kv => actual.TryGetValue(kv.Key, out var v) && v != kv.Value).Take(5).Select(kv => (kv.Key, kv.Value, actual[kv.Key])).ToList();
            if (missing.Count + extra.Count + wrong.Count > 0)
                Assert.Fail($"{what}: expected {expected.Count}, got {actual.Count}; missing [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}] wrong [{string.Join(", ", wrong)}]");
        }

        sealed class Model
        {
            readonly SortedSetMemberStore store;
            readonly Random rng;
            public readonly Dictionary<string, double> State = new();

            public Model(SortedSetMemberStore store, int seed) => (this.store, this.rng) = (store, new Random(seed));

            public void Ops(int n, int hotKeys)
            {
                for (var i = 0; i < n; i++)
                {
                    // Hot keys get repeated changes, deletes and re-inserts of the same member.
                    var k = rng.Next(4) == 0 ? rng.Next(hotKeys) : rng.Next(Keys);
                    var key = K(k);
                    var name = $"k{k}";
                    switch (rng.Next(10))
                    {
                        case < 6:
                            var score = rng.Next(1_000_000) / 8.0;
                            store[key] = score;
                            State[name] = score;
                            break;
                        case < 9:
                            ClassicAssert.AreEqual(State.Remove(name), store.Remove(key));
                            break;
                        default:
                            // Delete and re-insert the same member right away.
                            _ = store.Remove(key);
                            store[key] = -k;
                            State[name] = -k;
                            break;
                    }
                }
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void ChangeListDeltaGivesBoundaryContents(int seed)
        {
            var store = new SortedSetMemberStore(memberMode: true, trackChanges: true);
            var model = new Model(store, seed);
            model.Ops(5000, hotKeys: 50);

            // First view: a full read becomes the base.
            var view = store.TryOpenView(owner: this);
            var expected = new Dictionary<string, double>(model.State);
            model.Ops(300, hotKeys: 50);  // after the boundary
            var persisted = ReadAll(store, view);
            AssertSame(expected, persisted, "full read");
            store.CommitChainFile(view, seq: 1, full: true);
            store.Close(view);

            var listReads = 0;
            for (var round = 0; round < 10; round++)
            {
                // Mostly small rounds (deltas); every fourth round changes more than a quarter of the set, which drops the change
                // list and the base, so the next write must be full, as the real writer decides.
                model.Ops(round % 4 == 3 ? 3000 : 200, hotKeys: 50);
                view = store.TryOpenView(owner: this);
                expected = new Dictionary<string, double>(model.State);
                model.Ops(150, hotKeys: 50);  // after the boundary, before the read starts

                if (!store.TryBeginChangeListRead(view))
                {
                    ClassicAssert.AreEqual(0, store.BaseSeq, $"round {round}: change list refused although there is a base");
                    persisted = ReadAll(store, view);
                    AssertSame(expected, persisted, $"round {round} (full)");
                    store.CommitChainFile(view, seq: round + 2, full: true);
                    store.Close(view);
                    continue;
                }
                listReads++;
                var deletes = view.DeletedKeysSinceBase.Select(Encoding.ASCII.GetString).ToList();
                var upserts = new List<(string, double)>();
                var rows = new List<(byte[] Key, double Score)>();
                var batches = 0;
                while (!store.Done(view))
                {
                    store.ReadChanged(view, rows, maxMembers: 16);
                    upserts.AddRange(rows.Select(r => (Encoding.ASCII.GetString(r.Key), r.Score)));
                    if (++batches % 3 == 0)
                        model.Ops(50, hotKeys: 50);  // while the delta is being read
                }

                // Apply the delta to the previous contents as recovery does: deletes, then upserts.
                foreach (var d in deletes) _ = persisted.Remove(d);
                foreach (var (k, v) in upserts) persisted[k] = v;
                AssertSame(expected, persisted, $"round {round}");
                ClassicAssert.AreEqual(upserts.Count, upserts.Select(u => u.Item1).Distinct().Count(), "a member was emitted twice");

                store.CommitChainFile(view, seq: round + 2, full: false);
                store.Close(view);

                // After closing, the store holds exactly the live contents (nothing retained for the view leaks).
                CollectionAssert.AreEquivalent(model.State, store.ToDictionary(kv => Encoding.ASCII.GetString(kv.Key), kv => kv.Value));
                ClassicAssert.AreEqual(model.State.Count, store.Count);
            }
            ClassicAssert.GreaterOrEqual(listReads, 6);
        }

        [Test]
        public void ClosingAnUnreadViewReleasesRetainedMembers()
        {
            var store = new SortedSetMemberStore(memberMode: true, trackChanges: true);
            var model = new Model(store, seed: 9);
            model.Ops(3000, hotKeys: 20);
            for (var i = 0; i < 5; i++)
            {
                var view = store.TryOpenView(owner: this);
                model.Ops(800, hotKeys: 20);  // captures old scores and retains deleted members
                store.Close(view);            // never read
                CollectionAssert.AreEquivalent(model.State, store.ToDictionary(kv => Encoding.ASCII.GetString(kv.Key), kv => kv.Value));
            }
            // A full read after all that still sees exactly the boundary contents.
            var last = store.TryOpenView(owner: this);
            var expected = new Dictionary<string, double>(model.State);
            model.Ops(500, hotKeys: 20);
            CollectionAssert.AreEquivalent(expected, ReadAll(store, last));
            store.Close(last);
        }
    }
}
