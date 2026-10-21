# MortisPaint audit — 2026-10-21

Seven read-only audits, one per functional area, run on the `feat/mortis-style-et-outils` branch. Findings duplicated across areas are merged.

- **Verified**: re-read in the code.
- **Reproduced**: reproduced by a test.
- Other findings come straight from the audits.

Severity:

- **Blocker**: crash, data loss, or a feature that does not work.
- **Major**: wrong result, possible loss, or heavy cost.
- **Minor**.

UI labels are quoted in French, as they appear in the app.

## Fixes (`fix/audit` branch)

Every finding below is fixed:

- Before each fix, a test reproduced the defect on the original code.
- After the fix, the same test passes.

On the integrated code, every suite passes:

| Suite | Coverage | Result |
|---|---|---|
| UI harness (Avalonia.Headless) | full scenario, layer drag and drop, 9 view tests | all pass |
| Canvas gestures | stylus and palm, pinches, touch tools, Shift, input lock | 83 scenarios |
| Brush engine | symmetry, opacity cap, spacing, blur, ruler | 36 checks |
| Layers | structure, rendering, undo/redo, 6,000 random operations | 100 checks |
| Filters and text | alpha lock, edges, async preview, fallback fonts | 45 checks |
| Animation and timelapse | GIF, palette, frame rate, cap, thumbnails | all pass |
| Files | hostile PSDs (10,000 damaged files), round trips, atomic saves, EXIF, gallery | all pass |

The 19 psd-tools test PSDs render byte-for-byte identically to before the fixes.

Still open:

- **Android keyboard.** The padding that keeps the Text panel above the on-screen keyboard has not been tried on a device.
- **Pass-through group below 100 %.** Merging such a folder gives an approximate result: the folder opacity is applied to each layer.
- **90° rotation.** It replaces the document, so it clears the history (after a confirmation). The 180° rotation goes through the history.
- **Flattened RGB + 1 channel PSD without resource 1053.** It is read as transparent, as psd-tools does. psd-tools is not installed on this machine, so these rules were applied from memory.

## Blockers (5)

| # | Area | Defect | Where | Status |
|---|---|---|---|---|
| B1 | Layers | Drag and drop never moves anything: `Capture(null)` fires `PointerCaptureLost` → `FinishDrag(false)` before the commit. | `EditorView.Layers.cs:366-372` | **reproduced** |
| B2 | Interface | Native crash about 300 ms after closing the editor: the thumbnail timer reads bitmaps that were already freed (e.g. import an image, then ←). | `EditorView.axaml.cs:226`, `Layers.cs:160` | verified (crash reproduced by the audit) |
| B3 | Files | Deleting an artwork recursively erases `Timelapse/<Id>` using the **raw** Id from the manifest. A `.mpaint` with `"Id":".."` or an absolute path wipes the whole gallery or an arbitrary folder. | `AppServices.cs:82-83` | verified |
| B4 | Canvas | Cropping to a selection outside the canvas throws `ArgumentException` in an `async void` handler → crash. | `EditorView.Tools.cs:465`, `CanvasOps.cs:84` | verified |
| B5 | Canvas / brush | A 2nd pointer (palm, finger) during a stylus stroke overwrites the session without ending it. The stroke is lost, its dabs stay in the buffer and are replayed into a later stroke, and `_gesture` goes back to None when the palm lifts. | `CanvasView.cs:531-572, 644, 897-904` | verified (2 audits) |

## Brush engine

- **Major — Symmetry: fixed angle not mirrored.** Without « Suivre la direction » (follow direction), the tip keeps `_b.Angle` instead of the transformed angle, so mirrored calligraphy is not symmetrical. `DabRenderer.cs:113, 145` → use `angle`.
- **Major — Rotated square tip: dirty rect too small** (r+2 instead of r·√2): corners are cut off and leftovers stay in the buffer. `DabRenderer.cs:145-165`.
- **Major — Smudge and blur ignore the alpha lock**: colour spreads into transparent areas. `Sessions.cs:246-251, 307-313`.
- **Major — Minimum spacing of 0.4/zoom**: the stroke depends on the zoom (a thin stroke drawn zoomed out becomes dotted). `StrokePath.cs:130`.
- **Major — "Pressure → opacity" saturates**: alpha accumulates dab after dab (20 % pressure ≈ 84 % opacity). `DabRenderer.cs:59`.
- Minor:
  - first dab of a flat brush at 0° even with « Suivre la direction »;
  - smudge at the canvas edge punches holes in an opaque background;
  - blur is O(d³) and allocates 7 MB per dab at 400 px;
  - smudge and blur keep a fixed diameter despite "Pressure → size";
  - concentric ruler applied before stabilisation;
  - taper not taken into account in the spacing;
  - image tip made from a white PNG capped at 25 %.

