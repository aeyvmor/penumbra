# src/

The .NET solution. **Scaffolded in Phase 0** (see [`../docs/ROADMAP.md`](../docs/ROADMAP.md)) — not yet created.

Planned projects (see [`../docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) for detail and dependency rules):

```text
Penumbra.App           Avalonia UI: views, viewmodels (MVVM), DI wiring
Penumbra.Ink           stroke model, ink canvas control, capture, smoothing
Penumbra.Recognition   ONNX wrapper, preprocessing, segmentation, grammar
Penumbra.Cas           AngouriMath wrapper, LaTeX↔expr, evaluation, variable graph
Penumbra.Graphing      ScottPlot integration, y=f(x) detection
Penumbra.Core          shared models, .pen document + serialization
```

Dependency direction: `App → {Ink, Recognition, Cas, Graphing} → Core`. Engines never reference Avalonia.
