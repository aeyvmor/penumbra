# tests/

xUnit test projects, one per engine: `Penumbra.Core.Tests`, `Penumbra.Cas.Tests`,
`Penumbra.Recognition.Tests`, etc.

Per the **de-risking principle**, every engine's logic core is tested **headless** (no UI) before it gets
a UI. Priorities:

- `Penumbra.Cas.Tests` — golden-file table for the **LaTeX → AngouriMath** translator; evaluation correctness.
- `Penumbra.Recognition.Tests` — recognizer output on a fixed stroke-sample set.
- Dependency-graph re-evaluation (Pillar 2) — proven by unit tests before any ink/UI exists.