## Layers, folders, compositing, history

- **Major — Dropping onto a folder that only holds the dragged layer**: the range is computed after removal, so the layer lands at the very bottom and the parent folder becomes non-contiguous (`DeleteFolder` then deletes other layers). `PaintEngine.Advanced.cs:141-158`.
- **Major — « Fusionner les calques visibles » (merge visible) splits a folder in two**: the merged layer is placed at the root, in the middle of the folder. `Advanced.cs:215-221`.
- **Major — Merging down a clipped layer in a non-Normal mode**: the base alpha is squared, so semi-transparent edges lighten everywhere. `PaintEngine.cs:189-202`.
- **Major — Merging down across folders**: in animation, « Fusionner » (merge) pastes frame 2 into frame 1. `PaintEngine.cs:177-181` → refuse when the folders differ.
- **Major — Merging clipped layers / a folder**: the result is no longer clipped and hidden subfolders are ignored. `Advanced.cs:187-194`.
- **Major — History memory underestimated**:
  - deleted or merged layers kept alive by the history are not counted (out-of-memory risk on Android);
  - « Retourner la toile » (flip canvas) stores 2 full copies per layer.
  - `History.cs:103, 183`.
- **Major — Sampling in animation**: the bucket and the « Toile » (canvas) eyedropper see every frame; the magic wand sees the onion skins. `PaintEngine.cs:345, 383`, `Advanced.cs:325` → `new FrameFilter { Frame = CurrentFrame }`.
- Minor:
  - pending opacity edit (600 ms) not flushed by Ctrl+J, Ctrl+Shift+N, the folder eye or the paper;
  - Undo collapses or expands folders (`Expanded` is restored);
  - grouping a selection that covers a subfolder dissolves it;
  - Flatten and Merge visible stack every frame in animation mode.

## Canvas and tools

- **Major — Instant tools on the first finger**: the bucket, wand, text and eyedropper act before a pinch or a two-finger tap is recognised (pinching with the bucket fills the area). `CanvasView.cs:909-937`.
- **Major — Identity transform committed**: choosing V then switching tools pushes a full-canvas entry (128 MB at 4000²) and evicts the history. `Sessions.cs:503-514`.
- **Major — Commands that bypass `CommitPending`** while a transform or filter is open: flip canvas, fill, select layer content, Ctrl+J, Ctrl+T, Ctrl+Shift+N. Results: a half-flipped layer, or an uncommitted filter that comes back. `EditorView.axaml.cs:536-600, 766-769`.
- **Major — Slow magic wand on a large noisy area** (one rectangle per run + `Simplify`, on the UI thread). `Selection.cs:73-108`.
- Minor:
  - ruler and symmetry handles steal a stroke started within 22 px;
  - degenerate selections (zero height) block all painting;
  - Shift both adds to the selection and constrains to a square;
  - selection changes are not in the history;
  - Ctrl+Z or V in the middle of a stroke;
  - pinch frozen with 3 fingers;
  - transform scale can drop to 0;
  - `SpacePan` stuck if the Space release is lost.

## Files, formats, gallery

- **Major — Silent loss on close**: if `AutoSave` fails (storage full, out of memory), the toast dies with the editor and the document is freed. `MainView.axaml.cs:94-101`, `EditorView.axaml.cs:216`.
- **Major — An imported `.mpaint` is never saved** (`IsDirty` stays false). `GalleryView.axaml.cs:360-365`. *(verified)*
- **Major — Sizes read from files are unbounded** (PSD layer rectangle, channel count, layer count, PNGs in the ZIP): a 200-byte PSD can ask for 8 GB. `PsdFile.cs:354-357, 412-419, 533`, `ProjectIO.cs:178-205`.
- **Major — Uncaught exceptions in `async void` handlers**: deleting with a locked JPEG, exporting to a full disk, exporting the timelapse. `GalleryView.axaml.cs:245, 308`, `EditorView.axaml.cs:287`.
- **Major — PSD "pass" (pass-through) groups rendered isolated**: Multiply shadows in a Photoshop group turn opaque. `PsdFile.cs:383`.
- Minor:
  - folder cycle A→B→A in a `.mpaint` = infinite loop;
  - 4th channel of a flattened PSD taken as transparency;
  - a « Papier » (paper) layer added on every PSD round trip;
  - PSD built entirely in memory (fails above 2 GB, export on the UI thread);
  - photo EXIF orientation ignored;
  - `settings.json` written non-atomically;
  - orphaned timelapses never cleaned up.

