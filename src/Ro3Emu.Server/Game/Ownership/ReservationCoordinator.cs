namespace Ro3Emu.Server.Game.Ownership;

public enum ReservationKind { AccountAttachment, RoleLifecycle, RolePart, Realm, Entity }

public readonly record struct ReservationKey(ReservationKind Kind, string Id, string Part = "")
{
    public static ReservationKey Role(long id, string part = "WholeRole") => new(ReservationKind.RolePart, id.ToString(System.Globalization.CultureInfo.InvariantCulture), part);
    public static ReservationKey Lifecycle(long id) => new(ReservationKind.RoleLifecycle, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static ReservationKey Account(string id) => new(ReservationKind.AccountAttachment, id.ToUpperInvariant());
    public static ReservationKey Realm => new(ReservationKind.Realm, "legacy");
    public static ReservationKey Entity(string kind, string id, string part = "state") =>
        new(ReservationKind.Entity, kind + ":" + id, part);
    /// <summary>A reader's hold on an entity: shared holds coexist, any exclusive part of the entity excludes them,
    /// and an exclusive hold covers a nested shared request.</summary>
    public static ReservationKey SharedEntity(string kind, string id) => new(ReservationKind.Entity, kind + ":" + id, SharedPart);
    internal const string SharedPart = "shared";
    internal bool Overlaps(ReservationKey other)
    {
        if (Kind == ReservationKind.Realm || other.Kind == ReservationKind.Realm) return true;
        if (Kind == ReservationKind.Entity || other.Kind == ReservationKind.Entity)
            return Kind == other.Kind && Id == other.Id &&
                (Part == other.Part ? Part != SharedPart : Part == SharedPart || other.Part == SharedPart);
        if (Kind == ReservationKind.AccountAttachment || other.Kind == ReservationKind.AccountAttachment)
            return Kind == other.Kind && Id == other.Id;
        return Id == other.Id && (Kind == ReservationKind.RoleLifecycle || other.Kind == ReservationKind.RoleLifecycle ||
            Part == "WholeRole" || other.Part == "WholeRole" || Part == other.Part);
    }
    internal bool Covers(ReservationKey other) => Kind == ReservationKind.Realm || this == other ||
        Kind == ReservationKind.Entity && other.Kind == ReservationKind.Entity && Id == other.Id && other.Part == SharedPart ||
        Id == other.Id && Kind is ReservationKind.RoleLifecycle or ReservationKind.RolePart &&
        other.Kind is ReservationKind.RoleLifecycle or ReservationKind.RolePart && (Kind == ReservationKind.RoleLifecycle || Part == "WholeRole");
}

/// <summary>
/// Loop-only, all-or-none reservations. Waiting never retains a partial key set. Held keys are indexed by what they can
/// overlap (ReservationKey.Overlaps: an entity, an account or a role id; a realm hold overlaps everything), so a request
/// checks only the holds that share an id with it instead of every hold: with thousands of sessions each holding its
/// role, a scan of all holds per attempt made admission quadratic.
/// </summary>
public sealed class ReservationCoordinator
{
    private readonly Dictionary<long, ReservationKey[]> _held = [];
    private readonly Dictionary<(int Group, string Id), List<(long Operation, ReservationKey Key)>> _byId = [];
    private readonly HashSet<long> _realmHolders = [];
    // Wake-ups per waiter, filed by bucket: a release completes only the waiters whose keys share a bucket with it, not
    // every waiter (with thousands of sessions one release used to resume every queued admission, each retrying and
    // waiting again), and every waiter awaits its own signal (thousands of awaiters on one task made each new await
    // rescan the task's continuation list).
    private readonly Dictionary<(int Group, string Id), List<TaskCompletionSource>> _bucketWaiters = [];
    private readonly List<TaskCompletionSource> _realmWaiters = [];
    private readonly List<TaskCompletionSource> _anyWaiters = [];
    // Holds with at least one key: a realm request is admitted only when there are none, so its waiters wake when
    // the count reaches zero rather than on every release.
    private int _keyedHolds;
    private readonly Dictionary<string, ReservationKey[]> _recovery = [];
    // Writer preference on shared entities: an exclusive request blocked only by shared holds of an entity marks the
    // entity, and new shared requests for it then wait, so overlapping readers cannot starve the writer. A writer
    // blocked by anything else marks nothing: it would hold readers back behind an unrelated wait.
    private readonly Dictionary<long, string[]> _waitingWriters = [];
    // The realm's turn: a realm hold needs a moment with no keyed hold at all, and with thousands of sessions taking
    // short role holds the next keyed request took every such moment first (realm work, every message still on
    // Realm:legacy among it, waited 40 s, and a player's later messages queued behind it). So when the last keyed hold
    // goes while a realm request waits, the moment is the realm's: keyed requests wait until a realm request is in.
    // Only the free moment is handed over; a long keyed hold still lets disjoint keyed work proceed meanwhile.
    private readonly HashSet<long> _waitingRealm = [];
    private bool _realmTurn;
    private TaskCompletionSource _changed = NewSignal();
    public long Conflicts { get; private set; }
    public int Active => _held.Count;
    internal int WaitingWriters => _waitingWriters.Count;
    public int RecoveryRequired => _recovery.Count;
    internal void Quarantine(string operation, IEnumerable<ReservationKey> keys) => _recovery[operation] = keys.Distinct().ToArray();
    internal Task Changed => _changed.Task;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly Comparison<ReservationKey> KeyOrder = (a, b) =>
    {
        int c = a.Kind.CompareTo(b.Kind);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return c != 0 ? c : string.CompareOrdinal(a.Part, b.Part);
    };

    public bool TryReserve(long operation, IEnumerable<ReservationKey> keys)
    {
        if (_held.ContainsKey(operation)) throw new InvalidOperationException("Operation already reserved; pass its explicit owner context to nested work.");
        var ordered = keys.Distinct().ToArray();
        if (ordered.Length > 1) Array.Sort(ordered, KeyOrder);
        if (_recovery.Values.Any(held => held.Any(a => ordered.Any(a.Overlaps))))
            throw new InvalidOperationException("An overlapping committed owner operation requires recovery.");
        bool realm = ordered.Any(k => k.Kind == ReservationKind.Realm);
        if (Overlapping(ordered).Any())
        {
            Conflicts++;
            var starved = ReaderOnlyBlocks(ordered);
            if (starved.Length != 0) _waitingWriters[operation] = starved;
            else AbandonWriter(operation);
            if (realm) _waitingRealm.Add(operation);
            return false;
        }
        if (!realm && ordered.Length != 0 && _realmTurn)
        {
            Conflicts++;
            return false;
        }
        if (realm) { _waitingRealm.Remove(operation); _realmTurn = false; }
        if (_waitingWriters.Count != 0 && ordered.Any(key => key.Kind == ReservationKind.Entity && key.Part == ReservationKey.SharedPart
            && _waitingWriters.Any(writer => writer.Key != operation && writer.Value.Contains(key.Id))))
        {
            Conflicts++;
            return false;
        }
        _waitingWriters.Remove(operation); // admitted: it holds the entity now, readers wait for its release
        _held.Add(operation, ordered);
        if (ordered.Length != 0) _keyedHolds++;
        Index(operation, ordered);
        return true;
    }

    /// <summary>The bucket of keys a key can overlap besides realm holds: same entity, same account, same role.</summary>
    private static (int Group, string Id)? Bucket(ReservationKey key) => key.Kind switch
    {
        ReservationKind.Realm => null,
        ReservationKind.Entity => (0, key.Id),
        ReservationKind.AccountAttachment => (1, key.Id),
        _ => (2, key.Id), // RolePart and RoleLifecycle of one role overlap each other
    };

    private void Index(long operation, ReservationKey[] keys)
    {
        foreach (var key in keys)
        {
            if (Bucket(key) is not { } bucket) { _realmHolders.Add(operation); continue; }
            if (!_byId.TryGetValue(bucket, out var list)) _byId[bucket] = list = [];
            list.Add((operation, key));
        }
    }

    private void Unindex(long operation, ReservationKey[] keys)
    {
        _realmHolders.Remove(operation);
        foreach (var key in keys)
            if (Bucket(key) is { } bucket && _byId.TryGetValue(bucket, out var list))
            {
                list.RemoveAll(x => x.Operation == operation);
                if (list.Count == 0) _byId.Remove(bucket);
            }
    }

    /// <summary>Every held (operation, key) that overlaps a key of <paramref name="request"/>.</summary>
    private IEnumerable<(long Operation, ReservationKey Key)> Overlapping(ReservationKey[] request)
    {
        if (request.Length == 0) yield break; // a runtime operation holds nothing and is blocked by nothing
        if (request.Any(k => k.Kind == ReservationKind.Realm))
        {
            foreach (var (operation, held) in _held)
                foreach (var key in held) yield return (operation, key);
            yield break;
        }
        foreach (var operation in _realmHolders)
            foreach (var key in _held[operation])
                if (key.Kind == ReservationKind.Realm) yield return (operation, key);
        foreach (var bucket in request.Select(Bucket).OfType<(int, string)>().Distinct())
            if (_byId.TryGetValue(bucket, out var list))
                foreach (var held in list)
                    if (request.Any(held.Key.Overlaps)) yield return held;
    }
    /// <summary>A waiting request gave up (cancelled, or a one-shot try): its writer mark no longer holds readers back.</summary>
    internal void Abandon(long operation)
    {
        AbandonWriter(operation);
        // The last waiting realm request gave up its turn: the keyed requests that waited for it wait with the realm
        // waiters.
        if (_waitingRealm.Remove(operation) && _waitingRealm.Count == 0 && _realmTurn)
        {
            _realmTurn = false;
            Signal();
            Wake(_realmWaiters);
        }
    }

    private void AbandonWriter(long operation)
    {
        if (!_waitingWriters.Remove(operation, out var entities)) return;
        Signal();
        Wake(_anyWaiters);
        foreach (var entity in entities) WakeBucket((0, entity));
    }

    /// <summary>
    /// Completes when something that may admit <paramref name="keys"/> changes: a release or a writer giving up in one of
    /// their buckets, the release of a realm hold; a realm request wakes when no keyed hold is left (or a writer gives up).
    /// Call it right after a refused <see cref="TryReserve"/>, in the same loop turn.
    /// </summary>
    internal Task ChangedFor(ReservationKey[] keys)
    {
        var waiter = NewSignal();
        if (keys.Length == 0 || keys.Any(k => k.Kind == ReservationKind.Realm))
        {
            _anyWaiters.Add(waiter);
            return waiter.Task;
        }
        if (_realmHolders.Count != 0 || _realmTurn) _realmWaiters.Add(waiter);
        foreach (var bucket in keys.Select(Bucket).OfType<(int, string)>().Distinct())
        {
            if (!_bucketWaiters.TryGetValue(bucket, out var list)) _bucketWaiters[bucket] = list = [];
            list.Add(waiter);
        }
        return waiter.Task;
    }

    private void WakeBucket((int, string) bucket)
    {
        if (_bucketWaiters.Remove(bucket, out var list)) Wake(list);
    }

    private static void Wake(List<TaskCompletionSource> waiters)
    {
        if (waiters.Count == 0) return;
        var woken = waiters.ToArray();
        waiters.Clear();
        foreach (var waiter in woken) waiter.TrySetResult();
    }
    // The entities whose exclusive keys in the request are blocked only by shared holds; empty when anything else blocks.
    private string[] ReaderOnlyBlocks(ReservationKey[] request)
    {
        var entities = new List<string>();
        foreach (var (_, key) in Overlapping(request))
        {
            if (key.Kind != ReservationKind.Entity || key.Part != ReservationKey.SharedPart
                || !request.Any(r => r.Kind == ReservationKind.Entity && r.Id == key.Id && r.Part != ReservationKey.SharedPart))
                return [];
            if (!entities.Contains(key.Id)) entities.Add(key.Id);
        }
        return entities.ToArray();
    }
    public void Release(long operation)
    {
        if (!_held.Remove(operation, out var released)) throw new InvalidOperationException("Reservation released twice.");
        Unindex(operation, released);
        // A runtime operation holds no keys: its release cannot unblock anyone, so it wakes no waiter.
        if (released.Length == 0) return;
        Signal();
        if (--_keyedHolds == 0)
        {
            if (_waitingRealm.Count != 0) _realmTurn = true;
            Wake(_anyWaiters);
        }
        if (released.Any(k => k.Kind == ReservationKind.Realm)) Wake(_realmWaiters);
        foreach (var bucket in released.Select(Bucket).OfType<(int, string)>().Distinct()) WakeBucket(bucket);
    }
    private void Signal() { var changed = _changed; _changed = NewSignal(); changed.TrySetResult(); }
    /// <summary>An operation holding a key that overlaps <paramref name="keys"/>, and that key (diagnostics), or null.</summary>
    internal (long Operation, ReservationKey Key)? Blocker(IEnumerable<ReservationKey> keys)
    {
        var request = keys.ToArray();
        foreach (var held in Overlapping(request).OrderBy(x => x.Operation)) return held;
        return null;
    }
    internal bool Covers(long operation, IEnumerable<ReservationKey> keys) =>
        _held.TryGetValue(operation, out var held) && keys.All(key => held.Any(x => x.Covers(key)));
}
