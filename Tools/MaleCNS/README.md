# FlyMaze MaleCNS v1.0 bridge

This folder integrates the **HHMI Janelia MaleCNS v1.0** connectome into the Unity maze demo.

## First Play / automatic setup

A fresh clone does not commit the ~1.2 GB MaleCNS dataset. On the **first Play**, `MaleCNSBridge` now detects a missing local cache and automatically launches the setup process. Unity shows a blocking progress overlay while it creates `Library/MaleCNS/venv`, installs the Python packages, downloads the official MaleCNS v1.0 flat-connectome tables, and builds the runtime sparse graph.

Requirements:

- Python 3 must be installed and available as `py` or `python` on Windows.
- Internet access is required for the first setup.
- Partial downloads are resumable.
- Generated data stays under `Library/MaleCNS` and is not committed to Git.

The manual fallback still exists in Unity:

`Fly Maze > MaleCNS > Setup v1.0`

Official dataset page: https://male-cns.janelia.org/
Official download page: https://male-cns.janelia.org/download/
Dataset: `male-cns:v1.0`

## What is real vs modeled

**From MaleCNS v1.0:** neuron IDs, directed synaptic connections, synapse-count connection strengths, cell-type annotations, laterality, and aggregate neurotransmitter predictions.

**FlyMaze modeling choices:** leaky-integrate-and-fire dynamics, numerical normalization, background activity, sensory encoding, descending-neuron motor decoding, reinforcement pulse amplitude, and the KC→MBON plasticity rule described below.

Current input/output populations include:

- `LC4` + `LPLC2`: looming-sensitive visual populations for local wall proximity.
- `ORN_*`: olfactory receptor populations for local left/right food odor sampling.
- `LC10a`: final goal visual cue.
- `DNa02`: higher-gain steering readout.
- `DNa01`: lower-gain steering readout.
- `DNg100`: forward locomotor readout.
- `DNp01`: escape-related readout.
- PAM dopamine neurons: positive reinforcement pulse.
- PPL1 dopamine neurons: aversive reinforcement pulse.

Unity does **not** send A*, BFS, maze routes, waypoints, or a scripted turn toward food. It only converts the local environment into sensory signals and applies the resulting MaleCNS descending-neuron motor output.

## Persistent KC→MBON learning

FlyMaze now keeps a decaying eligibility trace of recently active Kenyon cells. A food/goal reward injects a PAM dopamine event, and repeated wall failure injects a PPL1 aversive event. Each dopamine event gates depression of eligible KC→MBON synapses in MBON rows most strongly associated with that DAN population in the loaded connectome.

The learned delta weights are stored at:

`Library/MaleCNS/kc_mbon_plasticity_v1.npz`

They survive maze regeneration, network reset, stopping Play mode, and later Play sessions on the same machine. They are intentionally local and are not committed to Git. The neural learning HUD displays the number of modified KC→MBON synapses and the total absolute delta weight.

This plasticity algorithm is an experimental simulation rule built around real MaleCNS neuron identities and wiring; it is not a claim that the numerical learning rule exactly reproduces biological dopamine-dependent plasticity.