## Animation, timelapse, GIF

- **Major — Changing layer does not redraw the canvas in animation** (`SetActive` does not invalidate): 2 frames mixed on screen. `PaintEngine.cs:88-93`. *(verified)*
- **Major — Cannot go back to frame 1 from a background layer** (`GoToFrame` compares against the `CurrentFrame` fallback). `Advanced.cs:233, 267`.
- **Major — Animation GIF export**: every frame composited at full resolution and kept in memory, on the UI thread. `EditorView.Animation.cs:234`.
- **Major — One error stops the timelapse for the whole session** (`await previous` outside the `try`, only `IOException` caught, `bmp` leaks). `Timelapse.cs:39, 57`.
- **Major — 2 full-resolution compositions after every stroke** (frame thumbnail + timelapse capture), and `RebuildTimeline` recreates every button. `Animation.cs:146`, `EditorView.axaml.cs:66`.
- **Major — The timelapse folder grows without limit** (one JPEG per action, never pruned). `Timelapse.cs:33`.
- Minor:
  - thumbnail key ignores the background and layer properties;
  - `OnionSkin=false` saved when closing during playback;
  - timelapse captures show the onion skins;
  - numbering overwritten after a gap;
  - `Clear` not synchronised;
  - frames distorted after a canvas size change;
  - GIF palette taken from box centres (white becomes 252);
  - frame rate rounded (24 fps → 25).
- Verified correct: GIF structure and LZW (Pillow decodes them), onion skins at the edges, `DuplicateFrame` with subfolders.

## Filters, tones, text

- **Major — Filter alpha lock**: `DstIn` squares the alpha over the whole canvas, outside the selection and outside the history. `Filters.cs:438-444`.
- **Major — Slow filter preview**: whole canvas, on the UI thread (halftone, motion blur: several seconds on Android). `Filters.cs:423-446`.
- **Major — Transparent edges** after a Gaussian or motion blur (`Decal`). `Filters.cs:136, 289`.
- **Major — Crop 4 px too large** (2 px margin of `Selection.Bounds`). `Tools.cs:474`.
- **Major — Shortcuts active once focus has left the text field**: Backspace clears the text layer it just created. `EditorView.axaml.cs:743, 796`. *(2 audits)*
- **Major — Android deactivation silently commits** the open filter, text or transform. `MainView.axaml.cs:49`.
- **Major — Canvas size up to 8192²** without a memory guard (the gallery caps at 6000). `Tools.cs:414`.
- Minor:
  - halo around filtered selections;
  - band erased by the mosaic;
  - halftone too dark;
  - text preview inherits the layer opacity;
  - no fallback font (Japanese, emoji);
  - zoom blocked while a filter is open;
  - a neutral filter still creates a history entry.

## Interface

- **Major — Reference window off screen** after a rotation or resize (position only clamped while dragging). `Reference.cs:37-64`.
- Minor:
  - Shift+click selects layers hidden in collapsed folders;
  - `MainView` subscriptions never removed;
  - images (thumbnails, 2048² reference) never freed;
  - Reference button stays active after Back;
  - reference image jumps after a pinch;
  - no accessibility names;
  - « Réinitialiser » (reset) has no effect on an image brush;
  - the Android keyboard may hide the Text panel (Android 15, to be confirmed).

## Verified correct

- View matrix (zoom, rotation, mirror, pinch).
- Touch gestures (two- and three-finger taps, long press).
- Folder contiguity for most operations.
- Restoring through `DocState`.
- Layers freed by the history.
- Compositing (clipping onto a folder, hidden folders, onion skins).
- `CanvasOps` matrices.
- Conformance of the written PSD.
- BGRA/RGBA order.
- `MapRgb`.
- Tones.
- GIF encoder.
- `ReplaceDocument` (no closure over the old engine).
- Responsive layout.
- `HandleBack` order.
