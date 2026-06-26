<!--
  This is the CONSUMER-FACING README for the PUBLIC `penumbra` repo.
  It is NOT the workspace README. The publish script copies this file to README.md
  in the public repo. Keep it clean: no internal planning, rebuild history, or roadmap.
-->

# Penumbra

**Write math by hand. Watch it answer — in your own handwriting.**

Penumbra is a local, offline math notebook. Sketch an equation with your pen and it recognizes your
handwriting, solves it with a real computer-algebra system, and writes the answer back in *your* hand —
no cloud, no account, everything on your device.

<!-- TODO: hero GIF — the "writes-itself" answer animation -->

## Features

- **Answers in your own handwriting**, animated as if you wrote them.
- **A real algebra engine** — not a calculator. Symbolic steps, calculus, equation solving.
- **A living page** — change a number and every dependent result re-solves instantly.
- **Graphing** — write `y = f(x)` and see it plotted.
- **A handwriting tutor** that hints and checks your work beside you.
- **Fully offline** — handwriting recognition runs locally.

## Status

Penumbra is in active early development. Expect rough edges.

## Building

Requires the **.NET SDK 8.0+**.

```bash
git clone https://github.com/aeyvmor/penumbra.git
cd penumbra
dotnet run --project src/Penumbra.App
```

## License

[MIT](LICENSE).

## Acknowledgements

Penumbra grew from an earlier C++ proof-of-concept. Inspired by Apple's Math Notes and by the original
Python concept from Ayush Pai. Handwriting font: *Caveat* (Google Fonts).
