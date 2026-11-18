---
name: rfa-generate
description: "Generates a Revit family (.rfa) end to end with the family-editor tools: picks the family template first, grades any source model (Rhino, SAT, 3DM, DWG) as native rebuild, imported geometry or 2D plan outline, asks the user when the source cannot be rebuilt natively (e.g. meshes), then builds, flex-tests, saves and optionally loads the family. Use when the user wants a new family made from a description, dimensions or an existing model. 觸發條件：建立族群、產生族群、生成 RFA、做一個族群、族群建模、Rhino 轉族群、模型轉族群、網格轉族群、選族群樣板、rfa generate、generate family、create rfa、model to family、family from Rhino。"
---

# RFA Generate — Template → Source Grading → Build → Verify

Single entry point for making a new Revit family. It decides **which template** and **which build route** before any geometry is created, and delegates the 2D-outline route to `rhino-furniture-plan` instead of duplicating it.

Method, coordinate conventions and known dead ends live in `domain/rfa-family-authoring.md`. This skill only orchestrates; if the two disagree, the domain file wins.

## Prerequisites

- Revit is running with the MCP service enabled (ribbon), and the `rfa_*` tools are available in this client. If they are not, say so and stop — do not describe a family as built.
- For a Rhino source: Rhino MCP tools are available. Pass `slot` explicitly on every Rhino call (`list_slots` first).
- Every number reported (sizes, face counts, IDs, paths) must come from a tool response in this turn.

## Workflow

### Step 1 — Choose the template (always first)

Decide from the request (and source model, if any), then **show the decision to the user and wait for confirmation** before creating anything.

| Question | Drives |
|---|---|
| Family category — furniture, casework, lighting, mechanical equipment, plumbing fixture, pipe/duct fitting, specialty equipment, generic model, detail item, annotation | Template family |
| Host — none, ceiling, wall, floor, face | Hosted vs. non-hosted template (e.g. a ceiling light needs a ceiling-based template) |
| MEP connectors needed? | Must be an MEP-category template; furniture templates reject duct/pipe connectors (domain 5.15) |
| 2D only? | Detail item / annotation template instead of a 3D one |

Rules:
- Never guess template file names — they differ by Revit language. Call `rfa_new_family` with your best `templateName`; if it fails, pick from the list the error returns, or use `templatePath` (domain 5.7).
- Note the template's origin quirks before placing geometry (domain 5.16: casework centre at X=44.7, ceiling-based lighting `Ceiling` plane at Z=4000). Confirm with `rfa_get_document_info` after creation.
- Ask the user for `savePath`. Never overwrite an existing .rfa without explicit confirmation.

Output of this step (state it to the user):

```text
Template: <name or path>   Category: <...>   Host: <...>   Connectors: <yes/no>   Save to: <path>
```

### Step 2 — Grade the source model

No source model (description or dimensions only) → route **A**, go to Step 3.

