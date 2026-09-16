#!/usr/bin/env python3
"""Line-oriented MaleCNS v1.0 simulation bridge for FlyMaze.

Protocol (stdin -> stdout):
  S|front|left|right|targetBearing|targetStrength|targetKind
where targetKind is 0=food, 1=goal, -1=none.

Outputs:
  READY|neurons|connections
  M|forward|turn|spikes|dNa02L|dNa02R|escape|dNa01L|dNa01R|forwardL|forwardR

The wiring and synapse-count weights come from MaleCNS v1.0. The LIF dynamics,
stimulus injection, sensory clamping, and motor readout are intentionally simple
engineering choices for an interactive demo, not claims of biological fidelity.
"""
from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

import numpy as np
from scipy import sparse


class MaleCNSRuntime:
    DT = 0.020
    TAU = 0.100
    DECAY = np.float32(math.exp(-DT / TAU))
    GAIN = np.float32(3.0)
    TONIC = np.float32(0.115)
    THRESHOLD = np.float32(1.0)
    REST_VOLTAGE = np.float32(0.63)

    def __init__(self, data_dir: Path):
        weights_path = data_dir / "malecns_weights.npz"
        meta_path = data_dir / "malecns_meta.npz"
        if not weights_path.exists() or not meta_path.exists():
            raise FileNotFoundError(
                f"MaleCNS cache missing in {data_dir}. Run Tools/MaleCNS/setup-malecns.ps1 first."
            )

        self.W = sparse.load_npz(weights_path).tocsr().astype(np.float32)
        meta = np.load(meta_path)
        self.n = self.W.shape[0]
        self.groups = {
            key.removeprefix("group_"): meta[key].astype(np.int32)
            for key in meta.files
            if key.startswith("group_")
        }

        self.v = np.zeros(self.n, np.float32)
        self.spikes = np.zeros(self.n, np.float32)
        self.rng = np.random.default_rng(20260916)
        self.noise_probability = np.float32(0.006)
        self.noise_amplitude = np.float32(0.14)

        self.turn_ema = 0.0
        self.forward_ema = 0.0
        self.escape_ema = 0.0

    def reset(self):
        self.v.fill(0)
        self.spikes.fill(0)
        self.turn_ema = 0.0
        self.forward_ema = 0.0
        self.escape_ema = 0.0

    def _stim(self, group: str, amount: float):
        if amount <= 0:
            return
        idx = self.groups.get(group)
        if idx is None or len(idx) == 0:
            return
        # Divide gently by sqrt(population) so huge ORN groups do not dominate merely by count.
        self.v[idx] += np.float32(amount / max(1.0, math.sqrt(len(idx)) * 0.12))

    def _group_rate(self, group: str, fired: np.ndarray) -> float:
        idx = self.groups.get(group)
        if idx is None or len(idx) == 0 or len(fired) == 0:
            return 0.0
        return float(np.intersect1d(idx, fired, assume_unique=False).size) / float(len(idx))

    def _group_voltage(self, group: str) -> float:
        """Continuous sub-threshold activity for tiny descending-neuron groups.

        Several motor groups contain only one neuron per side. Reading only spikes makes a
        game controller extremely sparse, so we also expose voltage above the network's tonic
        resting level. Left/right differences still come from the real MaleCNS wiring.
        """
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

        # Two 20 ms network updates per Unity command. Across successive commands recurrent
        # activity continues to propagate through the full 166k-neuron graph.
        for _ in range(2):
            syn = self.W.dot(self.spikes) * self.GAIN
            self.v *= self.DECAY
            self.v += syn + self.TONIC

            noise_mask = self.rng.random(self.n) < self.noise_probability
            self.v[noise_mask] += self.noise_amplitude

            # Prevent receptor feedback from saturating new game sensory input.
            sensory = self.groups.get("sensory")
            if sensory is not None and len(sensory):
                self.v[sensory] = 0.0

            # Looming / wall proximity -> MaleCNS looming-sensitive visual populations.
            self._stim("loom_L", 1.00 * left + 0.62 * front)
            self._stim("loom_R", 1.00 * right + 0.62 * front)

            # Food direction -> olfactory receptor populations. Goal -> LC10a visual populations.
            left_bias = max(0.0, -target_bearing)
            right_bias = max(0.0, target_bearing)
            center = 1.0 - abs(target_bearing)
            if target_kind == 0:
                self._stim("food_L", target_strength * (0.22 + 0.90 * left_bias + 0.25 * center))
                self._stim("food_R", target_strength * (0.22 + 0.90 * right_bias + 0.25 * center))
            elif target_kind == 1:
                self._stim("goal_L", target_strength * (0.25 + 0.86 * left_bias + 0.25 * center))
                self._stim("goal_R", target_strength * (0.25 + 0.86 * right_bias + 0.25 * center))

            fired = np.flatnonzero(self.v >= self.THRESHOLD).astype(np.int32)

            # Read tiny descending groups before resetting fired voltages. A spike is combined
            # with sub-threshold voltage so steering does not collapse to isolated one-frame pulses.
            low_l = max(self._group_rate("steer_low_L", fired), self._group_voltage("steer_low_L"))
            low_r = max(self._group_rate("steer_low_R", fired), self._group_voltage("steer_low_R"))
            high_l = max(self._group_rate("steer_high_L", fired), self._group_voltage("steer_high_L"))
            high_r = max(self._group_rate("steer_high_R", fired), self._group_voltage("steer_high_R"))
            fwd_l = max(self._group_rate("forward_L", fired), self._group_voltage("forward_L"))
            fwd_r = max(self._group_rate("forward_R", fired), self._group_voltage("forward_R"))
            esc_l = max(self._group_rate("escape_L", fired), self._group_voltage("escape_L"))
            esc_r = max(self._group_rate("escape_R", fired), self._group_voltage("escape_R"))

            d01_l = max(d01_l, low_l)
            d01_r = max(d01_r, low_r)
            d02_l = max(d02_l, high_l)
            d02_r = max(d02_r, high_r)
            fwd_l_out = max(fwd_l_out, fwd_l)
            fwd_r_out = max(fwd_r_out, fwd_r)
            escape = max(escape, 0.5 * (esc_l + esc_r))

            raw_turn = (high_r - high_l) + 0.38 * (low_r - low_l)
            raw_forward = 0.5 * (fwd_l + fwd_r)
            self.turn_ema = 0.66 * self.turn_ema + 0.34 * raw_turn
            self.forward_ema = 0.74 * self.forward_ema + 0.26 * raw_forward
            self.escape_ema = 0.70 * self.escape_ema + 0.30 * escape

            self.spikes.fill(0)
            if len(fired):
                self.spikes[fired] = 1.0
                self.v[fired] = 0.0
            total_spikes += int(len(fired))

        turn = float(np.tanh(self.turn_ema * 4.4))
        forward = float(np.clip(self.forward_ema * 1.9 + self.escape_ema * 0.22, 0.0, 1.0))
        return (forward, turn, total_spikes, d02_l, d02_r, self.escape_ema,
                d01_l, d01_r, fwd_l_out, fwd_r_out)


