# Changelog

All notable changes to MortisPaint are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [0.1.0] — 2026-10-21

First public release.

### Painting

- 11 brushes and 4 erasers:
  - size, opacity, hardness, flow, spacing;
  - paper grain, scatter, jitter, taper, roundness and angle;
  - pressure → size and pressure → opacity (capped by pressure).
- Brushes made from your own images.
- Stabiliser, stylus pressure, stylus eraser end, palm rejection.
- Tools: smudge, blur, paint bucket with manga tones, gradient, eyedropper, shapes, text with fallback fonts, selections (rectangle, ellipse, lasso, magic wand), transform.
- Symmetry (mirror, radial, kaleidoscope) and rulers (parallel, concentric, radial, one-point perspective).
- 17 filters with a live preview computed in the background.

### Layers

- Unlimited layers, nested folders and pass-through groups.
- Drag and drop, multi-selection.
- 17 blend modes, clipping, alpha lock.
- Merge down, merge visible, flatten.

### Animation and timelapse

- ibisPaint-style animation mode (one top-level folder per frame), onion skins, 2–24 fps, GIF export.
- Automatic timelapse recording, exported as GIF.

### Files

- Gallery with automatic, atomic saves (`.mpaint`).
- Layered PSD import and export, compatible with ibisPaint and Photoshop (folders included).
- PNG, JPEG and GIF export.
- Photos imported upright (EXIF orientation).

### Platforms

- Windows desktop build (self-contained, no .NET install needed).
- Android head (not yet published).

### Quality

- Per-feature audit with every finding fixed and covered by tests (see [docs/AUDIT.md](docs/AUDIT.md)).

[0.1.0]: https://github.com/Nekuzaky/MortisPaint/releases/tag/v0.1.0
