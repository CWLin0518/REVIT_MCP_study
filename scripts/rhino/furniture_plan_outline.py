"""
furniture_plan_outline.py - Rhino furniture -> Revit family plan symbolic lines.

Role: helper script for the `rhino-furniture-plan` skill. Not an MCP server.
Runs inside Rhino 8 (CPython 3) through the Rhino MCP `run_python` tool.

Two stages, run in two different Rhino instances on purpose:

  1. EXPORT  (the user's own Rhino, read-only)
     Copies the selected furniture geometry into a small .3dm file.
     Nothing in the user's document is added, changed or saved.

  2. COMPUTE (a separate Rhino started with spawn_slot)
     Reads that .3dm, casts a grid of rays straight down, records which
     part is on top at every grid cell, and traces:
       - the outer plan outline (boundary of everything that was hit)
       - the boundaries between parts that are visible from above
         (seat / backrest / armrest ...), with hidden parts removed.
     Writes a JSON file whose `polylines` array can be passed directly to
     the batch form of `rfa_create_symbolic_line`.

Usage from run_python (set PARAMS, then exec this file):

    PARAMS = {"stage": "export", "out_3dm": r"<scratch>\\sofa.3dm"}
    # optional: "object_ids": ["<guid>", ...] instead of the current selection
    exec(open(r"<repo>\\scripts\\rhino\\furniture_plan_outline.py", encoding="utf-8").read())

    PARAMS = {"stage": "compute", "in_3dm": r"<scratch>\\sofa.3dm",
              "out_json": r"<scratch>\\sofa_plan.json"}
    exec(open(r"<repo>\\scripts\\rhino\\furniture_plan_outline.py", encoding="utf-8").read())

Coordinates and units (output is always millimetres):
  - Block instance selected: block-definition coordinates (the block's own
    front/back orientation), scaled by the instance's uniform scale, so the
    family origin is the block base point.
  - Loose objects selected: world coordinates moved so the bottom-centre of
    their bounding box is the origin.
  - Revit furniture convention is front = -Y. If the source faces another
    way, pass "rotate_deg" in the compute stage.

Compute options (all optional):
  cell_mm        grid spacing, default 4
  min_part_pct   parts whose visible top area is below this % of the
                 footprint are merged into their neighbours, default 1.0
  reduce_mm      polyline simplification tolerance, default 2.5
  min_line_mm    drop inner boundary pieces shorter than this, default 40
  snap_mm        snap inner line ends onto the outer outline within this
                 distance, default 12
  rotate_deg     rotate the result about Z (counter-clockwise), default 0
  group_by       "object" (default) or "layer": parts on the same layer are
                 treated as one component, so no line is drawn between them
"""

import json
import math
import os
import time

import Rhino

G = Rhino.Geometry


def _p(name, default=None):
    return PARAMS.get(name, default)  # noqa: F821 - PARAMS is set by the caller


# ---------------------------------------------------------------- stage 1


def _mesh_of(geom):
    if isinstance(geom, G.Mesh):
        return [geom.DuplicateMesh()]
    if isinstance(geom, G.Extrusion):
        geom = geom.ToBrep()
    if isinstance(geom, G.SubD):
        geom = geom.ToBrep(G.SubDToBrepOptions())
    if isinstance(geom, G.Brep):
        mp = G.MeshingParameters()
        mp.Tolerance = 0.5
        mp.MaximumEdgeLength = 30
        mp.JaggedSeams = False
        return list(G.Mesh.CreateFromBrep(geom, mp) or [])
    return []


def _walk_instance(doc, idef, xform, layer, out):
    for ob in idef.GetObjects():
        g = ob.Geometry
        lay = doc.Layers[ob.Attributes.LayerIndex].FullPath
        if isinstance(g, G.InstanceReferenceGeometry):
            sub = doc.InstanceDefinitions.FindId(g.ParentIdefId)
            _walk_instance(doc, sub, xform * g.Xform, lay, out)
            continue
        for m in _mesh_of(g):
            m.Transform(xform)
            out.append((m, lay))