def parse_command(line: str):
    parts = line.strip().split("|")
    if not parts:
        return None
    if parts[0] == "Q":
        return "quit"
    if parts[0] == "R":
        return "reset"
    if parts[0] != "S" or len(parts) != 7:
        return None
    return (
        float(parts[1]), float(parts[2]), float(parts[3]),
        float(parts[4]), float(parts[5]), int(parts[6]),
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True)
    args = parser.parse_args()

    runtime = MaleCNSRuntime(Path(args.data).resolve())
    print(f"READY|{runtime.n}|{runtime.W.nnz}", flush=True)

    for line in sys.stdin:
        try:
            cmd = parse_command(line)
            if cmd == "quit":
                break
            if cmd == "reset":
                runtime.reset()
                print("RESET|OK", flush=True)
                continue
            if cmd is None:
                continue
            values = runtime.step_command(*cmd)
            forward, turn, spikes, d02_l, d02_r, escape, d01_l, d01_r, fwd_l, fwd_r = values
            print(
                f"M|{forward:.6f}|{turn:.6f}|{spikes}|{d02_l:.4f}|{d02_r:.4f}|{escape:.4f}|"
                f"{d01_l:.4f}|{d01_r:.4f}|{fwd_l:.4f}|{fwd_r:.4f}",
                flush=True,
            )
        except Exception as exc:
            print(f"ERR|{type(exc).__name__}|{exc}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