With a source model, inspect it first (Rhino: object types, part count, face/vertex counts, bounding size via the export stage or `list_objects` / `run_python` in the user's slot — read-only). Grade it:

| Grade | Route | When it fits | Result |
|---|---|---|---|
| **A** Native rebuild | `rfa_create_extrusion` / `blend` / `revolution` / `sweep` / `swept_blend` | Parts reduce to boxes, constant profiles, lathe shapes, or two-profile transitions; Brep/solids with planar or simple curved faces | Parametric; can flex |
| **B** Imported geometry | `rfa_import_cad` (.3dm preferred for meshes; .sat needs Brep) | Free-form or mesh geometry whose exact shape matters in 3D | Fixed shape, not parametric; can be heavy |
| **C** 2D plan outline | delegate to `rhino-furniture-plan` | Only the plan symbol matters, or B would be too heavy | Symbolic lines only, no 3D body |

Report the grade with the evidence that decided it (e.g. "mesh, ~N faces, organic cushion surfaces → not A").

**If the source cannot be graded A** (meshes, sculpted surfaces, very high face count), do not pick B or C on your own. Ask the user:

> This model can't be rebuilt as native parametric forms (<reason>). Options:
> B — import it as fixed 3D geometry (exact shape, not adjustable, ~N faces);
> C — build a 2D plan-outline family: the outer outline plus boundary lines between parts (e.g. seat / backrest / armrest), so the plan still shows how the parts relate;
> B + C — import the 3D body and add the plan outline, hiding the 3D body in plan views.

Mixed models are allowed: rebuild the simple parts as A (e.g. a tabletop) and import the rest as B.

### Step 3 — Build

**Route A** — follow the standard sequence in domain section 3:
`rfa_get_document_info` → `rfa_set_family_type` (templates often have 0 types) → `rfa_create_material` / `rfa_add_parameter` → `rfa_create_reference_plane` (planes at the current parameter values) → `rfa_create_dimension` (label to parameter, EQ where needed) → `rfa_create_*` forms drawn on the planes → `rfa_align_to_reference_plane`. Add `rfa_set_form_visibility`, `rfa_create_connector` or `rfa_place_nested_family` only when Step 1 called for them.

**Route B** — per domain 5.16:
- In Rhino, put the origin at the bottom centre with the front facing −Y; for meshes write a .3dm in mm (no SAT conversion needed). Run heavy geometry work in a spawned slot, not the user's Rhino.
- Account for the template origin quirks from Step 1 before export.
- `rfa_import_cad` with explicit `units`; keep `thisViewOnly: false` for 3D.

**Route C** — invoke the `rhino-furniture-plan` skill and follow it as written. The plan symbol must show:
- the **outer outline** of the whole object, and
- **part-to-part boundaries** visible from above (one line per visible part boundary),
drawn as separate polylines so the two roles are distinguishable. If the user wants them in different line styles, split the JSON by `role` and call `rfa_create_symbolic_line` twice (outer, then inner), each with its own `subcategory` — in batch form one `subcategory` applies to every line in the call.

**B + C** — do B, then C into the same family; hide the imported body in plan views (the user sets imported-geometry visibility in Revit; `rfa_set_form_visibility` does not cover imports).

### Step 4 — Verify, save, load

| Check | How | Pass |
|---|---|---|
| Size matches source | `sizeMm` / `boundingBoxMm` from the build calls vs. source size | Within a few mm; a 10× or 25.4× gap means wrong units |
| Has a type | `rfa_get_document_info` | ≥ 1 type |
| Flex (route A only) | `rfa_set_family_type` with clearly different values, then back; `rfa_list_constraints` | Forms follow the parameters |
| Plan lines (route C) | `totalCreated`, `skipped`, `boundingBoxMm` vs. `planBboxMm` | Counts match, no unexpected skips |

Then `rfa_save_as` (if not already saved at the confirmed path) and, if the user wants it in a project that is already open, `rfa_load_into_project`. After placing in the project, `get_element_geometry` can confirm the placed size (hosted lighting bbox includes the light-source symbol).

Final report: template, route (A/B/C), family path, types and parameters created, size check result, anything skipped or left for the user.

## 工具

| Tool | Use |
|---|---|
| `rfa_new_family` / `rfa_open_family` | Create from the chosen template / open an existing .rfa |
| `rfa_get_document_info` | Re-anchor on the active family; read planes, types, materials |
| `rfa_set_family_type` | Create the first type; flex test |
| `rfa_add_parameter` / `rfa_modify_parameter` | Parameters |
| `rfa_create_material` | Materials used by forms or parameters |
| `rfa_create_reference_plane` / `rfa_create_reference_line` | Skeleton |
| `rfa_create_dimension` | Labels and EQ |
| `rfa_create_extrusion` / `rfa_create_blend` / `rfa_create_revolution` / `rfa_create_sweep` / `rfa_create_swept_blend` | Route A forms |
| `rfa_align_to_reference_plane` | Lock faces to planes |
| `rfa_set_form_visibility` | Subcategory, detail level, view visibility |
| `rfa_create_connector` | MEP connectors (MEP templates only) |
| `rfa_place_nested_family` | Nested components |
| `rfa_import_cad` | Route B |
| `rfa_create_symbolic_line` | Route C (via `rhino-furniture-plan`) |
| `rfa_list_constraints` | Verify dimensions and locks |
| `rfa_delete_elements` | Undo a failed form (dryRun first) |
| `rfa_save_as` / `rfa_load_into_project` | Save and load |
| `list_slots` / `spawn_slot` / `run_python` / `close_slot` | Rhino source inspection and export |
| `get_element_geometry` | Check the placed size in the project |

## Reference

- `domain/rfa-family-authoring.md` — section 3 (build order), 4 (coordinates), 5.7 (templates), 5.15 (connectors), 5.16 (CAD import and template origins).
- `.claude/skills/rhino-furniture-plan/SKILL.md` — route C.
