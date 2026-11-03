using Purr2D2.Slicing;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var a = new object(); var b = new object(); var c = new object();
var vessel1 = new object(); var vessel2 = new object();
var queue = new VesselIntentQueue<object, object, int>(3);
static VesselIntentQueue<object, object, int>.SourceState Current(VesselIntentQueue<object, object, int>.Intent i)
    => new(i.Source, i.Owner, true);
VesselContactEvidence contact = new(new(1, 2, 3), new(0, 1, 0), new(1, 0, 0));
Check(queue.Enqueue(a, vessel1, 7, contact), "first intent admitted");
Check(!queue.Enqueue(a, vessel1, 7, contact), "structural source deduplicates");
Check(queue.Enqueue(b, vessel1, 7, contact) && queue.Enqueue(c, vessel2, 8, contact), "other sources admitted");
Check(!queue.Enqueue(new object(), vessel2, 8, contact), "capacity bounded");
Check(queue.TryAcquire(Current, out var first) == VesselIntentQueue<object, object, int>.AcquireResult.Acquired
    && ReferenceEquals(first!.Source, a) && first.Evidence == contact, "first lease and source-local evidence");
Check(queue.TryAcquire(Current, out var parallel) == VesselIntentQueue<object, object, int>.AcquireResult.Acquired
    && ReferenceEquals(parallel!.Source, c), "different owner can proceed while same owner waits");
Check(queue.TryAcquire(Current, out _) == VesselIntentQueue<object, object, int>.AcquireResult.None, "owner leases held until completion");
Check(queue.Complete(first!) && !queue.Complete(first!), "completion is exactly once");
Check(queue.TryAcquire(i => new(i.Source, i.Owner, i.Revision == 8), out var stale) == VesselIntentQueue<object, object, int>.AcquireResult.Stale
    && ReferenceEquals(stale!.Source, b), "changed revision is stale");
Check(queue.Complete(parallel!) && queue.Count == 0, "leases retire");
Check(queue.Enqueue(a, vessel1, 7, contact) && queue.Enqueue(b, vessel1, 7, contact), "sources can be reused after retirement");
Check(queue.TryAcquire(Current, out var held) == VesselIntentQueue<object, object, int>.AcquireResult.Acquired, "lease acquired");
Check(queue.CancelPending() == 1 && queue.Count == 1, "cancellation preserves active lease");
Check(queue.Complete(held!), "active cleanup releases lane");
Check(queue.Enqueue(a, vessel1, 7, contact), "enqueue exception test");
Check(queue.TryAcquire(_ => throw new InvalidOperationException(), out _) == VesselIntentQueue<object, object, int>.AcquireResult.Stale
    && queue.Count == 0, "inspection exception fails closed and retires");
Check(queue.Enqueue(a, vessel1, 7, contact), "enqueue ownership test");
Check(queue.TryAcquire(i => new(i.Source, vessel2, true), out _) == VesselIntentQueue<object, object, int>.AcquireResult.Stale,
    "external ownership change is stale");
Check(queue.Enqueue(a, vessel1, 7, contact), "enqueue source identity test");
Check(queue.TryAcquire(i => new(b, i.Owner, true), out _) == VesselIntentQueue<object, object, int>.AcquireResult.Stale,
    "recycled or substituted source identity is stale");
Console.WriteLine("Vessel intent checks passed.");