def stage_export():
    doc = __rhino_doc__  # noqa: F821 - injected by the Rhino MCP script runner
    ids = _p("object_ids")
    if ids:
        import System
        sel = [doc.Objects.FindId(System.Guid(i)) for i in ids]
        sel = [o for o in sel if o is not None]
    else:
        sel = list(doc.Objects.GetSelectedObjects(False, False))
    if not sel:
        raise RuntimeError("Nothing is selected in Rhino. Select the furniture first.")
    to_mm = Rhino.RhinoMath.UnitScale(doc.ModelUnitSystem, Rhino.UnitSystem.Millimeters)
    parts = []
    names = []
    if len(sel) == 1 and isinstance(sel[0].Geometry, G.InstanceReferenceGeometry):
        inst = sel[0]
        idef = inst.InstanceDefinition
        x = inst.InstanceXform
        det = abs(x.Determinant)
        s = det ** (1.0 / 3.0) if det > 0 else 1.0
        xf = G.Transform.Scale(G.Point3d.Origin, s * to_mm)
        _walk_instance(doc, idef, xf, None, parts)
        names.append(idef.Name)
        source = "block"
    else:
        tmp = []
        bb = G.BoundingBox.Empty
        for ob in sel:
            lay = doc.Layers[ob.Attributes.LayerIndex].FullPath
            g = ob.Geometry
            if isinstance(g, G.InstanceReferenceGeometry):
                idef = doc.InstanceDefinitions.FindId(g.ParentIdefId)
                sub = []
                _walk_instance(doc, idef, g.Xform, lay, sub)
                tmp.extend(sub)
                names.append(idef.Name)
            else:
                tmp.extend((m, lay) for m in _mesh_of(g))
        for m, _ in tmp:
            bb.Union(m.GetBoundingBox(True))
        c = bb.Center
        move = G.Transform.Translation(-c.X, -c.Y, -bb.Min.Z)
        sc = G.Transform.Scale(G.Point3d.Origin, to_mm)
        for m, lay in tmp:
            m.Transform(move)
            m.Transform(sc)
            parts.append((m, lay))
        source = "objects"
    if not parts:
        raise RuntimeError("The selection contains no mesh, brep, extrusion or SubD geometry.")
    f = Rhino.FileIO.File3dm()
    f.Settings.ModelUnitSystem = Rhino.UnitSystem.Millimeters
    for m, lay in parts:
        a = Rhino.DocObjects.ObjectAttributes()
        a.Name = lay or ""
        f.Objects.AddMesh(m, a)
    path = _p("out_3dm")
    f.Write(path, 8)
    bb = G.BoundingBox.Empty
    for m, _ in parts:
        bb.Union(m.GetBoundingBox(True))
    print(json.dumps({
        "stage": "export", "source": source, "names": names, "parts": len(parts),
        "faces": sum(m.Faces.Count for m, _ in parts),
        "sizeMm": [round(bb.Max.X - bb.Min.X, 1), round(bb.Max.Y - bb.Min.Y, 1), round(bb.Max.Z - bb.Min.Z, 1)],
        "bboxMm": [[round(bb.Min.X, 1), round(bb.Min.Y, 1), round(bb.Min.Z, 1)],
                   [round(bb.Max.X, 1), round(bb.Max.Y, 1), round(bb.Max.Z, 1)]],
        "file": path}, ensure_ascii=False))


# ---------------------------------------------------------------- stage 2


def _trace(segments, smooth, reduce_mm):
    curves = G.Curve.JoinCurves(segments, 0.01)
    out = []
    for c in curves:
        ok, pl = c.TryGetPolyline()
        if not ok:
            continue
        closed = pl.IsClosed
        for _ in range(smooth):
            pl.Smooth(0.5)
        if closed and not pl.IsClosed:
            pl.Add(pl[0])
        pl.ReduceSegments(reduce_mm)
        out.append(pl)
    return out


