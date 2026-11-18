---
name: rhino-furniture-plan
description: "Reads a furniture model selected in Rhino, creates a new Revit furniture family, and draws its plan representation as symbolic lines: the outer outline plus the visible boundaries between parts such as seat, backrest and armrest. Use when turning a Rhino furniture block into a Revit family plan symbol. 觸發條件：Rhino 家具轉族群、家具平面輪廓、沙發平面、椅子平面符號、平面外輪廓、椅背椅墊關係線、Rhino to Revit furniture、furniture plan outline、plan symbol from Rhino、symbolic lines from Rhino。"
---

# Rhino Furniture → Revit Family Plan Outline

Turns a piece of furniture selected in Rhino into a new Revit furniture family (.rfa) whose plan view shows the outer outline and the part-to-part boundaries visible from above.

## Prerequisites

- Rhino 8 is open with the furniture model, and the Rhino MCP tools are available.
- Revit is open with the Revit MCP service enabled (ribbon). The batch `polylines` form of `rfa_create_symbolic_line` must be deployed.
- `scripts/rhino/furniture_plan_outline.py` exists in this repo.

## Workflow

### 1. Identify the Rhino slots

Call `list_slots`. The user's own Rhino is the **adopted** slot. Pass `slot` explicitly on **every** Rhino call in this workflow — an omitted slot goes to the last-used Rhino, which silently runs the script in the wrong instance.

### 2. Export the selection (user's Rhino, read-only)

Ask the user to select one piece of furniture (a block instance is best), or take object ids they name. Run in the **user's** slot:

```python
PARAMS = {"stage": "export", "out_3dm": r"<scratchpad>\<name>.3dm"}
# or add "object_ids": ["<guid>"] instead of relying on the selection
exec(open(r"<repo>\scripts\rhino\furniture_plan_outline.py", encoding="utf-8").read())
```

Report `names`, `parts` and `sizeMm` back to the user and confirm it is the right object. This stage adds, changes and saves nothing in the user's document.

### 3. Compute the plan lines (separate Rhino)

Heavy geometry work never runs in the user's Rhino (a large file crashed Rhino once during mesh work). `spawn_slot`, then run in **that** slot:

```python
PARAMS = {"stage": "compute", "in_3dm": r"<scratchpad>\<name>.3dm",
          "out_json": r"<scratchpad>\<name>_plan.json"}
exec(open(r"<repo>\scripts\rhino\furniture_plan_outline.py", encoding="utf-8").read())
```

Check the printed summary before going on:

| Field | Expect | If not |
|---|---|---|
| `outerPolylines` | 1 | more than 1 = separate pieces or a hole; inspect before drawing |
| `planBboxMm` | matches `sizeMm` from step 2 within a few mm | wrong units or wrong object |
| `innerPolylines` | one per visible part boundary | too many: raise `min_part_pct` or use `group_by: "layer"`; too few: lower `min_part_pct` |
| `segments` | ≤ 2000 | split the batch in step 5 |

Options (`cell_mm`, `min_part_pct`, `reduce_mm`, `min_line_mm`, `snap_mm`, `rotate_deg`, `group_by`) are documented in the script header. Close the spawned slot with `close_slot` when done.

### 4. Create the family (Revit)

1. `rfa_new_family` with the furniture template (`templateName`, e.g. "Metric Furniture" / 「公制家具」; the name depends on the Revit language — if not found, choose from the list the error returns) and `savePath` (ask the user where; never overwrite without confirmation).
2. `rfa_get_document_info` — re-anchor: confirm the active document is the new family and find the horizontal reference plane at Z = 0 (usually named "Reference Plane"). If none exists, create one with `rfa_create_reference_plane` (`normal: "Z"`, `offsetMm: 0`).

### 5. Draw the plan lines in one call

Read `polylines` from the JSON and call `rfa_create_symbolic_line` once with `hostPlane` = that Z = 0 plane and `polylines` = the array (each item's `pointsMm` as is; drop the extra `role` / `parts` keys if the tool rejects them). Check `totalCreated`, `skipped` and that `boundingBoxMm` matches `planBboxMm`.

### 6. Save and report

`rfa_save_as` (or keep the path from step 4). Report: family path, outer/inner line counts, total segments, plan size, and any parts merged as hidden.

## 工具

| Tool | Use |
|---|---|
| `list_slots` / `spawn_slot` / `close_slot` | Find the user's Rhino, start and close the separate compute Rhino |
| `run_python` | Run the export and compute stages of the helper script |
| `rfa_new_family` | Create the furniture family from a template |
| `rfa_get_document_info` | Re-anchor on the active family and find the Z = 0 plane |
| `rfa_create_reference_plane` | Only if the template has no horizontal plane at Z = 0 |
| `rfa_create_symbolic_line` (`polylines`) | Draw all plan lines in one transaction |
| `rfa_save_as` | Save the family |

## Notes

- Lines are traced from a top-down ray grid (default 4 mm), smoothed and simplified (default 2.5 mm); expect about ±2 mm against the true silhouette. For tighter results lower `cell_mm` and `reduce_mm`.
- Parts that show less than `min_part_pct` of the footprint from above (legs, screws, hidden frames) are merged into their neighbours, so no lines are drawn for them.
- Gaps fully enclosed by the furniture (e.g. the seam between two cushions) are treated as inside and become a boundary line, not a hole.
- The plan symbol is 2D only. A 3D body is a separate step (export SAT from Rhino and `rfa_import_cad`, or model with `rfa_*` forms); when both exist, hide the 3D body in plan views so only the symbolic lines show.
- Lines are not constrained to reference planes; they do not flex with parameters.

## Reference

- `domain/rfa-family-authoring.md` — section 5.5 (symbolic lines, batch form), 5.7 (new family / templates), 5.16 (CAD import).
