# FlyMaze MaleCNS v1.0 bridge

This folder integrates the **HHMI Janelia MaleCNS v1.0** connectome into the Unity maze demo.

## One-time setup

In Unity choose:

`Fly Maze > MaleCNS > Setup v1.0`

The setup creates a Python virtual environment and cache under `Library/MaleCNS`, then downloads the official flat-connectome tables directly from Janelia's public storage. Expect roughly 1.2 GB of raw downloads plus generated runtime files.

The source dataset is not committed to this repository. MaleCNS is licensed CC-BY; see the official project/download pages for citation requirements.

Official dataset page: https://male-cns.janelia.org/
Official download page: https://male-cns.janelia.org/download/
Dataset used by neuPrint: `male-cns:v1.0`

## What is real vs modeled

**From MaleCNS v1.0:** neuron IDs, directed synaptic connections, synapse-count connection strengths, cell-type annotations, laterality, and aggregate neurotransmitter predictions.

**FlyMaze modeling choices:** leaky-integrate-and-fire dynamics, numerical normalization, background activity, clamping feedback into sensory populations, mapping maze rays/targets into sensory neuron groups, and mapping descending-neuron activity into game movement.

The current input/output channels use biologically motivated populations:

- `LC4` + `LPLC2`: looming-sensitive visual projection neurons for wall proximity.
- `ORN_*`: olfactory receptor neuron populations for food direction.
- `LC10a`: visual target channel used for the final goal marker.
- `DNa02`: higher-gain steering readout.
- `DNa01`: lower-gain steering readout.
- `DNg100`: forward locomotor readout.
- `DNp01`: giant-fiber/escape readout.
- `MDN`: backward walking readout (prepared for later use).

The connectome itself is never trained or edited by FlyMaze. The adapter around it is a game/simulation layer, not a claim that the virtual fly exactly reproduces a living fly.
