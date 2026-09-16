#!/usr/bin/env python3
"""Line-oriented MaleCNS v1.0 simulation bridge for FlyMaze.

Protocol (stdin -> stdout):
  S|front|left|right|lateralSensoryBias|sensoryStrength|targetKind
  E|reward|punishment
  R
  Q

The Unity side does not provide a maze route or waypoint. It provides only local wall/looming
signals and left-vs-right odor/goal sensory bias. Motor output is decoded from MaleCNS
descending neurons.

Reward injects a pulse into identified PAM11 dopamine neurons; punishment injects a pulse into
identified PPL101/PPL1 dopamine neurons. These are engineered reinforcement events applied to
real MaleCNS neuron identities. This runtime does not claim that chemical concentration or
biophysical dopamine release is being reproduced.
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

    def __init__(self, data_dir: Path):
        self.data_dir = data_dir
        weights_path = data_dir / "malecns_weights.npz"
        meta_path = data_dir / "malecns_meta.npz"
        if not weights_path.exists() or not meta_path.exists():
            raise FileNotFoundError(
                f"MaleCNS cache missing in {data_dir}. Run Tools/MaleCNS/setup-malecns.ps1 first."
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
        self._ensure_reinforcement_groups()

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

    def _ensure_reinforcement_groups(self):
        if len(self.groups.get("reward_dan", ())) and len(self.groups.get("punish_dan", ())):
            return

        raw = self.data_dir / "raw"
        annotation = raw / "body-annotations-male-cns-v1.0-minconf-0.5.feather"
        neurotransmitters = raw / "body-neurotransmitters-male-cns-v1.0.feather"
        if not annotation.exists() or not neurotransmitters.exists():
            self.groups.setdefault("reward_dan", np.empty(0, np.int32))
            self.groups.setdefault("punish_dan", np.empty(0, np.int32))
            return

        ann = feather.read_table(annotation).to_pandas()
        nt = feather.read_table(neurotransmitters, columns=["body", "consensus_nt"]).to_pandas()
        if "bodyId" not in ann.columns:
            return

        flywire = ann["flywireType"].fillna("").astype(str) if "flywireType" in ann.columns else ""
        fallback = ann["type"].fillna("").astype(str) if "type" in ann.columns else ""
        instance = ann["instance"].fillna("").astype(str) if "instance" in ann.columns else ""
        if isinstance(flywire, str):
            labels = fallback if not isinstance(fallback, str) else instance
        else:
            labels = flywire.copy()
            if not isinstance(fallback, str):
                labels = labels.where(labels.ne(""), fallback)
            if not isinstance(instance, str):
                labels = labels.where(labels.ne(""), instance)
        labels = labels.fillna("").astype(str).str.upper()

        nt_map = nt.drop_duplicates("body").set_index("body")["consensus_nt"].fillna("").astype(str).str.lower()
        ann_nt = ann["bodyId"].map(nt_map).fillna("").astype(str).str.lower()
        dopamine = ann_nt.eq("dopamine")

        reward_mask = dopamine & labels.str.contains("PAM11", regex=False)
        punish_mask = dopamine & labels.str.contains("PPL101", regex=False)
        if not reward_mask.any():
            reward_mask = dopamine & labels.str.startswith("PAM")
        if not punish_mask.any():
            punish_mask = dopamine & labels.str.startswith("PPL1")

        self.groups["reward_dan"] = self._body_ids_to_indices(ann.loc[reward_mask, "bodyId"].to_numpy(np.int64))
        self.groups["punish_dan"] = self._body_ids_to_indices(ann.loc[punish_mask, "bodyId"].to_numpy(np.int64))

    def _body_ids_to_indices(self, body_ids: np.ndarray) -> np.ndarray:
        if len(body_ids) == 0:
            return np.empty(0, np.int32)
        pos = np.searchsorted(self.ids, body_ids)
        valid = pos < len(self.ids)
        safe = np.minimum(pos, len(self.ids) - 1)
        valid &= self.ids[safe] == body_ids
        return np.unique(pos[valid].astype(np.int32))

    def reset(self):
        self.v.fill(0)
        self.spikes.fill(0)
        self.turn_ema = 0.0
        self.forward_ema = 0.0
        self.escape_ema = 0.0
        self.reward_pulse = 0.0
        self.punishment_pulse = 0.0
        self.pam_ema = 0.0
        self.ppl1_ema = 0.0

    def reinforce(self, reward: float, punishment: float):
        self.reward_pulse = max(self.reward_pulse, float(np.clip(reward, 0.0, 2.0)))
        self.punishment_pulse = max(self.punishment_pulse, float(np.clip(punishment, 0.0, 2.0)))

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

            noise_mask = self.rng.random(self.n) < self.noise_probability
            self.v[noise_mask] += self.noise_amplitude

            sensory = self.groups.get("sensory")
            if sensory is not None and len(sensory):
                self.v[sensory] = 0.0

            # Local optic/looming input only.
            self._stim("loom_L", 1.22 * left + 0.78 * front)
            self._stim("loom_R", 1.22 * right + 0.78 * front)

            # target_bearing is not a route angle; Unity derives it from the difference between
            # two local antenna/cue samples. This lateralized sensory signal is injected into the
            # real receptor/projection populations.
            left_bias = max(0.0, -target_bearing)
            right_bias = max(0.0, target_bearing)
            center = 1.0 - abs(target_bearing)
            if target_kind == 0:
                self._stim("food_L", target_strength * (0.24 + 1.05 * left_bias + 0.20 * center))
                self._stim("food_R", target_strength * (0.24 + 1.05 * right_bias + 0.20 * center))
            elif target_kind == 1:
                self._stim("goal_L", target_strength * (0.22 + 0.95 * left_bias + 0.18 * center))
                self._stim("goal_R", target_strength * (0.22 + 0.95 * right_bias + 0.18 * center))

            # Engineered reinforcement event delivered to identified dopamine neurons.
            self._stim("reward_dan", self.reward_pulse * 2.8)
            self._stim("punish_dan", self.punishment_pulse * 3.1)

            fired = np.flatnonzero(self.v >= self.THRESHOLD).astype(np.int32)

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
        return (forward, turn, total_spikes, d02_l, d02_r, self.escape_ema,
                d01_l, d01_r, fwd_l_out, fwd_r_out, max(pam, self.pam_ema), max(ppl1, self.ppl1_ema))


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
                break
            if cmd[0] == "reset":
                runtime.reset()
                print("RESET|OK", flush=True)
                continue
            if cmd[0] == "reinforce":
                runtime.reinforce(cmd[1], cmd[2])
                continue

            values = runtime.step_command(*cmd[1:])
            forward, turn, spikes, d02_l, d02_r, escape, d01_l, d01_r, fwd_l, fwd_r, pam, ppl1 = values
            print(
                f"M|{forward:.6f}|{turn:.6f}|{spikes}|{d02_l:.4f}|{d02_r:.4f}|{escape:.4f}|"
                f"{d01_l:.4f}|{d01_r:.4f}|{fwd_l:.4f}|{fwd_r:.4f}|{pam:.4f}|{ppl1:.4f}",
                flush=True,
            )
        except Exception as exc:
            print(f"ERR|{type(exc).__name__}|{exc}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
