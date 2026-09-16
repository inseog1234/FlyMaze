#!/usr/bin/env python3
"""Build a runtime representation of the official MaleCNS v1.0 connectome.

Source data is downloaded directly from HHMI Janelia's public Google Storage bucket.
The raw ~1.2 GB files and generated runtime cache belong in Unity's Library/MaleCNS
folder and are intentionally not committed to Git.

The connectome supplies the wiring and synapse-count weights. The small LIF dynamics,
stimulus encoding, and motor decoding used by FlyMaze are engineering/modeling choices;
they are not measurements from the dataset.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.request
from pathlib import Path

import numpy as np
import pandas as pd
import pyarrow.feather as feather
from scipy import sparse

BASE = "https://storage.googleapis.com/flyem-male-cns/v1.0/connectome-data/flat-connectome"
FILES = {
    "annotations": "body-annotations-male-cns-v1.0-minconf-0.5.feather",
    "neurotransmitters": "body-neurotransmitters-male-cns-v1.0.feather",
    "weights": "connectome-weights-male-cns-v1.0-minconf-0.5.feather",
}
INHIBITORY = {"gaba", "glutamate", "histamine"}


def _human(n: int) -> str:
    units = ["B", "KB", "MB", "GB"]
    value = float(n)
    for unit in units:
        if value < 1024 or unit == units[-1]:
            return f"{value:.1f} {unit}"
        value /= 1024
    return f"{value:.1f} GB"


def download_resume(url: str, dst: Path) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    partial = dst.with_suffix(dst.suffix + ".part")
    start = partial.stat().st_size if partial.exists() else 0
    headers = {"User-Agent": "FlyMaze-MaleCNS/1.0"}
    if start:
        headers["Range"] = f"bytes={start}-"

    request = urllib.request.Request(url, headers=headers)
    print(f"[MaleCNS] Downloading {dst.name}" + (f" (resume {_human(start)})" if start else ""))
    with urllib.request.urlopen(request, timeout=60) as response:
        status = getattr(response, "status", 200)
        if start and status != 206:
            start = 0
            mode = "wb"
        else:
            mode = "ab" if start else "wb"

        total_header = response.headers.get("Content-Length")
        total = start + int(total_header) if total_header else 0
        done = start
        last_print = 0.0
        with partial.open(mode) as f:
            while True:
                chunk = response.read(8 * 1024 * 1024)
                if not chunk:
                    break
                f.write(chunk)
                done += len(chunk)
                now = time.time()
                if now - last_print > 0.5:
                    if total:
                        print(f"\r  {_human(done)} / {_human(total)}  ({done / total * 100:5.1f}%)", end="", flush=True)
                    else:
                        print(f"\r  {_human(done)}", end="", flush=True)
                    last_print = now
    print()
    partial.replace(dst)


def ensure_raw(data_dir: Path) -> dict[str, Path]:
    raw = data_dir / "raw"
    raw.mkdir(parents=True, exist_ok=True)
    paths = {}
    for key, name in FILES.items():
        path = raw / name
        paths[key] = path
        if not path.exists():
            download_resume(f"{BASE}/{name}", path)
    return paths


def _column(df: pd.DataFrame, name: str, default: str = "") -> pd.Series:
    if name in df.columns:
        return df[name]
    return pd.Series([default] * len(df), index=df.index)


def _pick(cell_type: np.ndarray, side: np.ndarray, names: tuple[str, ...], wanted_side: str | None = None) -> np.ndarray:
    mask = np.isin(cell_type, names)
    if wanted_side:
        mask &= side == wanted_side
    return np.flatnonzero(mask).astype(np.int32)


def build(data_dir: Path) -> None:
    data_dir.mkdir(parents=True, exist_ok=True)
    paths = ensure_raw(data_dir)

    print("[MaleCNS] Reading annotations...")
    ann = feather.read_table(paths["annotations"]).to_pandas()
    if "bodyId" not in ann.columns:
        raise RuntimeError("MaleCNS annotation schema changed: bodyId is missing")

    superclass_series = _column(ann, "superclass").fillna("").astype(str)
    ann = ann.loc[superclass_series.ne("")].copy()
    ann = ann.drop_duplicates("bodyId").sort_values("bodyId").set_index("bodyId")

    ids = ann.index.to_numpy(np.int64)
    n = len(ids)
    print(f"[MaleCNS] Selected {n:,} annotated neurons")

    flywire_type = _column(ann, "flywireType").fillna("").astype(str)
    fallback_type = _column(ann, "type").fillna("").astype(str)
    cell_type = flywire_type.where(flywire_type.ne(""), fallback_type).to_numpy(dtype=str)

    soma_side = _column(ann, "somaSide").fillna("").astype(str).str.upper()
    root_side = _column(ann, "rootSide").fillna("").astype(str).str.upper()
    instance = _column(ann, "instance").fillna("").astype(str).str.upper()
    resolved_side = soma_side.where(soma_side.isin(["L", "R"]), root_side)
    side_np = resolved_side.to_numpy(dtype=str)
    for i in np.flatnonzero(~np.isin(side_np, ["L", "R"])):
        text = instance.iloc[i]
        if "_L" in text or "(L)" in text:
            side_np[i] = "L"
        elif "_R" in text or "(R)" in text:
            side_np[i] = "R"

    print("[MaleCNS] Reading neurotransmitter predictions...")
    nt = feather.read_table(paths["neurotransmitters"], columns=["body", "consensus_nt"]).to_pandas()
    nt = nt.drop_duplicates("body").set_index("body")
    nt_label = nt.reindex(ids)["consensus_nt"].fillna("unknown").astype(str).str.lower().to_numpy()
    sign = np.ones(n, np.float32)
    sign[np.isin(nt_label, list(INHIBITORY))] = -1.0

    print("[MaleCNS] Streaming full connection graph...")
    table = feather.read_table(paths["weights"], columns=["body_pre", "body_post", "weight"], memory_map=True)
    pre_parts: list[np.ndarray] = []
    post_parts: list[np.ndarray] = []
    weight_parts: list[np.ndarray] = []
    scanned = 0

    for batch in table.to_batches(max_chunksize=3_000_000):
        pre_id = batch.column(0).to_numpy(zero_copy_only=False).astype(np.int64, copy=False)
        post_id = batch.column(1).to_numpy(zero_copy_only=False).astype(np.int64, copy=False)
        raw_weight = batch.column(2).to_numpy(zero_copy_only=False).astype(np.float32, copy=False)

        pre = np.searchsorted(ids, pre_id)
        post = np.searchsorted(ids, post_id)
        valid = (pre < n) & (post < n)
        if np.any(valid):
            safe_pre = np.minimum(pre, n - 1)
            safe_post = np.minimum(post, n - 1)
            valid &= (ids[safe_pre] == pre_id) & (ids[safe_post] == post_id)

        if np.any(valid):
            pre_parts.append(pre[valid].astype(np.int32, copy=False))
            post_parts.append(post[valid].astype(np.int32, copy=False))
            weight_parts.append(raw_weight[valid].astype(np.float32, copy=False))

        scanned += len(pre_id)
        print(f"\r  scanned {scanned:,} / {table.num_rows:,} connection rows", end="", flush=True)
    print()
    del table

    pre = np.concatenate(pre_parts)
    post = np.concatenate(post_parts)
    weight = np.concatenate(weight_parts)
    del pre_parts, post_parts, weight_parts

    weight *= sign[pre]
    incoming = np.bincount(post, weights=np.abs(weight), minlength=n).astype(np.float32)
    weight /= np.maximum(incoming[post], 1.0)

    W = sparse.csr_matrix((weight, (post, pre)), shape=(n, n), dtype=np.float32)
    W.sum_duplicates()
    print(f"[MaleCNS] Runtime graph: {W.shape[0]:,} neurons, {W.nnz:,} directed connections")

    superclass_np = _column(ann, "superclass").fillna("").astype(str).to_numpy(dtype=str)
    sensory = np.flatnonzero(np.char.find(np.char.lower(superclass_np.astype(str)), "sensory") >= 0).astype(np.int32)

    groups: dict[str, np.ndarray] = {
        "loom_L": _pick(cell_type, side_np, ("LC4", "LPLC2"), "L"),
        "loom_R": _pick(cell_type, side_np, ("LC4", "LPLC2"), "R"),
        "goal_L": _pick(cell_type, side_np, ("LC10a",), "L"),
        "goal_R": _pick(cell_type, side_np, ("LC10a",), "R"),
        "steer_low_L": _pick(cell_type, side_np, ("DNa01",), "L"),
        "steer_low_R": _pick(cell_type, side_np, ("DNa01",), "R"),
        "steer_high_L": _pick(cell_type, side_np, ("DNa02",), "L"),
        "steer_high_R": _pick(cell_type, side_np, ("DNa02",), "R"),
        "forward_L": _pick(cell_type, side_np, ("DNg100",), "L"),
        "forward_R": _pick(cell_type, side_np, ("DNg100",), "R"),
        "escape_L": _pick(cell_type, side_np, ("DNp01",), "L"),
        "escape_R": _pick(cell_type, side_np, ("DNp01",), "R"),
        "backward_L": _pick(cell_type, side_np, ("MDN",), "L"),
        "backward_R": _pick(cell_type, side_np, ("MDN",), "R"),
        "sensory": sensory,
    }

    orn_mask = np.char.startswith(cell_type.astype(str), "ORN_")
    groups["food_L"] = np.flatnonzero(orn_mask & (side_np == "L")).astype(np.int32)
    groups["food_R"] = np.flatnonzero(orn_mask & (side_np == "R")).astype(np.int32)

    required = ["loom_L", "loom_R", "steer_high_L", "steer_high_R", "forward_L", "forward_R"]
    missing = [name for name in required if len(groups[name]) == 0]
    if missing:
        raise RuntimeError("Required MaleCNS groups resolved to zero neurons: " + ", ".join(missing))

    for name, idx in groups.items():
        print(f"  {name:15s}: {len(idx):6d}")

    sparse.save_npz(data_dir / "malecns_weights.npz", W, compressed=False)
    np.savez(data_dir / "malecns_meta.npz", ids=ids, **{f"group_{name}": idx for name, idx in groups.items()})

    manifest = {
        "dataset": "male-cns:v1.0",
        "source": "HHMI Janelia FlyEM Male CNS",
        "neurons": int(n),
        "connections": int(W.nnz),
        "model_note": "Wiring/weights are MaleCNS v1.0; LIF dynamics and game sensor/motor mappings are FlyMaze modeling choices.",
        "groups": {name: int(len(idx)) for name, idx in groups.items()},
    }
    (data_dir / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"[MaleCNS] Build complete: {data_dir}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True, help="Output/cache directory, e.g. Unity Library/MaleCNS")
    args = parser.parse_args()
    try:
        build(Path(args.data).resolve())
        return 0
    except KeyboardInterrupt:
        print("\n[MaleCNS] Cancelled")
        return 130
    except Exception as exc:
        print(f"\n[MaleCNS] ERROR: {exc}", file=sys.stderr)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
