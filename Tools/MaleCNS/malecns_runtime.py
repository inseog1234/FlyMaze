#!/usr/bin/env python3
"""MaleCNS v1.0 runtime bridge for FlyMaze.

Unity sends only local sensory signals and reinforcement events. No maze route, waypoint,
A*, BFS, or scripted turn is supplied to the connectome.

Persistent learning is an explicit modeling layer: recent Kenyon-cell activity forms an
eligibility trace, PAM/PPL1 dopamine events gate depression of KC->MBON synapses in MBONs
most strongly associated with the corresponding DAN population, and the resulting delta
weights are saved under Library/MaleCNS/kc_mbon_plasticity_v1.npz across Play sessions.
The MaleCNS wiring/neuron identities are real dataset values; the LIF dynamics and plasticity
rule are engineering choices for this interactive experiment, not measured biophysics.
"""
from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

import numpy as np
import pyarrow.feather as feather
from scipy import sparse


class MaleCNSRuntime:
    DT = 0.020
    TAU = 0.100
    DECAY = np.float32(math.exp(-DT / TAU))
    GAIN = np.float32(3.15)
    TONIC = np.float32(0.116)
    THRESHOLD = np.float32(1.0)
    REST_VOLTAGE = np.float32(0.63)

    ELIGIBILITY_DECAY = np.float32(0.965)
    ELIGIBILITY_MIN = np.float32(0.06)
    LEARNING_RATE = np.float32(0.085)
    MAX_DEPRESSION = np.float32(0.68)
    MAX_EDGES_PER_MBON_EVENT = 96

    def __init__(self, data_dir: Path):
        self.data_dir = data_dir
        weights_path = data_dir / "malecns_weights.npz"
        meta_path = data_dir / "malecns_meta.npz"
        if not weights_path.exists() or not meta_path.exists():
            raise FileNotFoundError(
                f"MaleCNS cache missing in {data_dir}. Run setup once first."
            )

        self.W = sparse.load_npz(weights_path).tocsr().astype(np.float32)
        meta = np.load(meta_path)
        self.ids = meta["ids"].astype(np.int64)
        self.n = self.W.shape[0]
        self.groups = {
            key.removeprefix("group_"): meta[key].astype(np.int32)
            for key in meta.files
            if key.startswith("group_")
        }
        self._ensure_annotation_groups()

        self.v = np.zeros(self.n, np.float32)
        self.spikes = np.zeros(self.n, np.float32)
        self.rng = np.random.default_rng(20260916)
        self.noise_probability = np.float32(0.006)
        self.noise_amplitude = np.float32(0.14)

        self.turn_ema = 0.0
        self.forward_ema = 0.0
        self.escape_ema = 0.0
        self.reward_pulse = 0.0
        self.punishment_pulse = 0.0
        self.pam_ema = 0.0
        self.ppl1_ema = 0.0

        self.kc_idx = self.groups.get("kc", np.empty(0, np.int32))
        self.mbon_idx = self.groups.get("mbon", np.empty(0, np.int32))
        self.kc_mask = np.zeros(self.n, dtype=bool)
        if len(self.kc_idx):
            self.kc_mask[self.kc_idx] = True
        self.kc_eligibility = np.zeros(self.n, np.float32)

        self.plasticity_path = self.data_dir / "kc_mbon_plasticity_v1.npz"
        self.kc_mbon_base = sparse.csr_matrix((len(self.mbon_idx), self.n), dtype=np.float32)
        self.plastic_matrix = sparse.csr_matrix((len(self.mbon_idx), self.n), dtype=np.float32)
        self.plastic_changes: dict[tuple[int, int], float] = {}
        self.reward_mbon_rows = np.empty(0, np.int32)
        self.punish_mbon_rows = np.empty(0, np.int32)
        self._prepare_plasticity()

    def _ensure_annotation_groups(self):
        need = any(len(self.groups.get(name, ())) == 0 for name in ("reward_dan", "punish_dan", "kc", "mbon"))
        if not need:
            return

        raw = self.data_dir / "raw"
        annotation = raw / "body-annotations-male-cns-v1.0-minconf-0.5.feather"
        neurotransmitters = raw / "body-neurotransmitters-male-cns-v1.0.feather"
        if not annotation.exists() or not neurotransmitters.exists():
            for name in ("reward_dan", "punish_dan", "kc", "mbon"):
                self.groups.setdefault(name, np.empty(0, np.int32))
            return

        ann = feather.read_table(annotation).to_pandas()
        nt = feather.read_table(neurotransmitters, columns=["body", "consensus_nt"]).to_pandas()
        if "bodyId" not in ann.columns:
            return

        label_columns = []
        for name in ("flywireType", "type", "instance", "class", "subclass", "superclass"):
            if name in ann.columns:
                label_columns.append(ann[name].fillna("").astype(str).str.upper())

        if label_columns:
            combined = label_columns[0].copy()
            for series in label_columns[1:]:
                combined = combined + "|" + series
        else:
            combined = ann["bodyId"].astype(str)

        nt_map = nt.drop_duplicates("body").set_index("body")["consensus_nt"].fillna("").astype(str).str.lower()
        ann_nt = ann["bodyId"].map(nt_map).fillna("").astype(str).str.lower()
        dopamine = ann_nt.eq("dopamine")

        reward_mask = dopamine & combined.str.contains("PAM11", regex=False)
        punish_mask = dopamine & combined.str.contains("PPL101", regex=False)
        if not reward_mask.any():
            reward_mask = dopamine & combined.str.contains("PAM", regex=False)
        if not punish_mask.any():
            punish_mask = dopamine & combined.str.contains("PPL1", regex=False)

        kc_mask = combined.str.contains("KENYON", regex=False)
        kc_mask |= combined.str.contains("|KC", regex=False)
        kc_mask |= combined.str.startswith("KC")

        mbon_mask = combined.str.contains("MBON", regex=False)
        mbon_mask |= combined.str.contains("MUSHROOM BODY OUTPUT", regex=False)

        self.groups["reward_dan"] = self._body_ids_to_indices(ann.loc[reward_mask, "bodyId"].to_numpy(np.int64))
        self.groups["punish_dan"] = self._body_ids_to_indices(ann.loc[punish_mask, "bodyId"].to_numpy(np.int64))
        self.groups["kc"] = self._body_ids_to_indices(ann.loc[kc_mask, "bodyId"].to_numpy(np.int64))
        self.groups["mbon"] = self._body_ids_to_indices(ann.loc[mbon_mask, "bodyId"].to_numpy(np.int64))

    def _body_ids_to_indices(self, body_ids: np.ndarray) -> np.ndarray:
        if len(body_ids) == 0:
            return np.empty(0, np.int32)
        pos = np.searchsorted(self.ids, body_ids)
        valid = pos < len(self.ids)
        safe = np.minimum(pos, len(self.ids) - 1)
        valid &= self.ids[safe] == body_ids
        return np.unique(pos[valid].astype(np.int32))

    def _prepare_plasticity(self):
        if len(self.kc_idx) == 0 or len(self.mbon_idx) == 0:
            print("LEARNING|0|0|KC_OR_MBON_GROUP_MISSING", flush=True)
            return

        base_rows = self.W[self.mbon_idx, :].tocsr()
        coo = base_rows.tocoo()
        keep = self.kc_mask[coo.col] & (coo.data > 0)
        self.kc_mbon_base = sparse.csr_matrix(
            (coo.data[keep].astype(np.float32), (coo.row[keep], coo.col[keep])),
            shape=base_rows.shape,
            dtype=np.float32,
        )
        self.kc_mbon_base.sum_duplicates()

        self.reward_mbon_rows = self._dan_target_rows("reward_dan")
        self.punish_mbon_rows = self._dan_target_rows("punish_dan")
        self._load_plasticity()
        print(
            f"LEARNING|{len(self.kc_idx)}|{len(self.mbon_idx)}|{self.kc_mbon_base.nnz}|"
            f"{len(self.reward_mbon_rows)}|{len(self.punish_mbon_rows)}|{len(self.plastic_changes)}",
            flush=True,
        )

    def _dan_target_rows(self, group: str) -> np.ndarray:
        if len(self.mbon_idx) == 0:
            return np.empty(0, np.int32)
        dan = self.groups.get(group, np.empty(0, np.int32))
        if len(dan) == 0:
            return np.arange(len(self.mbon_idx), dtype=np.int32)

        sub = self.W[self.mbon_idx, :][:, dan]
        drive = np.asarray(np.abs(sub).sum(axis=1)).ravel()
        positive = np.flatnonzero(drive > 0)
        if len(positive) == 0:
            return np.arange(len(self.mbon_idx), dtype=np.int32)

        keep_count = min(len(positive), max(4, int(math.ceil(len(positive) * 0.35))))
        order = positive[np.argsort(drive[positive])[-keep_count:]]
        return np.sort(order.astype(np.int32))

    def _load_plasticity(self):
        if not self.plasticity_path.exists() or len(self.mbon_idx) == 0:
            self._rebuild_plastic_matrix()
            return
        try:
            data = np.load(self.plasticity_path)
            saved_mbon = data["mbon_body_ids"].astype(np.int64)
            current_mbon = self.ids[self.mbon_idx]
            if len(saved_mbon) != len(current_mbon) or not np.array_equal(saved_mbon, current_mbon):
                print("[MaleCNS] Ignoring incompatible KC->MBON plasticity cache", file=sys.stderr, flush=True)
                self._rebuild_plastic_matrix()
                return

            rows = data["rows"].astype(np.int32)
            cols = data["cols"].astype(np.int32)
            values = data["delta"].astype(np.float32)
            valid = (rows >= 0) & (rows < len(self.mbon_idx)) & (cols >= 0) & (cols < self.n)
            for r, c, v in zip(rows[valid], cols[valid], values[valid]):
                if abs(float(v)) > 1e-9:
                    self.plastic_changes[(int(r), int(c))] = float(v)
            self._rebuild_plastic_matrix()
        except Exception as exc:
            print(f"[MaleCNS] Plasticity load warning: {exc}", file=sys.stderr, flush=True)
            self.plastic_changes.clear()
            self._rebuild_plastic_matrix()

    def _save_plasticity(self):
        if len(self.mbon_idx) == 0:
            return
        try:
            if self.plastic_changes:
                keys = list(self.plastic_changes.keys())
                rows = np.fromiter((k[0] for k in keys), dtype=np.int32)
                cols = np.fromiter((k[1] for k in keys), dtype=np.int32)
                values = np.fromiter((self.plastic_changes[k] for k in keys), dtype=np.float32)
            else:
                rows = np.empty(0, np.int32)
                cols = np.empty(0, np.int32)
                values = np.empty(0, np.float32)
            np.savez_compressed(
                self.plasticity_path,
                dataset=np.array(["male-cns:v1.0"]),
                mbon_body_ids=self.ids[self.mbon_idx],
                rows=rows,
                cols=cols,
                delta=values,
            )
        except Exception as exc:
            print(f"[MaleCNS] Plasticity save warning: {exc}", file=sys.stderr, flush=True)

    def _rebuild_plastic_matrix(self):
        if len(self.mbon_idx) == 0 or not self.plastic_changes:
            self.plastic_matrix = sparse.csr_matrix((len(self.mbon_idx), self.n), dtype=np.float32)
            return
        keys = list(self.plastic_changes.keys())
        rows = np.fromiter((k[0] for k in keys), dtype=np.int32)
        cols = np.fromiter((k[1] for k in keys), dtype=np.int32)
        values = np.fromiter((self.plastic_changes[k] for k in keys), dtype=np.float32)
        self.plastic_matrix = sparse.csr_matrix(
            (values, (rows, cols)), shape=(len(self.mbon_idx), self.n), dtype=np.float32
        )
        self.plastic_matrix.sum_duplicates()

    def _seed_eligibility_from_voltage(self):
        if len(self.kc_idx) == 0:
            return
        current = self.kc_eligibility[self.kc_idx]
        if np.any(current > self.ELIGIBILITY_MIN):
            return
        voltage = np.clip(self.v[self.kc_idx], 0.0, 1.0)
        if not np.any(voltage > 0):
            return
        take = min(64, len(self.kc_idx))
        if take <= 0:
            return
        local = np.argpartition(voltage, -take)[-take:]
        chosen = self.kc_idx[local]
        self.kc_eligibility[chosen] = np.maximum(self.kc_eligibility[chosen], voltage[local].astype(np.float32))

    def _apply_plasticity(self, target_rows: np.ndarray, amount: float):
        if amount <= 0 or len(target_rows) == 0 or self.kc_mbon_base.nnz == 0:
            return

        self._seed_eligibility_from_voltage()
        changed = False
        for row_index in target_rows:
            row = self.kc_mbon_base.getrow(int(row_index))
            if row.nnz == 0:
                continue
            cols = row.indices
            base = row.data
            eligibility = self.kc_eligibility[cols]
            candidates = np.flatnonzero(eligibility > self.ELIGIBILITY_MIN)
            if len(candidates) == 0:
                continue

            score = eligibility[candidates] * np.abs(base[candidates])
            if len(candidates) > self.MAX_EDGES_PER_MBON_EVENT:
                top = np.argpartition(score, -self.MAX_EDGES_PER_MBON_EVENT)[-self.MAX_EDGES_PER_MBON_EVENT:]
                candidates = candidates[top]

            for i in candidates:
                col = int(cols[i])
                base_weight = float(base[i])
                if base_weight <= 0:
                    continue
                key = (int(row_index), col)
                current = self.plastic_changes.get(key, 0.0)
                step = float(self.LEARNING_RATE) * amount * float(eligibility[i]) * base_weight
                floor = -float(self.MAX_DEPRESSION) * base_weight
                updated = max(floor, current - step)
                if abs(updated - current) > 1e-10:
                    self.plastic_changes[key] = updated
                    changed = True

        if changed:
            self._rebuild_plastic_matrix()
            self._save_plasticity()

    @property
    def learned_synapses(self) -> int:
        return len(self.plastic_changes)

    @property
    def plasticity_magnitude(self) -> float:
        if not self.plastic_changes:
            return 0.0
        return float(sum(abs(v) for v in self.plastic_changes.values()))

    def reset(self):
        self.v.fill(0)
        self.spikes.fill(0)
        self.kc_eligibility.fill(0)
        self.turn_ema = 0.0
        self.forward_ema = 0.0
        self.escape_ema = 0.0
        self.reward_pulse = 0.0
        self.punishment_pulse = 0.0
        self.pam_ema = 0.0
        self.ppl1_ema = 0.0
        # Persistent KC->MBON deltas intentionally survive Reset and Play sessions.

    def reinforce(self, reward: float, punishment: float):
        reward = float(np.clip(reward, 0.0, 2.0))
        punishment = float(np.clip(punishment, 0.0, 2.0))
        self.reward_pulse = max(self.reward_pulse, reward)
        self.punishment_pulse = max(self.punishment_pulse, punishment)
        if reward > 0:
            self._apply_plasticity(self.reward_mbon_rows, reward)
        if punishment > 0:
            self._apply_plasticity(self.punish_mbon_rows, punishment)

    def _stim(self, group: str, amount: float):
        if amount <= 0:
            return
        idx = self.groups.get(group)
        if idx is None or len(idx) == 0:
            return
        self.v[idx] += np.float32(amount / max(1.0, math.sqrt(len(idx)) * 0.12))

    def _group_rate(self, group: str, fired: np.ndarray) -> float:
        idx = self.groups.get(group)
        if idx is None or len(idx) == 0 or len(fired) == 0:
            return 0.0
        return float(np.intersect1d(idx, fired, assume_unique=False).size) / float(len(idx))

    def _group_voltage(self, group: str) -> float:
        idx = self.groups.get(group)
        if idx is None or len(idx) == 0:
            return 0.0
        mean_v = float(np.mean(self.v[idx]))
        return float(np.clip((mean_v - float(self.REST_VOLTAGE)) / (1.0 - float(self.REST_VOLTAGE)), 0.0, 1.0))

    def step_command(self, front: float, left: float, right: float,
                     target_bearing: float, target_strength: float, target_kind: int):
        front = float(np.clip(front, 0.0, 1.0))
        left = float(np.clip(left, 0.0, 1.0))
        right = float(np.clip(right, 0.0, 1.0))
        target_bearing = float(np.clip(target_bearing, -1.0, 1.0))
        target_strength = float(np.clip(target_strength, 0.0, 1.0))

        total_spikes = 0
        d02_l = d02_r = d01_l = d01_r = 0.0
        fwd_l_out = fwd_r_out = 0.0
        escape = 0.0
        pam = ppl1 = 0.0

        for _ in range(2):
            syn = self.W.dot(self.spikes) * self.GAIN
            self.v *= self.DECAY
            self.v += syn + self.TONIC

            if self.plastic_matrix.nnz:
                learned_input = self.plastic_matrix.dot(self.spikes) * self.GAIN
                self.v[self.mbon_idx] += learned_input

            noise_mask = self.rng.random(self.n) < self.noise_probability
            self.v[noise_mask] += self.noise_amplitude

            sensory = self.groups.get("sensory")
            if sensory is not None and len(sensory):
                self.v[sensory] = 0.0

            self._stim("loom_L", 1.22 * left + 0.78 * front)
            self._stim("loom_R", 1.22 * right + 0.78 * front)

            left_bias = max(0.0, -target_bearing)
            right_bias = max(0.0, target_bearing)
            center = 1.0 - abs(target_bearing)
            if target_kind == 0:
                self._stim("food_L", target_strength * (0.24 + 1.05 * left_bias + 0.20 * center))
                self._stim("food_R", target_strength * (0.24 + 1.05 * right_bias + 0.20 * center))
            elif target_kind == 1:
                self._stim("goal_L", target_strength * (0.22 + 0.95 * left_bias + 0.18 * center))
                self._stim("goal_R", target_strength * (0.22 + 0.95 * right_bias + 0.18 * center))

            self._stim("reward_dan", self.reward_pulse * 2.8)
            self._stim("punish_dan", self.punishment_pulse * 3.1)

            fired = np.flatnonzero(self.v >= self.THRESHOLD).astype(np.int32)

            self.kc_eligibility *= self.ELIGIBILITY_DECAY
            if len(fired) and len(self.kc_idx):
                fired_kc = fired[self.kc_mask[fired]]
                if len(fired_kc):
                    self.kc_eligibility[fired_kc] = np.minimum(
                        1.0, self.kc_eligibility[fired_kc] + np.float32(0.58)
                    )

            low_l = max(self._group_rate("steer_low_L", fired), self._group_voltage("steer_low_L"))
            low_r = max(self._group_rate("steer_low_R", fired), self._group_voltage("steer_low_R"))
            high_l = max(self._group_rate("steer_high_L", fired), self._group_voltage("steer_high_L"))
            high_r = max(self._group_rate("steer_high_R", fired), self._group_voltage("steer_high_R"))
            fwd_l = max(self._group_rate("forward_L", fired), self._group_voltage("forward_L"))
            fwd_r = max(self._group_rate("forward_R", fired), self._group_voltage("forward_R"))
            esc_l = max(self._group_rate("escape_L", fired), self._group_voltage("escape_L"))
            esc_r = max(self._group_rate("escape_R", fired), self._group_voltage("escape_R"))
            pam_now = max(self._group_rate("reward_dan", fired), self._group_voltage("reward_dan"))
            ppl_now = max(self._group_rate("punish_dan", fired), self._group_voltage("punish_dan"))

            d01_l = max(d01_l, low_l)
            d01_r = max(d01_r, low_r)
            d02_l = max(d02_l, high_l)
            d02_r = max(d02_r, high_r)
            fwd_l_out = max(fwd_l_out, fwd_l)
            fwd_r_out = max(fwd_r_out, fwd_r)
            escape = max(escape, 0.5 * (esc_l + esc_r))
            pam = max(pam, pam_now)
            ppl1 = max(ppl1, ppl_now)

            raw_turn = (high_r - high_l) + 0.42 * (low_r - low_l)
            raw_forward = 0.5 * (fwd_l + fwd_r)
            self.turn_ema = 0.63 * self.turn_ema + 0.37 * raw_turn
            self.forward_ema = 0.72 * self.forward_ema + 0.28 * raw_forward
            self.escape_ema = 0.68 * self.escape_ema + 0.32 * escape
            self.pam_ema = 0.72 * self.pam_ema + 0.28 * pam_now
            self.ppl1_ema = 0.72 * self.ppl1_ema + 0.28 * ppl_now

            self.spikes.fill(0)
            if len(fired):
                self.spikes[fired] = 1.0
                self.v[fired] = 0.0
            total_spikes += int(len(fired))

            self.reward_pulse *= 0.62
            self.punishment_pulse *= 0.62
            if self.reward_pulse < 0.005:
                self.reward_pulse = 0.0
            if self.punishment_pulse < 0.005:
                self.punishment_pulse = 0.0

        turn = float(np.tanh(self.turn_ema * 4.8))
        forward = float(np.clip(self.forward_ema * 2.0 + self.escape_ema * 0.16, 0.0, 1.0))
        return (
            forward, turn, total_spikes, d02_l, d02_r, self.escape_ema,
            d01_l, d01_r, fwd_l_out, fwd_r_out,
            max(pam, self.pam_ema), max(ppl1, self.ppl1_ema),
            self.learned_synapses, self.plasticity_magnitude,
        )


