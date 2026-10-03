# Vahan Hardpoints → SolidWorks

A small C# tool that pushes suspension hardpoint coordinates (exported from
[Vahan](https://en.wikipedia.org/wiki/Vehicle_dynamics) VD software, or any tool
that produces the paste format below) into a **SolidWorks part**, by **moving
existing sketch points in place** — so the point entities keep their IDs and
nothing that references them (linkage sketches, mates, in-context features, PDM
links) breaks.

Built for an FSAE team to keep a SolidWorks hardpoint model in sync with
suspension kinematics iterations without rebuilding geometry every time.

---

## The problem it solves

When you re-import updated hardpoints, the naive approach is to delete the old
points and recreate them. In SolidWorks every reference points at a specific
**entity ID**, so delete-and-recreate assigns new IDs and **breaks every
downstream reference**. SolidWorks macros don't help here either — they're
one-shot and can't preserve this cleanly across a real workflow.

This tool does the opposite: it finds each existing point and **moves it**
(`SketchPoint.SetCoords`). The entity is never deleted or recreated, so its ID
is stable and all references survive.

---

## How it works

- Talks to SolidWorks through its official **COM API** (no simulated clicks, no
  macros).
- Attaches to the **already-running** SolidWorks via the Windows **Running
  Object Table (ROT)** and operates on whatever **part is open and active**.
- Reads the pasted/`--data` text, then **validates everything first** — any bad
  number or out-of-range value aborts the whole run and changes nothing.
- **Auto-detects the part's structure:**
  - **Named-sketch part** — one named 3D sketch per hardpoint
    (`FL_uca_front`, `RR_lca_outer`, …): it moves each point in place, and
    **creates a named sketch** for any hardpoint that doesn't exist yet.
  - **Fresh part** — it builds a single `Hardpoints` sketch and stores a
    `name → persistent-reference` map in a custom property so points are
    re-found reliably across save/close/reopen.
- A permanent **`ORIGIN_PT`** at (0,0,0) is always kept.
- It **never saves** — you press **Ctrl+S** yourself (so a bad run is
  throwaway: close without saving).

### Guardrails

- **Read-only / not-checked-out part** → refuses up front instead of silently
  doing nothing.
- **Version-mix warning** → any point in the part but missing from your paste is
  flagged as still holding old values.
- **Won't touch a part it didn't build** (provenance check on the single-sketch
  path).
- `0,0,0` corners (a hardpoint's "not applicable" for that corner) are skipped
  rather than stacked on the origin.

---

## Paste format

One record per hardpoint, records separated by `|`, fields by `,`:

```
name,FLx,FLy,FLz,FRx,FRy,FRz,RLx,RLy,RLz,RRx,RRy,RRz|name,...
```

- Columns `1,2,3` of each corner map straight to **X, Y, Z in millimetres**
  (the tool divides by 1000 — SolidWorks works in metres internally).
- Corners that are `0,0,0` mean "not applicable" for that corner and are skipped.

See [`sample_points.txt`](sample_points.txt) for a full example.

---

## Build

Requires the .NET Framework C# compiler (ships with Windows) and a local
SolidWorks install (for the interop assemblies).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` copies the two `SolidWorks.Interop.*.dll` assemblies out of your
SolidWorks program folder next to the exe and compiles `hp.cs` into
`VahanHardpoints.exe`. (Those interop DLLs are proprietary to SolidWorks and are
intentionally **not** committed here — they come from your own install.)

---

## Use

1. Open your hardpoint part in SolidWorks (if it's in PDM/Kenesto, **check it
   out** so it's writable).
2. Run **`VahanHardpoints.exe`** (a desktop shortcut / hotkey works well).
3. Paste your points into the dialog (it also pre-fills from the clipboard) and
   click **Apply**.
4. Read the result, press **Ctrl+S**, and check in.

Headless / scripting:

```
VahanHardpoints.exe --data=points.txt        # apply a file, show a summary popup
VahanHardpoints.exe --data=points.txt --quiet # apply a file, print instead of popup
```

---

## Notes / caveats

- Updating a part built as many separate named sketches is **not instant** —
  SolidWorks opens/closes each sketch one at a time (~tens of seconds for a full
  car). The single-sketch path is much faster but doesn't fit a part that
  already has a linkage referencing per-point named sketches.
- The tool moves the **hardpoint points**. It does not draw or update your
  linkage/visualization sketch — that follows automatically only where its
  geometry is constrained (coincident/pierce) to the hardpoint points.
- Entity IDs are preserved specifically because points are only ever *moved*,
  never deleted and recreated. Keep it that way: don't delete + rebuild the
  hardpoint sketches by hand.

