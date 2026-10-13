# Separates one stem from a mix with Demucs. Run by StemSeparator in its own venv; audio passes as raw float32 files
# (channels x frames, planar) at the model's rate, so no audio library or ffmpeg is needed.
import json
import sys
import time

import numpy as np
import torch
from demucs.apply import apply_model
from demucs.pretrained import get_model

job = json.load(open(sys.argv[1], encoding="utf-8"))
started = time.time()
# CUDA (NVIDIA), then MPS (Apple silicon), then the CPU.
if job["device"] != "cpu" and torch.cuda.is_available():
    device = "cuda"
elif job["device"] != "cpu" and torch.backends.mps.is_available():
    device = "mps"
else:
    device = "cpu"
    # Half the cores, so the PC stays usable.
    torch.set_num_threads(job["threads"])
model = get_model(job["model"])
model.eval()
if job["stem"] not in model.sources:
    raise SystemExit(f"The model has no '{job['stem']}' stem; it has {', '.join(model.sources)}.")
mix = torch.from_numpy(np.fromfile(job["input"], dtype=np.float32).reshape(job["channels"], job["frames"]).copy())
# As demucs.separate does: normalise by the mono mix, separate, then undo it.
reference = mix.mean(0)
mean, std = reference.mean(), reference.std() + 1e-8
with torch.no_grad():
    sources = apply_model(model, ((mix - mean) / std)[None], device=device, shifts=job["shifts"], split=True,
                          overlap=0.25, progress=False)[0]
sources = sources * std + mean
stem = sources[model.sources.index(job["stem"])]
stem.numpy().astype(np.float32).tofile(job["stemOutput"])
# The rest is the mix minus the stem, so nothing the separator dropped is lost.
(mix - stem).numpy().astype(np.float32).tofile(job["restOutput"])
print(json.dumps({
    "device": torch.cuda.get_device_name(0) if device == "cuda" else ("Apple silicon GPU" if device == "mps" else f"CPU ({job['threads']} threads)"),
    "sources": model.sources,
    "seconds": time.time() - started,
}))
