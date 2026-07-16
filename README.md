# Penumbra

**Write math by hand. Watch it answer in your own handwriting.**

Penumbra is a local, offline math notebook. Sketch an equation with your pen and it recognizes your
handwriting, solves it with a real computer-algebra system, and writes the answer back in *your* hand —
no cloud, no account, everything on your device.

![Penumbra writes an answer in handwriting](assets/hero.gif)

## Download

**Latest release:** `v0.0.5` for Windows x64.

Download `Penumbra-v0.0.5-win-x64.zip` from
[Releases](https://github.com/aeyvmor/penumbra/releases/latest), unzip it, and run
`Penumbra.App.exe`.

Windows may warn because this early build is not signed yet.

## What Works Today

Penumbra is in active early development. The latest packaged release is v0.0.5; public `main` may
contain newer milestone work before it receives a release tag. Expect rough edges.

- Write a single-line expression ending in `=` (for example `2+2=`, `21+7=`, `4+1-9=`) — lift the
  pen, and a beat later the answer writes itself from the `=` in animated handwriting. No button.
- Symbols Penumbra isn't sure about desaturate and shiver; when it can't read a line at all, it
  says so instead of guessing (the recognizer is confidence-calibrated and refuses non-math ink).
- Tap an answer to highlight the exact strokes it came from.
- Put definitions and queries on separate lines (`x=5`, `y=x+2`, `y+1=`): changing one value
  re-solves downstream answers and shows the dependency ripple.
- Erase individual strokes, undo/redo one gesture at a time, and save/reopen `.pen` v4 pages. Crash-safe
  autosave and a local recovery checkpoint protect interrupted work.
- Hold an answer to drag and stamp it elsewhere as ordinary ink. Hold a numeric literal and drag
  horizontally to preview dependent values as reversible "taffy" ghost ink.
- Write practical spatial math: powers, drawn fractions, square roots, brackets, functions, implicit
  products, and nested combinations are parsed into a stroke-owned layout tree. Ambiguous structures
  are refused instead of flattened into plausible-looking wrong math.
- Solve a one-variable equation on one line and ask for it later (`2x=4`, then `x=`). Penumbra carries
  forward only a single verified solution; it does not guess when an equation has multiple answers.
- Write explicit functions such as `y=x`, `y=x^2`, and `y=sin(x)` to plot them in a live side panel.
  Multiple curves, pan/zoom resampling, discontinuity gaps, and a crosshair are included.
- Penumbra passively learns your digits/operators as you use it; missing symbols fall back to the
  bundled Caveat handwriting font.
- Everything runs offline on your machine.

Recognition works best with clearly separated symbols and one expression per horizontal line. Indexed
roots, general subscripts, dense or genuinely ambiguous layouts, implicit curves, inequality shading, and
the tutor remain future work. This is an unsigned early build; keep important notebooks backed up.

## Building

Requires the **.NET SDK 8.0+** and **Git LFS**. The recognizer model ships via LFS.

```bash
git lfs install
git clone https://github.com/aeyvmor/penumbra.git
cd penumbra
dotnet test Penumbra.sln
dotnet run --project src/Penumbra.App
```

## License

[MIT](LICENSE).

The shipped recognizer weights have their own provenance note in
[`src/Penumbra.Recognition/Models/MODELCARD.md`](src/Penumbra.Recognition/Models/MODELCARD.md).

## Acknowledgements

Penumbra grew from an earlier C++ proof-of-concept. Inspired by Apple's Math Notes and by the original
Python concept from Ayush Pai. Handwriting font: *Caveat* (Google Fonts).
