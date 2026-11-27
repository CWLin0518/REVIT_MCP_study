#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    // =====================================================================================
    // Remote placement helpers (phone -> Revit workflow, step 4 "place"):
    //   load_family_from_path  - Document.LoadFamily(path) straight into the project, no family editor.
    //   find_host_at_point     - which ceiling / floor face is above / below a plan point, or which wall
    //                            is nearest to it, using the same tests place_family_instances applies
    //                            with hostId (vertical ray vs bottom/top faces; straight walls only).
    //
    // Why a dedicated host finder: query_walls_by_location treats every wall as a straight segment and
    // does not say whether the wall can host (curved / curtain walls are rejected by
    // place_family_instances), and no existing tool answers "which ceiling covers this point" - ceiling
    // bounding boxes overlap, L-shaped ceilings and openings make a bbox test unreliable.
    // =====================================================================================
    public partial class CommandExecutor
    {
        private const double RpMmPerFt = 304.8;

        #region load_family_from_path

        /// <summary>
        /// Load a .rfa from disk into an open project with Document.LoadFamily(path, options, out family).
        /// Parameters:
        ///   filePath (string, required)       absolute .rfa path
        ///   targetProject (string)            project title / path; default = active project, else the only open project
        ///   reloadIfExists (bool, false)      a family with the same name already in the project: false = use it as is
        ///   overwriteParameterValues (bool, false)  only with a reload: replace existing type parameter values
        ///   activateProject (bool, true)      when the target is not the active document, switch to it afterwards
        /// </summary>
        private object LoadFamilyFromPath(JObject parameters)
        {
            string filePath = parameters["filePath"]?.Value<string>()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath))
                throw new Exception("filePath is required and must be an absolute path to a .rfa file.");
            if (!string.Equals(Path.GetExtension(filePath), ".rfa", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"filePath must be a .rfa file: {filePath}");
            if (!File.Exists(filePath))
                throw new Exception($"File not found: {filePath}");

            bool reloadIfExists = parameters["reloadIfExists"]?.Value<bool>() ?? false;
            bool overwriteValues = parameters["overwriteParameterValues"]?.Value<bool>() ?? false;
            bool activate = parameters["activateProject"]?.Value<bool>() ?? true;
            string target = parameters["targetProject"]?.Value<string>()?.Trim();
            var warnings = new List<string>();

            // ---- target project ----
            Document active = _uiApp.ActiveUIDocument?.Document;
            List<Document> projects = RfaListOpenProjects();
            string projectList = projects.Count == 0 ? "(no project is open)" : string.Join(", ", projects.Select(d => $"\"{d.Title}\""));
            Document project;
            if (!string.IsNullOrEmpty(target))
            {
                string targetNoExt = RfaStripExtension(target, ".rvt");
                var matches = projects.Where(d =>
                {
                    string title = d.Title ?? "";
                    string path = null;
                    try { path = d.PathName; } catch { }
                    return string.Equals(title, target, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(RfaStripExtension(title, ".rvt"), targetNoExt, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(path) && Path.IsPathRooted(target) && RfaSamePath(path, target));
                }).ToList();
                if (matches.Count != 1)
                    throw new Exception($"targetProject \"{target}\" matched {matches.Count} open projects. Open projects: {projectList}");
                project = matches[0];
            }
            else if (active != null && !active.IsFamilyDocument)
            {
                project = active;
            }
            else
            {
                if (projects.Count != 1)
                    throw new Exception($"The active document is a family, and {projects.Count} projects are open; give targetProject. Open projects: {projectList}");
                project = projects[0];
            }

            if (project.IsReadOnly)
                throw new Exception($"Project \"{project.Title}\" is read-only.");
            if (project.IsModifiable)
                throw new Exception($"Project \"{project.Title}\" has an open transaction; finish or cancel the current edit in Revit first.");

            // ---- already loaded? (a family loaded from a file is named after the file) ----
            string familyName = Path.GetFileNameWithoutExtension(filePath);
            Family existing = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
            IdType? existingId = existing?.Id.GetIdValue();

            Family family = existing;
            string status;
            if (existing != null && !reloadIfExists)
            {
                status = "already_loaded";
                warnings.Add($"A family named \"{existing.Name}\" is already in the project; it was used as is (reloadIfExists=false). Its types may differ from the file.");
            }
            else
            {
                Family loaded = null;
                bool ok;
                using (Transaction t = new Transaction(project, $"Load family: {familyName}"))
                {
                    t.Start();
                    try
                    {
                        ok = project.LoadFamily(filePath, new RfaLoadIntoProjectOptions(overwriteValues), out loaded);
                    }
                    catch (Exception ex)
                    {
                        t.RollBack();
                        throw new Exception($"LoadFamily failed for {filePath} ({ex.GetType().Name}: {ex.Message}). A family saved in a newer Revit version, or a category that cannot be loaded into a project, are the usual causes.");
                    }
                    t.Commit();
                }

                if (ok && loaded != null)
                {
                    family = loaded;
                    status = existing != null ? "reloaded" : "loaded";
                }
                else
                {
                    // Revit returns false when the same family is already loaded and nothing changed.
                    family = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>()
                        .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
                    if (family == null)
                        throw new Exception($"LoadFamily returned false and no family named \"{familyName}\" is in the project.");
                    status = "unchanged";
                    warnings.Add("LoadFamily reported that nothing was loaded (same family already in the project); the existing family is returned.");
                }
            }

            // ---- describe the family ----
            string categoryName = null, builtInCategory = null;
            try
            {
                Category cat = family.FamilyCategory;
                categoryName = cat?.Name;
                if (cat != null)
                {
                    string bic = Enum.ToObject(typeof(BuiltInCategory), cat.Id.GetIdValue()).ToString();
                    if (bic.StartsWith("OST_", StringComparison.Ordinal)) builtInCategory = bic;
                }
            }
            catch { }

            string hosting = null;
            try { hosting = family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsValueString(); } catch { }

            FamilyPlacementType placementType = family.FamilyPlacementType;
            string placementNote;
            switch (placementType)
            {
                case FamilyPlacementType.OneLevelBased:
                    placementNote = "level-based: place_family_instances without hostId (levelName + z)"; break;
                case FamilyPlacementType.OneLevelBasedHosted:
                    placementNote = "hosted: place_family_instances needs hostId (wall, ceiling, floor ... per hostingBehavior)"; break;
                case FamilyPlacementType.WorkPlaneBased:
                    placementNote = "work-plane / face based: place_family_instances with hostId places it on the host face"; break;
                default:
                    placementNote = $"{placementType}: not supported by place_family_instances"; break;
            }

            var types = new List<object>();
            foreach (ElementId sid in family.GetFamilySymbolIds())
            {
                FamilySymbol s = project.GetElement(sid) as FamilySymbol;
                if (s == null) continue;
                var lengths = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (Parameter p in s.Parameters)
                {
                    try
                    {
                        if (p.StorageType != StorageType.Double || p.Definition == null) continue;
                        if (p.Definition.GetDataType() != SpecTypeId.Length) continue;
                        string n = p.Definition.Name;
                        if (string.IsNullOrEmpty(n) || lengths.ContainsKey(n)) continue;
                        lengths[n] = new { mm = Math.Round(p.AsDouble() * RpMmPerFt, 2), readOnly = p.IsReadOnly };
                    }
                    catch { }
                }
                types.Add(new { id = s.Id.GetIdValue(), name = s.Name, isActive = s.IsActive, lengthParams = lengths });
            }

            // ---- activate ----
            bool activated = false;
            bool isActive = active != null && project.Equals(active);
            if (!isActive && activate)
            {
                string projPath = null;
                try { projPath = project.PathName; } catch { }
                if (string.IsNullOrEmpty(projPath))
                    warnings.Add($"Project \"{project.Title}\" was never saved, so it cannot be activated by path; switch to it in Revit before placing.");
                else
                {
                    try { _uiApp.OpenAndActivateDocument(projPath); activated = true; }
                    catch (Exception ex) { warnings.Add($"Family loaded, but switching to \"{project.Title}\" failed ({ex.GetType().Name}: {ex.Message}). Switch in Revit before placing."); }
                }
            }
            else if (!isActive)
            {
                warnings.Add("The target project is not the active document; placement tools act on the active document.");
            }

            string activeTitle = null;
            try { activeTitle = _uiApp.ActiveUIDocument?.Document?.Title; } catch { }

            return new
            {
                Success = true,
                status,
                filePath,
                familyName = family.Name,
                familyId = family.Id.GetIdValue(),
                previousFamilyId = existingId,
                category = categoryName,
                builtInCategory,
                placementType = placementType.ToString(),
                hostingBehavior = hosting,
                placementNote,
                targetProject = project.Title,
                activated,
                activeDocument = activeTitle,
                typeCount = types.Count,
                types,
                warnings,
            };
        }

        #endregion

        #region find_host_at_point

        /// <summary>
        /// Find the host for a plan point.
        /// Parameters: x, y (mm, project internal), hostKind = ceiling | floor | wall, levelName or viewId (optional),
        /// searchRadiusMm (default 3000), maxResults (default 5).
        /// ceiling / floor: vertical ray through (x, y) against each element's bottom (ceiling) / top (floor) faces -
        /// the same test place_family_instances uses with hostId, so a hit here is placeable there.
        /// wall: distance from (x, y) to each wall's location curve in plan; placeable = straight, not curtain,
        /// point projects inside the segment and is off the centreline (side can be decided).
        /// </summary>
        private object FindHostAtPoint(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            if (doc.IsFamilyDocument)
                throw new Exception("The active document is a family; switch to the project first.");

            if (parameters["x"] == null || parameters["y"] == null)
                throw new Exception("x and y (mm, project internal coordinates) are required.");
            double xMm = parameters["x"].Value<double>();
            double yMm = parameters["y"].Value<double>();
            string kind = (parameters["hostKind"]?.Value<string>() ?? "").Trim().ToLowerInvariant();
            if (kind != "ceiling" && kind != "floor" && kind != "wall")
                throw new Exception("hostKind must be 'ceiling', 'floor' or 'wall'.");
            double radiusMm = parameters["searchRadiusMm"]?.Value<double>() ?? 3000;
            if (radiusMm <= 0) radiusMm = 3000;
            int maxResults = Math.Max(1, Math.Min(50, parameters["maxResults"]?.Value<int>() ?? 5));

            // ---- level ----
            Level level = null;
            var warnings = new List<string>();
            JToken viewIdTok = parameters["viewId"];
            string levelName = parameters["levelName"]?.Value<string>()?.Trim();
            if (viewIdTok != null && viewIdTok.Type != JTokenType.Null)
            {
                View v = doc.GetElement(viewIdTok.Value<IdType>().ToElementId()) as View;
                if (v == null) throw new Exception($"viewId {viewIdTok} is not a view.");
                level = v.GenLevel;
                if (level == null) throw new Exception($"View \"{v.Name}\" has no associated level; give levelName.");
                if (!string.IsNullOrEmpty(levelName) && !string.Equals(level.Name, levelName, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"viewId level \"{level.Name}\" differs from levelName \"{levelName}\"; the view's level was used.");
            }
            else if (!string.IsNullOrEmpty(levelName))
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                level = levels.FirstOrDefault(l => l.Name == levelName)
                     ?? levels.FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (level == null)
                    throw new Exception($"Level \"{levelName}\" not found (exact name). Levels: {string.Join(", ", levels.OrderBy(l => l.ProjectElevation).Select(l => l.Name))}");
            }
            else
            {
                warnings.Add("No levelName / viewId: hosts on every level were searched.");
            }

            double xFt = xMm / RpMmPerFt, yFt = yMm / RpMmPerFt, rFt = radiusMm / RpMmPerFt;
            object levelInfo = level == null ? null : (object)new
            {
                name = level.Name,
                id = level.Id.GetIdValue(),
                projectElevationMm = Math.Round(level.ProjectElevation * RpMmPerFt, 2),
            };

            List<JObject> rows = kind == "wall"
                ? FindWallHosts(doc, level, xFt, yFt, rFt)
                : FindFaceHosts(doc, level, kind, xFt, yFt, rFt);

            List<JObject> top = rows.Take(maxResults).ToList();
            JObject best = rows.FirstOrDefault(r => r.Value<bool>("placeable"));
            string note;
            if (best != null)
                note = kind == "wall"
                    ? "recommendedHostId = nearest placeable straight wall; pass it as hostId with the same (x, y) to place_family_instances."
                    : $"recommendedHostId = the {kind} whose {(kind == "ceiling" ? "bottom" : "top")} face is hit by a vertical line through (x, y); pass it as hostId with hostFace '{(kind == "ceiling" ? "bottom" : "top")}'.";
            else
                note = rows.Count == 0
                    ? $"No {kind} within {radiusMm} mm{(level != null ? " on level " + level.Name : "")}."
                    : (kind == "wall" ? "Walls were found but none is placeable (curved, curtain, or the point projects outside the wall)." : $"No {kind} face lies above/below (x, y); the nearest ones are listed (opening, gap or boundary).");

            return new
            {
                Success = true,
                hostKind = kind,
                point = new { x = xMm, y = yMm },
                level = levelInfo,
                searchRadiusMm = radiusMm,
                recommendedHostId = best == null ? null : (object)best["hostId"].Value<IdType>(),
                candidateCount = rows.Count,
                candidates = top,
                note,
                warnings,
            };
        }

        private List<JObject> FindFaceHosts(Document doc, Level level, string kind, double xFt, double yFt, double rFt)
        {
            BuiltInCategory bic = kind == "ceiling" ? BuiltInCategory.OST_Ceilings : BuiltInCategory.OST_Floors;
            var hits = new List<Tuple<double, JObject>>();
            var misses = new List<Tuple<double, JObject>>();
            foreach (Element e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                HostObject ho = e as HostObject;
                if (ho == null) continue;
                if (level != null && e.LevelId != level.Id) continue;
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                double dx = Math.Max(0, Math.Max(bb.Min.X - xFt, xFt - bb.Max.X));
                double dy = Math.Max(0, Math.Max(bb.Min.Y - yFt, yFt - bb.Max.Y));
                double bboxDist = Math.Sqrt(dx * dx + dy * dy);
                if (bboxDist > rFt) continue;

                double? hitZ = null;
                if (bboxDist <= 1e-9)
                {
                    IList<Reference> refs = null;
                    try { refs = kind == "ceiling" ? HostObjectUtils.GetBottomFaces(ho) : HostObjectUtils.GetTopFaces(ho); } catch { }
                    if (refs != null)
                    {
                        Line ray = Line.CreateBound(new XYZ(xFt, yFt, bb.Min.Z - 1.0), new XYZ(xFt, yFt, bb.Max.Z + 1.0));
                        foreach (Reference r in refs)
                        {
                            Face f = null;
                            try { f = ho.GetGeometryObjectFromReference(r) as Face; } catch { }
                            if (f == null) continue;
                            IntersectionResultArray ira;
                            if (f.Intersect(ray, out ira) == SetComparisonResult.Overlap && ira != null && ira.Size > 0)
                            {
                                double z = ira.get_Item(0).XYZPoint.Z;
                                if (hitZ == null || (kind == "ceiling" ? z < hitZ.Value : z > hitZ.Value)) hitZ = z;
                            }
                        }
                    }
                }

                Level hl = doc.GetElement(e.LevelId) as Level;
                var row = new JObject
                {
                    ["hostId"] = e.Id.GetIdValue(),
                    ["category"] = e.Category?.Name,
                    ["typeName"] = doc.GetElement(e.GetTypeId())?.Name,
                    ["levelName"] = hl?.Name,
                    ["containsPoint"] = hitZ.HasValue,
                    ["placeable"] = hitZ.HasValue,
                    ["faceElevationMm"] = hitZ.HasValue ? (JToken)Math.Round(hitZ.Value * RpMmPerFt, 2) : JValue.CreateNull(),
                    ["heightAboveLevelMm"] = hitZ.HasValue && hl != null ? (JToken)Math.Round((hitZ.Value - hl.ProjectElevation) * RpMmPerFt, 2) : JValue.CreateNull(),
                    ["bboxDistanceMm"] = Math.Round(bboxDist * RpMmPerFt, 2),
                    ["face"] = kind == "ceiling" ? "bottom" : "top",
                };
                if (hitZ.HasValue) hits.Add(Tuple.Create(hitZ.Value, row));
                else misses.Add(Tuple.Create(bboxDist, row));
            }
            // ceiling: lowest bottom face first (what you see from the room); floor: highest top face first
            var ordered = kind == "ceiling" ? hits.OrderBy(h => h.Item1) : hits.OrderByDescending(h => h.Item1);
            return ordered.Select(h => h.Item2).Concat(misses.OrderBy(m => m.Item1).Select(m => m.Item2)).ToList();
        }

        private List<JObject> FindWallHosts(Document doc, Level level, double xFt, double yFt, double rFt)
        {
            double probeZ = level != null ? level.ProjectElevation + 1000.0 / RpMmPerFt : 0;
            var rows = new List<Tuple<bool, double, JObject>>();
            foreach (Wall w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>())
            {
                LocationCurve lc = w.Location as LocationCurve;
                Curve c = lc?.Curve;
                if (c == null || !c.IsBound) continue;

                if (level != null)
                {
                    bool onLevel = false;
                    ElementId baseId = null;
                    try { baseId = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId(); } catch { }
                    if (baseId != null && baseId == level.Id) onLevel = true;
                    else if (w.LevelId == level.Id) onLevel = true;
                    else
                    {
                        BoundingBoxXYZ wb = w.get_BoundingBox(null);
                        if (wb != null && wb.Min.Z <= probeZ && wb.Max.Z >= probeZ) onLevel = true;
                    }
                    if (!onLevel) continue;
                }

                double cz = c.GetEndPoint(0).Z;
                XYZ p = new XYZ(xFt, yFt, cz);
                IntersectionResult pr = c.Project(p);
                if (pr == null) continue;
                XYZ proj = pr.XYZPoint;
                double dist = Math.Sqrt((p.X - proj.X) * (p.X - proj.X) + (p.Y - proj.Y) * (p.Y - proj.Y));
                if (dist > rFt) continue;

                double p0 = c.GetEndParameter(0), p1 = c.GetEndParameter(1);
                double tol = Math.Abs(p1 - p0) * 1e-6 + 1e-9;
                bool within = pr.Parameter > Math.Min(p0, p1) + tol && pr.Parameter < Math.Max(p0, p1) - tol;
                bool straight = c is Line;
                bool curtain = false;
                try { curtain = w.WallType.Kind == WallKind.Curtain; } catch { }
                double halfW = w.Width / 2.0;

                string side = null;
                double? faceX = null, faceY = null;
                bool offCentre = dist * RpMmPerFt >= 1.0;
                if (straight && offCentre)
                {
                    XYZ n = new XYZ(w.Orientation.X, w.Orientation.Y, 0).Normalize();
                    double s = (p - proj).DotProduct(n);
                    side = s > 0 ? "exterior" : "interior";
                    XYZ fp = proj + n * (s > 0 ? halfW : -halfW);
                    faceX = fp.X; faceY = fp.Y;
                }
                bool placeable = straight && !curtain && within && offCentre;
                var reasons = new List<string>();
                if (!straight) reasons.Add("not a straight wall");
                if (curtain) reasons.Add("curtain wall");
                if (!within) reasons.Add("point projects onto a wall end, not inside the wall");
                if (!offCentre) reasons.Add("point is on the wall centreline");

                Level wl = doc.GetElement(w.LevelId) as Level;
                BoundingBoxXYZ bbw = w.get_BoundingBox(null);
                var row = new JObject
                {
                    ["hostId"] = w.Id.GetIdValue(),
                    ["typeName"] = w.WallType?.Name,
                    ["levelName"] = wl?.Name,
                    ["isStraight"] = straight,
                    ["isCurtainWall"] = curtain,
                    ["withinSegment"] = within,
                    ["placeable"] = placeable,
                    ["notPlaceableBecause"] = reasons.Count == 0 ? JValue.CreateNull() : (JToken)string.Join("; ", reasons),
                    ["widthMm"] = Math.Round(w.Width * RpMmPerFt, 2),
                    ["lengthMm"] = Math.Round(c.Length * RpMmPerFt, 2),
                    ["distanceToCenterlineMm"] = Math.Round(dist * RpMmPerFt, 2),
                    ["distanceToFaceMm"] = Math.Round((dist - halfW) * RpMmPerFt, 2),
                    ["side"] = side,
                    ["projectedPoint"] = new JObject { ["x"] = Math.Round(proj.X * RpMmPerFt, 2), ["y"] = Math.Round(proj.Y * RpMmPerFt, 2) },
                    ["facePoint"] = faceX.HasValue ? (JToken)new JObject { ["x"] = Math.Round(faceX.Value * RpMmPerFt, 2), ["y"] = Math.Round(faceY.Value * RpMmPerFt, 2) } : JValue.CreateNull(),
                    ["baseZMm"] = bbw != null ? (JToken)Math.Round(bbw.Min.Z * RpMmPerFt, 2) : JValue.CreateNull(),
                    ["topZMm"] = bbw != null ? (JToken)Math.Round(bbw.Max.Z * RpMmPerFt, 2) : JValue.CreateNull(),
                };
                if (straight)
                {
                    XYZ a = c.GetEndPoint(0), b = c.GetEndPoint(1);
                    row["start"] = new JObject { ["x"] = Math.Round(a.X * RpMmPerFt, 2), ["y"] = Math.Round(a.Y * RpMmPerFt, 2) };
                    row["end"] = new JObject { ["x"] = Math.Round(b.X * RpMmPerFt, 2), ["y"] = Math.Round(b.Y * RpMmPerFt, 2) };
                }
                rows.Add(Tuple.Create(placeable, Math.Abs(dist - halfW), row));
            }
            return rows.OrderByDescending(r => r.Item1).ThenBy(r => r.Item2).Select(r => r.Item3).ToList();
        }

        #endregion
    }
}