def stage_compute():
    t0 = time.time()
    cell = float(_p("cell_mm", 4.0))
    min_pct = float(_p("min_part_pct", 1.0))
    reduce_mm = float(_p("reduce_mm", 2.5))
    min_line = float(_p("min_line_mm", 40.0))
    snap_mm = float(_p("snap_mm", 12.0))
    group_by = _p("group_by", "object")

    f = Rhino.FileIO.File3dm.Read(_p("in_3dm"))
    meshes, keys = [], []
    for o in f.Objects:
        if isinstance(o.Geometry, G.Mesh):
            meshes.append(o.Geometry)
            keys.append(o.Attributes.Name if group_by == "layer" else str(len(keys)))
    key_ids = {}
    part_label = [key_ids.setdefault(k, len(key_ids) + 1) for k in keys]

    allm = G.Mesh()
    face_label = []
    for m, lab in zip(meshes, part_label):
        allm.Append(m)
        face_label.extend([lab] * m.Faces.Count)
    bb = allm.GetBoundingBox(True)
    x0, y0 = bb.Min.X - cell, bb.Min.Y - cell
    nx = int(math.ceil((bb.Max.X - bb.Min.X) / cell)) + 3
    ny = int(math.ceil((bb.Max.Y - bb.Min.Y) / cell)) + 3
    ztop = bb.Max.Z + 100.0
    down = G.Vector3d(0, 0, -1)

    # 1. top-most part per cell (0 = nothing hit)
    lab = [[0] * nx for _ in range(ny)]
    for j in range(ny):
        y = y0 + j * cell
        row = lab[j]
        for i in range(nx):
            x = x0 + i * cell
            t = G.Intersect.Intersection.MeshRay(allm, G.Ray3d(G.Point3d(x, y, ztop), down))
            if t < 0:
                continue
            mp = allm.ClosestMeshPoint(G.Point3d(x, y, ztop - t), 1.0)
            row[i] = face_label[mp.FaceIndex] if mp else -1

    # 2. merge parts that barely show from above into their neighbours
    counts = {}
    for row in lab:
        for v in row:
            if v > 0:
                counts[v] = counts.get(v, 0) + 1
    covered = sum(counts.values()) or 1
    small = {k for k, v in counts.items() if 100.0 * v / covered < min_pct}
    for row in lab:
        for i, v in enumerate(row):
            if v in small:
                row[i] = -1
    # empty cells not connected to the grid border are gaps between parts
    # (e.g. the seam between two cushions), not outside: fill them too
    outside = [[False] * nx for _ in range(ny)]
    stack = [(i, j) for i in range(nx) for j in (0, ny - 1)] + [(i, j) for j in range(ny) for i in (0, nx - 1)]
    while stack:
        i, j = stack.pop()
        if not (0 <= i < nx and 0 <= j < ny) or outside[j][i] or lab[j][i] != 0:
            continue
        outside[j][i] = True
        stack.extend(((i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1)))
    for j in range(ny):
        for i in range(nx):
            if lab[j][i] == 0 and not outside[j][i]:
                lab[j][i] = -1
    for _ in range(50):
        changed = 0
        snap = [r[:] for r in lab]
        for j in range(ny):
            for i in range(nx):
                if snap[j][i] != -1:
                    continue
                votes = {}
                for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ii, jj = i + di, j + dj
                    if 0 <= ii < nx and 0 <= jj < ny and snap[jj][ii] > 0:
                        votes[snap[jj][ii]] = votes.get(snap[jj][ii], 0) + 1
                if votes:
                    lab[j][i] = max(votes, key=votes.get)
                    changed += 1
        if not changed:
            break

    # 3. cell-edge segments: outer (hit | empty) and inner (part A | part B)
    outer_segs, inner_segs = [], {}

    def L(i, j):
        return lab[j][i] if 0 <= i < nx and 0 <= j < ny else 0

    for j in range(-1, ny):
        for i in range(-1, nx):
            a = L(i, j)
            for di, dj in ((1, 0), (0, 1)):
                b = L(i + di, j + dj)
                if a == b:
                    continue
                if di == 1:
                    x = x0 + (i + 0.5) * cell
                    p, q = G.Point3d(x, y0 + (j - 0.5) * cell, 0), G.Point3d(x, y0 + (j + 0.5) * cell, 0)
                else:
                    y = y0 + (j + 0.5) * cell
                    p, q = G.Point3d(x0 + (i - 0.5) * cell, y, 0), G.Point3d(x0 + (i + 0.5) * cell, y, 0)
                seg = G.LineCurve(p, q)
                if a == 0 or b == 0:
                    outer_segs.append(seg)
                elif a > 0 and b > 0:
                    inner_segs.setdefault(tuple(sorted((a, b))), []).append(seg)

    outers = _trace(outer_segs, 6, reduce_mm)
    outers = [pl for pl in outers if pl.Length > min_line]
    outer_curves = [G.PolylineCurve(pl) for pl in outers]

    def nearest_on_outer(pt):
        best, bd = None, 1e18
        for oc in outer_curves:
            ok, t = oc.ClosestPoint(pt)
            if ok:
                q = oc.PointAt(t)
                d = q.DistanceTo(pt)
                if d < bd:
                    best, bd = q, d
        return best, bd

    inners = []
    for pair, segs in sorted(inner_segs.items()):
        for pl in _trace(segs, 6, reduce_mm):
            if not pl.IsClosed:
                for end in (0, pl.Count - 1):
                    q, d = nearest_on_outer(pl[end])
                    if q is not None and d < snap_mm:
                        pl[end] = q
            if pl.Length >= min_line:
                inners.append((pair, pl))

    rot = float(_p("rotate_deg", 0.0))
    rx = G.Transform.Rotation(math.radians(rot), G.Point3d.Origin) if rot else None

    def pts(pl):
        out = []
        for p in pl:
            if rx is not None:
                p = G.Point3d(p)
                p.Transform(rx)
            out.append([round(p.X, 1), round(p.Y, 1), 0.0])
        return out

    polylines = [{"pointsMm": pts(pl), "closed": False, "role": "outer"} for pl in outers]
    polylines += [{"pointsMm": pts(pl), "closed": False, "role": "inner",
                   "parts": list(pair)} for pair, pl in inners]
    nseg = sum(len(p["pointsMm"]) - 1 for p in polylines)
    allpts = [q for p in polylines for q in p["pointsMm"]]
    result = {
        "stage": "compute",
        "cellMm": cell,
        "parts": len(meshes),
        "visibleParts": len(counts),
        "mergedSmallParts": len(small),
        "outerPolylines": len(outers),
        "innerPolylines": len(inners),
        "segments": nseg,
        "planBboxMm": [[min(p[0] for p in allpts), min(p[1] for p in allpts)],
                       [max(p[0] for p in allpts), max(p[1] for p in allpts)]] if allpts else None,
        "seconds": round(time.time() - t0, 1),
        "polylines": polylines,
    }
    with open(_p("out_json"), "w", encoding="utf-8") as fh:
        json.dump(result, fh, ensure_ascii=False)
    summary = {k: v for k, v in result.items() if k != "polylines"}
    summary["file"] = _p("out_json")
    print(json.dumps(summary, ensure_ascii=False))


_stage = _p("stage")
if _stage == "export":
    stage_export()
elif _stage == "compute":
    stage_compute()
else:
    raise RuntimeError("PARAMS['stage'] must be 'export' or 'compute'")
