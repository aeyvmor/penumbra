# Model card — `crohme_geo_cnn.onnx` (Penumbra R1)

**What it is:** a per-symbol handwritten-math classifier — a small CNN over a 32×32 rendering of one
symbol's strokes, fused with 5 geometry features (aspect, height/width relative to sibling symbols,
line position, stroke count) so size/baseline context survives normalization. 39 classes (digits, core
operators, brackets, common letters, `\sqrt \pi \sum \int`, Greek, relations). Consumed at runtime by
`OnnxSymbolClassifier`; the preprocessing contract lives in `crohme_geo_cnn.meta.json` and must be
reproduced exactly by any caller.

**Training data & license:** trained on the **CROHME 2013** dataset (isolated symbols extracted from
expression-level InkML). CROHME is distributed **for research purposes only (non-commercial)**.
Accordingly, **these weights are NOT covered by this repository's MIT license** — the MIT grant applies
to the source code. Penumbra is a free/open project; the weights ship so the app runs out of the box.
Do not reuse the weights in commercial products.

**Metrics:** 92.4% test accuracy (CROHME 2013 test split, 4,129 samples), from the geometry-aware
model — up from 90.2% image-only, with the comma class improving 37%→83%. Honesty note: this number
came from best-epoch selection against the same test split (no validation split existed at training
time); treat it as ~0.5% optimistic. Known structural confusions (`x`↔`×`, `1`↔`|`) are resolved
downstream by grammar/context, not by this classifier.

**Provenance:** trained 2026-06-28 with the private `ml/` pipeline (PyTorch → ONNX opset 17,
ir_version 10, torch↔onnxruntime parity verified at export). Inputs: `image` `1×1×32×32` float,
`features` `1×5` float; output: `scores` `1×39` logits.