def parse_command(line: str):
    parts = line.strip().split("|")
    if not parts:
        return None
    if parts[0] == "Q":
        return ("quit",)
    if parts[0] == "R":
        return ("reset",)
    if parts[0] == "E" and len(parts) == 3:
        return ("reinforce", float(parts[1]), float(parts[2]))
    if parts[0] == "S" and len(parts) == 7:
        return (
            "sensory",
            float(parts[1]), float(parts[2]), float(parts[3]),
            float(parts[4]), float(parts[5]), int(parts[6]),
        )
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True)
    args = parser.parse_args()

    runtime = MaleCNSRuntime(Path(args.data).resolve())
    print(f"READY|{runtime.n}|{runtime.W.nnz}", flush=True)

    for line in sys.stdin:
        try:
            cmd = parse_command(line)
            if cmd is None:
                continue
            if cmd[0] == "quit":
                runtime._save_plasticity()
                break
            if cmd[0] == "reset":
                runtime.reset()
                print("RESET|OK", flush=True)
                continue
            if cmd[0] == "reinforce":
                runtime.reinforce(cmd[1], cmd[2])
                continue

            values = runtime.step_command(*cmd[1:])
            (forward, turn, spikes, d02_l, d02_r, escape, d01_l, d01_r,
             fwd_l, fwd_r, pam, ppl1, learned, magnitude) = values
            print(
                f"M|{forward:.6f}|{turn:.6f}|{spikes}|{d02_l:.4f}|{d02_r:.4f}|{escape:.4f}|"
                f"{d01_l:.4f}|{d01_r:.4f}|{fwd_l:.4f}|{fwd_r:.4f}|{pam:.4f}|{ppl1:.4f}|"
                f"{learned}|{magnitude:.6f}",
                flush=True,
            )
        except Exception as exc:
            print(f"ERR|{type(exc).__name__}|{exc}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
