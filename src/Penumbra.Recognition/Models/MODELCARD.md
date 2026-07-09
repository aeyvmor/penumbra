# MODELCARD — crohme_geo_cnn (R1 symbol classifier, consolidated retrain)

- **Architecture:** SymbolCNNGeo (`ml/train/model.py`) — 3 conv blocks + geometry-feature fusion head.
- **Inputs:** 32x32 stroke render (unified rasterizer `ml/common/raster.py`, parity-fixed to
  `SymbolPreprocessor.RenderImage`) + 5 geometry features. **Classes:** 49.
- **Training data:** CROHME 2013 (train split, writer-disjoint validation carve; test evaluated once).
  CROHME distribution terms are research/**non-commercial** — these weights are NOT covered by the
  repository's MIT license grant. See ADR-0006 (training-data licensing).
- **Metrics:** val 91.93% (selection set) · test 89.59% (reported once).
- **Seed:** 20260705 · **Source:** `ml/` at commit `b9201a1` · trained on CPU.
- **Decision contract (meta.json):** temperature 1.3080 · energy reject threshold
  -2.7641 · min_confidence 0.5925 ·
  bank_confidence 0.9300.
- **Regeneration:** `python train/train_crohme_geo.py` then `python export/export_crohme_geo_onnx.py`
  (atomic: onnx + meta + parity fixture + this card move together or not at all).
