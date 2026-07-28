#!/usr/bin/env python3
"""Workload benchmark: is LadybugDB's columnar/vectorized engine viable for a MUSH?

Measures the write-heavy, small-mutation pattern SharpMUSH actually produces, against
LadybugDB's analytics-oriented storage. Thresholds are pre-registered (see THRESHOLDS)
so the verdict is not rationalized after seeing numbers.

Two schema models are measured, because a MUSH's arbitrary sparse per-object attributes
do not map cleanly onto a columnar store:

  map  - Obj.attrs as MAP(STRING,STRING). One attribute set rewrites the whole map.
  edge - Attributes as their own node table joined by a rel. Set touches one row,
         but every read becomes a traversal.

SQLite is measured on the identical workload as an in-process OLTP reference point.
It is not SurrealDB (what SharpMUSH runs today) -- it is a "what does a good embedded
OLTP engine do on this hardware" control.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import shutil
import sqlite3
import statistics
import subprocess
import sys
import threading
import time
from dataclasses import dataclass, field, asdict

import ladybug

SEED = 20260727
ATTRS_PER_OBJ = 10
ATTR_NAMES = ["DESC", "SEX", "LAST", "ALIAS", "IDLE", "AWAY", "EMAIL", "MAILCURF", "TZ", "PENNIES"]

# Pre-registered decision thresholds. Set before any measurement.
# Rationale: a MUSH command that mutates must feel instant; p99 is what players notice.
THRESHOLDS = {
    "single_write_p99_ms": 5.0,       # one @set, committed on its own
    "point_lookup_p99_ms": 2.0,       # one attribute read by dbref+name
    "read_under_write_p99_ms": 5.0,   # same read while a writer runs continuously
    "batch_knee_min_speedup": 10.0,   # >=10x gain by 100 mutations/tx rescues bad per-commit cost
}


@dataclass
class Stat:
    n: int = 0
    p50: float = 0.0
    p95: float = 0.0
    p99: float = 0.0
    max: float = 0.0
    mean: float = 0.0

    @staticmethod
    def of(samples_ms: list[float]) -> "Stat":
        if not samples_ms:
            return Stat()
        s = sorted(samples_ms)
        q = lambda p: s[min(len(s) - 1, int(len(s) * p))]
        return Stat(len(s), q(0.50), q(0.95), q(0.99), s[-1], statistics.fmean(s))


@dataclass
class ModelResult:
    model: str
    size: int
    load_s: float = 0.0
    disk_mb_after_load: float = 0.0
    cold_open_ms: float = 0.0
    single_write: Stat = field(default_factory=Stat)
    point_lookup: Stat = field(default_factory=Stat)
    contents_traversal: Stat = field(default_factory=Stat)
    read_under_write: Stat = field(default_factory=Stat)
    batch_rates: dict = field(default_factory=dict)     # batch_size -> mutations/sec
    concurrent_rates: dict = field(default_factory=dict)  # writers -> mutations/sec
    concurrent_conflicts: dict = field(default_factory=dict)  # writers -> conflict retries
    disk_mb_after_mutations: float = 0.0
    notes: list = field(default_factory=list)


def du_mb(path: str) -> float:
    """Size of a database, which may be a single file or a directory (plus WAL siblings)."""
    total = 0
    if os.path.isfile(path):
        total += os.path.getsize(path)
        for suf in (".wal", ".shadow", "-wal", ".lock"):
            try:
                total += os.path.getsize(path + suf)
            except OSError:
                pass
    for root, _, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return round(total / 1048576, 2)


def du_mb_file(path: str) -> float:
    try:
        return round(os.path.getsize(path) / 1048576, 2)
    except OSError:
        return 0.0


# --------------------------------------------------------------------------- Ladybug

def lbug_purge(path: str) -> None:
    """A Ladybug database is a single file plus siblings, not a directory.

    rmtree() silently no-ops on a file, which leaves a stale catalog behind and makes a
    rerun fail with "already exists in catalog" -- remove both forms explicitly.
    """
    shutil.rmtree(path, ignore_errors=True)
    for suf in ("", ".wal", ".shadow", "-wal", ".lock", ".tmp"):
        try:
            os.remove(path + suf)
        except OSError:
            pass


def lbug_fresh(path: str) -> tuple:
    lbug_purge(path)
    db = ladybug.Database(path)
    return db, ladybug.Connection(db)


def lbug_schema(conn, model: str) -> None:
    if model == "map":
        conn.execute(
            "CREATE NODE TABLE Obj(dbref INT64, name STRING, loc INT64, "
            "attrs MAP(STRING,STRING), PRIMARY KEY(dbref))"
        )
    else:
        conn.execute("CREATE NODE TABLE Obj(dbref INT64, name STRING, loc INT64, PRIMARY KEY(dbref))")
        conn.execute("CREATE NODE TABLE Attr(akey STRING, aname STRING, aval STRING, PRIMARY KEY(akey))")
        conn.execute("CREATE REL TABLE Has(FROM Obj TO Attr)")
    conn.execute("CREATE REL TABLE Located(FROM Obj TO Obj)")


def lbug_load(conn, model: str, size: int, rng: random.Random) -> None:
    """Bulk load via batched transactions -- this is setup, not a measured path."""
    BATCH = 1000
    for start in range(0, size, BATCH):
        conn.execute("BEGIN TRANSACTION")
        for i in range(start, min(start + BATCH, size)):
            loc = rng.randrange(0, max(1, size // 10))
            if model == "map":
                keys = ",".join(f"'{a}'" for a in ATTR_NAMES[:ATTRS_PER_OBJ])
                vals = ",".join(f"'v{i}_{j}'" for j in range(ATTRS_PER_OBJ))
                conn.execute(
                    f"CREATE (o:Obj {{dbref: {i}, name: 'obj{i}', loc: {loc}, "
                    f"attrs: map([{keys}],[{vals}])}})"
                )
            else:
                conn.execute(f"CREATE (o:Obj {{dbref: {i}, name: 'obj{i}', loc: {loc}}})")
                for j, a in enumerate(ATTR_NAMES[:ATTRS_PER_OBJ]):
                    conn.execute(
                        f"CREATE (a:Attr {{akey: '{i}/{a}', aname: '{a}', aval: 'v{i}_{j}'}})"
                    )
        conn.execute("COMMIT")

    # location edges, batched
    for start in range(0, size, BATCH):
        conn.execute("BEGIN TRANSACTION")
        for i in range(start, min(start + BATCH, size)):
            conn.execute(
                f"MATCH (a:Obj), (b:Obj) WHERE a.dbref={i} AND b.dbref=a.loc "
                f"CREATE (a)-[:Located]->(b)"
            )
        conn.execute("COMMIT")

    if model == "edge":
        for start in range(0, size, BATCH):
            conn.execute("BEGIN TRANSACTION")
            for i in range(start, min(start + BATCH, size)):
                for a in ATTR_NAMES[:ATTRS_PER_OBJ]:
                    conn.execute(
                        f"MATCH (o:Obj), (t:Attr) WHERE o.dbref={i} AND t.akey='{i}/{a}' "
                        f"CREATE (o)-[:Has]->(t)"
                    )
            conn.execute("COMMIT")


def lbug_set_attr(conn, model: str, dbref: int, attr: str, val: str, size: int) -> None:
    """One @set. The measured unit of work."""
    if model == "map":
        # MAP has no per-key update: the whole map must be rewritten.
        keys = ",".join(f"'{a}'" for a in ATTR_NAMES[:ATTRS_PER_OBJ])
        vals = ",".join(
            f"'{val}'" if a == attr else f"'v{dbref}_{j}'"
            for j, a in enumerate(ATTR_NAMES[:ATTRS_PER_OBJ])
        )
        conn.execute(
            f"MATCH (o:Obj) WHERE o.dbref={dbref} SET o.attrs = map([{keys}],[{vals}])"
        )
    else:
        conn.execute(f"MATCH (a:Attr) WHERE a.akey='{dbref}/{attr}' SET a.aval='{val}'")


def lbug_read_attr(conn, model: str, dbref: int, attr: str):
    if model == "map":
        return conn.execute(f"MATCH (o:Obj) WHERE o.dbref={dbref} RETURN o.attrs").get_next()
    return conn.execute(f"MATCH (a:Attr) WHERE a.akey='{dbref}/{attr}' RETURN a.aval").get_next()


def run_ladybug(model: str, size: int, samples: int, root: str, rng: random.Random) -> ModelResult:
    path = os.path.join(root, f"lbug-{model}-{size}")
    res = ModelResult(model=f"ladybug-{model}", size=size)

    db, conn = lbug_fresh(path)
    lbug_schema(conn, model)
    t0 = time.perf_counter()
    lbug_load(conn, model, size, rng)
    res.load_s = round(time.perf_counter() - t0, 2)
    res.disk_mb_after_load = du_mb(path)

    # cold open: close, reopen, time it
    del conn, db
    t0 = time.perf_counter_ns()
    db = ladybug.Database(path)
    conn = ladybug.Connection(db)
    conn.execute("MATCH (o:Obj) WHERE o.dbref=0 RETURN o.dbref").get_next()
    res.cold_open_ms = round((time.perf_counter_ns() - t0) / 1e6, 2)

    # 1. single-mutation commit latency (one @set per transaction)
    lat = []
    for k in range(samples):
        d = rng.randrange(0, size)
        a = rng.choice(ATTR_NAMES[:ATTRS_PER_OBJ])
        t0 = time.perf_counter_ns()
        conn.execute("BEGIN TRANSACTION")
        lbug_set_attr(conn, model, d, a, f"s{k}", size)
        conn.execute("COMMIT")
        lat.append((time.perf_counter_ns() - t0) / 1e6)
    res.single_write = Stat.of(lat)

    # 2. batching knee
    for batch in (1, 10, 100, 1000):
        total = min(batch * 20, 2000)
        t0 = time.perf_counter()
        done = 0
        while done < total:
            conn.execute("BEGIN TRANSACTION")
            for _ in range(min(batch, total - done)):
                d = rng.randrange(0, size)
                a = rng.choice(ATTR_NAMES[:ATTRS_PER_OBJ])
                lbug_set_attr(conn, model, d, a, "b", size)
                done += 1
            conn.execute("COMMIT")
        el = time.perf_counter() - t0
        res.batch_rates[batch] = round(total / el, 1) if el > 0 else 0.0

    # 3. point lookup
    lat = []
    for _ in range(samples):
        d = rng.randrange(0, size)
        a = rng.choice(ATTR_NAMES[:ATTRS_PER_OBJ])
        t0 = time.perf_counter_ns()
        lbug_read_attr(conn, model, d, a)
        lat.append((time.perf_counter_ns() - t0) / 1e6)
    res.point_lookup = Stat.of(lat)

    # 4. contents-of-room traversal
    lat = []
    for _ in range(max(200, samples // 4)):
        r = rng.randrange(0, max(1, size // 10))
        t0 = time.perf_counter_ns()
        conn.execute(
            f"MATCH (a:Obj)-[:Located]->(b:Obj) WHERE b.dbref={r} RETURN count(a)"
        ).get_next()
        lat.append((time.perf_counter_ns() - t0) / 1e6)
    res.contents_traversal = Stat.of(lat)

    # 5. read latency under continuous write (separate connection)
    stop = threading.Event()
    err: list = []

    def writer():
        try:
            wc = ladybug.Connection(db)
            i = 0
            while not stop.is_set():
                d = rng.randrange(0, size)
                wc.execute("BEGIN TRANSACTION")
                lbug_set_attr(wc, model, d, "DESC", f"w{i}", size)
                wc.execute("COMMIT")
                i += 1
        except Exception as e:  # noqa: BLE001
            err.append(f"{type(e).__name__}: {e}")

    th = threading.Thread(target=writer, daemon=True)
    th.start()
    time.sleep(0.3)
    lat = []
    for _ in range(min(samples, 500)):
        d = rng.randrange(0, size)
        a = rng.choice(ATTR_NAMES[:ATTRS_PER_OBJ])
        t0 = time.perf_counter_ns()
        try:
            lbug_read_attr(conn, model, d, a)
            lat.append((time.perf_counter_ns() - t0) / 1e6)
        except Exception as e:  # noqa: BLE001
            err.append(f"read-under-write {type(e).__name__}: {e}")
            break
    stop.set()
    th.join(timeout=10)
    res.read_under_write = Stat.of(lat)
    if err:
        res.notes.append(f"read_under_write errors: {err[:3]}")

    # 6. concurrent writers.
    # Ladybug permits exactly one write transaction at a time and *raises* rather than
    # queueing, so a naive harness would simply kill every loser thread and report the
    # survivor's rate as "throughput". Retry on conflict and count conflicts instead.
    for nw in (1, 2, 4, 8):
        stop2 = threading.Event()
        counts = [0] * nw
        conflicts = [0] * nw
        fatal: list = []

        def w(idx: int):
            wc = ladybug.Connection(db)
            lrng = random.Random(SEED + idx)
            while not stop2.is_set():
                d = lrng.randrange(0, size)
                try:
                    wc.execute("BEGIN TRANSACTION")
                    lbug_set_attr(wc, model, d, "LAST", "c", size)
                    wc.execute("COMMIT")
                    counts[idx] += 1
                except Exception as e:  # noqa: BLE001
                    msg = str(e)
                    if "one write transaction" in msg or "write transaction" in msg:
                        conflicts[idx] += 1
                        try:
                            wc.execute("ROLLBACK")
                        except Exception:  # noqa: BLE001
                            pass
                        time.sleep(0.0005)
                    else:
                        fatal.append(f"{type(e).__name__}: {msg[:90]}")
                        return

        ths = [threading.Thread(target=w, args=(i,), daemon=True) for i in range(nw)]
        t0 = time.perf_counter()
        for t in ths:
            t.start()
        time.sleep(3.0)
        stop2.set()
        for t in ths:
            t.join(timeout=15)
        el = time.perf_counter() - t0
        res.concurrent_rates[nw] = round(sum(counts) / el, 1)
        res.concurrent_conflicts[nw] = sum(conflicts)
        if fatal:
            res.notes.append(f"concurrent w={nw} FATAL: {fatal[:2]}")

    res.disk_mb_after_mutations = du_mb(path)
    del conn, db
    return res


# --------------------------------------------------------------------------- SQLite reference

def run_sqlite(size: int, samples: int, root: str, rng: random.Random) -> ModelResult:
    path = os.path.join(root, f"sqlite-{size}.db")
    for suf in ("", "-wal", "-shm"):
        try:
            os.remove(path + suf)
        except OSError:
            pass
    res = ModelResult(model="sqlite-reference", size=size)

    con = sqlite3.connect(path)
    con.execute("PRAGMA journal_mode=WAL")
    con.execute("PRAGMA synchronous=FULL")  # durability comparable to a committed tx
    con.execute("CREATE TABLE obj(dbref INTEGER PRIMARY KEY, name TEXT, loc INTEGER)")
    con.execute("CREATE TABLE attr(dbref INTEGER, aname TEXT, aval TEXT, PRIMARY KEY(dbref,aname))")
    con.execute("CREATE INDEX idx_loc ON obj(loc)")

    t0 = time.perf_counter()
    con.execute("BEGIN")
    for i in range(size):
        con.execute("INSERT INTO obj VALUES(?,?,?)", (i, f"obj{i}", rng.randrange(0, max(1, size // 10))))
        con.executemany(
            "INSERT INTO attr VALUES(?,?,?)",
            [(i, a, f"v{i}_{j}") for j, a in enumerate(ATTR_NAMES[:ATTRS_PER_OBJ])],
        )
    con.commit()
    res.load_s = round(time.perf_counter() - t0, 2)
    res.disk_mb_after_load = du_mb_file(path)

    con.close()
    t0 = time.perf_counter_ns()
    con = sqlite3.connect(path)
    con.execute("SELECT aval FROM attr WHERE dbref=0 AND aname='DESC'").fetchone()
    res.cold_open_ms = round((time.perf_counter_ns() - t0) / 1e6, 2)
    con.execute("PRAGMA journal_mode=WAL")
    con.execute("PRAGMA synchronous=FULL")

    lat = []
    for k in range(samples):
        d = rng.randrange(0, size)
        a = rng.choice(ATTR_NAMES[:ATTRS_PER_OBJ])
        t0 = time.perf_counter_ns()
        con.execute("UPDATE attr SET aval=? WHERE dbref=? AND aname=?", (f"s{k}", d, a))
        con.commit()
        lat.append((time.perf_counter_ns() - t0) / 1e6)
    res.single_write = Stat.of(lat)

    for batch in (1, 10, 100, 1000):
        total = min(batch * 20, 2000)
        t0 = time.perf_counter()
        done = 0
        while done < total:
            for _ in range(min(batch, total - done)):
                d = rng.randrange(0, size)
                con.execute("UPDATE attr SET aval='b' WHERE dbref=? AND aname='DESC'", (d,))
                done += 1
            con.commit()
        el = time.perf_counter() - t0
        res.batch_rates[batch] = round(total / el, 1) if el > 0 else 0.0

    lat = []
    for _ in range(samples):
        d = rng.randrange(0, size)
        a = rng.choice(ATTR_NAMES[:ATTRS_PER_OBJ])
        t0 = time.perf_counter_ns()
        con.execute("SELECT aval FROM attr WHERE dbref=? AND aname=?", (d, a)).fetchone()
        lat.append((time.perf_counter_ns() - t0) / 1e6)
    res.point_lookup = Stat.of(lat)

    lat = []
    for _ in range(max(200, samples // 4)):
        r = rng.randrange(0, max(1, size // 10))
        t0 = time.perf_counter_ns()
        con.execute("SELECT count(*) FROM obj WHERE loc=?", (r,)).fetchone()
        lat.append((time.perf_counter_ns() - t0) / 1e6)
    res.contents_traversal = Stat.of(lat)

    # read latency under continuous write -- same shape as the Ladybug measurement
    stop = threading.Event()

    def sq_writer():
        wc = sqlite3.connect(path, timeout=30)
        wc.execute("PRAGMA synchronous=FULL")
        lrng = random.Random(SEED + 99)
        i = 0
        while not stop.is_set():
            try:
                wc.execute("UPDATE attr SET aval=? WHERE dbref=? AND aname='DESC'",
                           (f"w{i}", lrng.randrange(0, size)))
                wc.commit()
                i += 1
            except sqlite3.OperationalError:
                pass
        wc.close()

    th = threading.Thread(target=sq_writer, daemon=True)
    th.start()
    time.sleep(0.3)
    lat = []
    for _ in range(min(samples, 500)):
        d = rng.randrange(0, size)
        a = rng.choice(ATTR_NAMES[:ATTRS_PER_OBJ])
        t0 = time.perf_counter_ns()
        con.execute("SELECT aval FROM attr WHERE dbref=? AND aname=?", (d, a)).fetchone()
        lat.append((time.perf_counter_ns() - t0) / 1e6)
    stop.set()
    th.join(timeout=10)
    res.read_under_write = Stat.of(lat)

    # concurrent writers, measured identically to Ladybug (retry on lock contention)
    for nw in (1, 2, 4, 8):
        stop2 = threading.Event()
        counts = [0] * nw
        conflicts = [0] * nw

        def sw(idx: int):
            wc = sqlite3.connect(path, timeout=0.001)
            wc.execute("PRAGMA synchronous=FULL")
            lrng = random.Random(SEED + idx)
            while not stop2.is_set():
                try:
                    wc.execute("UPDATE attr SET aval='c' WHERE dbref=? AND aname='LAST'",
                               (lrng.randrange(0, size),))
                    wc.commit()
                    counts[idx] += 1
                except sqlite3.OperationalError:
                    conflicts[idx] += 1
                    time.sleep(0.0005)
            wc.close()

        ths = [threading.Thread(target=sw, args=(i,), daemon=True) for i in range(nw)]
        t0 = time.perf_counter()
        for t in ths:
            t.start()
        time.sleep(3.0)
        stop2.set()
        for t in ths:
            t.join(timeout=15)
        el = time.perf_counter() - t0
        res.concurrent_rates[nw] = round(sum(counts) / el, 1)
        res.concurrent_conflicts[nw] = sum(conflicts)

    res.disk_mb_after_mutations = du_mb_file(path)
    res.notes.append("in-process OLTP control, not SurrealDB; synchronous=FULL, WAL")
    con.close()
    return res


# --------------------------------------------------------------------------- main

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sizes", type=int, nargs="+", default=[1000, 10000])
    ap.add_argument("--samples", type=int, default=500)
    ap.add_argument("--root", default="/home/grave/.cache/lbug-bench")
    ap.add_argument("--models", nargs="+", default=["map", "edge"])
    ap.add_argument("--skip-sqlite", action="store_true")
    ap.add_argument("--out", default="results.json")
    args = ap.parse_args()

    os.makedirs(args.root, exist_ok=True)
    meta = {
        "seed": SEED,
        "thresholds": THRESHOLDS,
        "attrs_per_obj": ATTRS_PER_OBJ,
        "host": {
            "cpu_count": os.cpu_count(),
            "storage": "nvme (/home)",
            "python": sys.version.split()[0],
            "ladybug": getattr(ladybug, "__version__", "0.18.3"),
        },
    }
    results: list = []

    def flush() -> None:
        """Persist after every model so a long run survives being killed partway."""
        with open(args.out, "w") as f:
            json.dump({"meta": meta, "results": results, "complete": False}, f, indent=2)

    for size in args.sizes:
        for model in args.models:
            print(f"[run] ladybug-{model} size={size} ...", flush=True)
            t0 = time.perf_counter()
            r = run_ladybug(model, size, args.samples, args.root, random.Random(SEED))
            print(f"      done in {time.perf_counter()-t0:.1f}s  "
                  f"write_p99={r.single_write.p99:.3f}ms  lookup_p99={r.point_lookup.p99:.3f}ms",
                  flush=True)
            results.append(asdict(r))
            flush()
        if not args.skip_sqlite:
            print(f"[run] sqlite size={size} ...", flush=True)
            r = run_sqlite(size, args.samples, args.root, random.Random(SEED))
            print(f"      write_p99={r.single_write.p99:.3f}ms  lookup_p99={r.point_lookup.p99:.3f}ms",
                  flush=True)
            results.append(asdict(r))
            flush()

    with open(args.out, "w") as f:
        json.dump({"meta": meta, "results": results, "complete": True}, f, indent=2)
    print(f"\nwrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
