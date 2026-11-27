using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        // ------------------------------------------------------------------------------------
        // copy_room_ceilings_with_lighting
        //
        // Copies native ceilings that lie inside a source room, together with the fixtures hosted
        // on those ceilings (default: Lighting Fixtures), to congruent target rooms on other levels.
        //
        // Safety model:
        //  * Every room reference is resolved up front; unknown or ambiguous room numbers abort the
        //    whole call before any transaction (phase / design option filters, roomIdOverrides).
        //  * Translation = (target finish-boundary centre - source finish-boundary centre) in XY and
        //    (target level elevation - source level elevation) in Z. Target and source boundaries must
        //    be congruent under that translation (shapeToleranceMm) or the pair is refused.
        //  * Only ceilings fully inside the source room (boundaryToleranceMm) are copied. A ceiling that
        //    straddles the room boundary refuses the whole mapping instead of being copied or dropped.
        //  * Level exactness: a ceiling belongs to a room only when Ceiling.LevelId == Room.LevelId; a
        //    fixture only when its host ceiling's LevelId (or, for a fixture not hosted on a ceiling,
        //    its own level) equals Room.LevelId. verticalToleranceMm widens the search band (e.g. above
        //    the Upper Limit) but can never admit elements of another level.
        //  * Existing content in a target = level-exact ceilings/fixtures whose phase status in the
        //    target room's phase is New, Existing or Temporary. Demolished / Past / Future elements are
        //    reported as ignoredInTarget and never counted or deleted.
        //  * existingPolicy skip | fail: a target that already holds content is never written to.
        //  * existingPolicy replace (explicit caller opt-in): deletes ONLY level-exact target ceilings
        //    fully contained in the target room (phase status New, not in a group / design option, not
        //    pinned, not owned by another user) plus the requested-category fixtures hosted on them.
        //    Every dependent of those elements is inspected first; any straddling ceiling, unrequested
        //    hosted/dependent element (tags, other categories, model lines...) or requested fixture that
        //    the plan cannot remove refuses the target. The preview lists every element to be deleted.
        //    Deletion and copy run in the same pair transaction; doc.Delete's actual result must be a
        //    subset of the inspected set or the pair is rolled back. After the copy the target must hold
        //    only the new copies.
        //  * Each ceiling is copied with its hosted fixtures in one CopyElements call so Revit re-hosts
        //    the copies onto the copied ceiling; the copied ceiling is then re-levelled to the target
        //    room level with an offset that preserves the world height. Every copied element is
        //    verified (host, position, level, containment) before commit; any mismatch rolls the pair
        //    back. atomic=true (default) rolls the whole run back if any pair fails.
        //  * dryRun=true (default) plans, and with verifyInDryRun=true also performs the copy inside a
        //    TransactionGroup that is always rolled back, so the preview reports real host checks.
        // ------------------------------------------------------------------------------------

        private const double CcMmPerFt = 304.8;

        private sealed class CcRoomShape
        {
            public Room Room;
            public Level Level;
            public List<List<XYZ>> Loops = new List<List<XYZ>>();
            public List<XYZ> Points = new List<XYZ>();
            public double MinX, MinY, MaxX, MaxY;
            public double ZMin, ZMax;
            public XYZ Center;
        }

        private sealed class CcSourceCeiling
        {
            public Ceiling Ceiling;
            public BoundingBoxXYZ Box;
            public double OffsetFromRoomLevelFt;
            public List<FamilyInstance> Fixtures = new List<FamilyInstance>();
        }

        /// <summary>A ceiling or requested fixture already present in a target room.</summary>
        private sealed class CcExisting
        {
            public Element Element;
            public bool IsCeiling;
            public string Relation;
        }

        /// <summary>existingPolicy='replace': the exact, inspected deletion scope for one target room.</summary>
        private sealed class CcReplacePlan
        {
            public List<Ceiling> Ceilings = new List<Ceiling>();
            public List<FamilyInstance> Fixtures = new List<FamilyInstance>();
            /// <summary>Ceilings + fixtures passed to doc.Delete.</summary>
            public HashSet<IdType> Authorized = new HashSet<IdType>();
            /// <summary>Inspected internal dependents (sketch, sketch lines, openings, nested sub-components) that may cascade.</summary>
            public HashSet<IdType> AllowedCascade = new HashSet<IdType>();
            public List<string> Problems = new List<string>();
            public JObject Preview;
        }

        private sealed class CcPairFailure : Exception
        {
            public CcPairFailure(string message) : base(message) { }
        }

        private object CopyRoomCeilingsWithLighting(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) throw new Exception("No active document.");
            if (doc.IsFamilyDocument) throw new Exception("copy_room_ceilings_with_lighting works on project documents only.");

            bool dryRun = parameters["dryRun"]?.Value<bool>() ?? true;
            bool verifyInDryRun = parameters["verifyInDryRun"]?.Value<bool>() ?? true;
            bool atomic = parameters["atomic"]?.Value<bool>() ?? true;
            bool allowDesignOptionRooms = parameters["allowDesignOptionRooms"]?.Value<bool>() ?? false;
            string existingPolicy = (parameters["existingPolicy"]?.Value<string>() ?? "skip").Trim().ToLowerInvariant();
            if (existingPolicy != "skip" && existingPolicy != "fail" && existingPolicy != "replace")
                throw new Exception("existingPolicy must be 'skip', 'fail' or 'replace'.");
            bool replaceMode = existingPolicy == "replace";
            string phaseName = parameters["phaseName"]?.Value<string>();

            double boundaryTolMm = CcPositive(parameters, "boundaryToleranceMm", 150.0);
            double verticalTolMm = CcPositive(parameters, "verticalToleranceMm", 50.0);
            double shapeTolMm = CcPositive(parameters, "shapeToleranceMm", 50.0);
            double positionTolMm = CcPositive(parameters, "positionToleranceMm", 2.0);
            double boundaryTol = boundaryTolMm / CcMmPerFt;
            double verticalTol = verticalTolMm / CcMmPerFt;
            double positionTol = positionTolMm / CcMmPerFt;

            // ---------- hosted categories ----------
            var hostedCategories = new List<BuiltInCategory>();
            JArray catArray = parameters["hostedCategories"] as JArray;
            if (catArray == null || catArray.Count == 0)
                hostedCategories.Add(BuiltInCategory.OST_LightingFixtures);
            else
            {
                foreach (JToken tok in catArray)
                {
                    string name = tok?.Value<string>()?.Trim();
                    BuiltInCategory bic;
                    if (string.IsNullOrEmpty(name) || !name.StartsWith("OST_", StringComparison.Ordinal)
                        || !Enum.TryParse(name, false, out bic) || !Enum.IsDefined(typeof(BuiltInCategory), bic))
                        throw new Exception($"hostedCategories entry '{name}' is not a BuiltInCategory name (e.g. OST_LightingFixtures).");
                    if (bic == BuiltInCategory.OST_Ceilings || bic == BuiltInCategory.OST_Rooms)
                        throw new Exception($"hostedCategories must not contain {name}.");
                    if (!hostedCategories.Contains(bic)) hostedCategories.Add(bic);
                }
            }
            var hostedCatIds = new HashSet<IdType>(hostedCategories.Select(c => ((IdType)(int)c)));

            // ---------- mappings: structural validation ----------
            JArray mappingArray = parameters["mappings"] as JArray;
            if (mappingArray == null || mappingArray.Count == 0)
                throw new Exception("mappings is required: [{ sourceRoomNumber, targetRoomNumbers: [...] }].");
            if (mappingArray.Count > 50)
                throw new Exception("At most 50 mappings per call.");

            var errors = new List<string>();
            var mappingSpecs = new List<KeyValuePair<string, List<string>>>();
            var seenSources = new HashSet<string>(StringComparer.Ordinal);
            var seenTargets = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < mappingArray.Count; i++)
            {
                JObject m = mappingArray[i] as JObject;
                string src = m?["sourceRoomNumber"]?.Value<string>()?.Trim();
                JArray tgts = m?["targetRoomNumbers"] as JArray;
                if (string.IsNullOrEmpty(src)) { errors.Add($"mappings[{i}]: sourceRoomNumber is required."); continue; }
                if (tgts == null || tgts.Count == 0) { errors.Add($"mappings[{i}] ({src}): targetRoomNumbers must be a non-empty array."); continue; }
                if (!seenSources.Add(src)) errors.Add($"mappings[{i}]: source room {src} appears in more than one mapping; merge them.");
                var list = new List<string>();
                foreach (JToken t in tgts)
                {
                    string tn = t?.Value<string>()?.Trim();
                    if (string.IsNullOrEmpty(tn)) { errors.Add($"mappings[{i}] ({src}): empty target room number."); continue; }
                    if (tn == src) { errors.Add($"mappings[{i}]: target {tn} equals its source."); continue; }
                    if (!seenTargets.Add(tn)) { errors.Add($"Target room {tn} is listed more than once."); continue; }
                    list.Add(tn);
                }
                mappingSpecs.Add(new KeyValuePair<string, List<string>>(src, list));
            }
            foreach (string t in seenTargets)
                if (seenSources.Contains(t))
                    errors.Add($"Room {t} is both a source and a target; chained copies are refused.");
            if (errors.Count > 0)
                throw new Exception("Invalid mappings:\n- " + string.Join("\n- ", errors));

            // ---------- phase filter ----------
            Phase phaseFilter = null;
            if (!string.IsNullOrWhiteSpace(phaseName))
            {
                phaseFilter = doc.Phases.Cast<Phase>().FirstOrDefault(p => p.Name == phaseName.Trim());
                if (phaseFilter == null)
                    throw new Exception($"Phase '{phaseName}' not found. Available: {string.Join(", ", doc.Phases.Cast<Phase>().Select(p => p.Name))}.");
            }

            // ---------- room resolution (deterministic, refuses ambiguity) ----------
            var overrides = new Dictionary<string, IdType>(StringComparer.Ordinal);
            JObject overrideObj = parameters["roomIdOverrides"] as JObject;
            if (overrideObj != null)
                foreach (var prop in overrideObj.Properties())
                {
                    IdType v;
                    try { v = prop.Value.Value<IdType>(); }
                    catch { throw new Exception($"roomIdOverrides['{prop.Name}'] must be a room ElementId number."); }
                    overrides[prop.Name.Trim()] = v;
                }

            List<Room> placedRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Location != null && r.Area > 0)
                .ToList();

            var resolved = new Dictionary<string, Room>(StringComparer.Ordinal);
            foreach (string number in seenSources.Concat(seenTargets))
            {
                Room r = CcResolveRoom(doc, number, placedRooms, overrides, phaseFilter, allowDesignOptionRooms, errors);
                if (r != null) resolved[number] = r;
            }
            foreach (string key in overrides.Keys)
                if (!seenSources.Contains(key) && !seenTargets.Contains(key))
                    errors.Add($"roomIdOverrides key '{key}' is not used by any mapping.");
            if (errors.Count > 0)
                throw new Exception("Room resolution failed (nothing was changed):\n- " + string.Join("\n- ", errors));

            // DesignOption.GetActiveDesignOptionId is resolved by reflection so the single csproj compiles
            // against every supported Revit API; if it is unavailable the state is reported as unknown.
            ElementId activeOption = ElementId.InvalidElementId;
            bool activeOptionKnown = false;
            try
            {
                MethodInfo getActive = typeof(DesignOption).GetMethod("GetActiveDesignOptionId",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Document) }, null);
                if (getActive != null)
                {
                    activeOption = getActive.Invoke(null, new object[] { doc }) as ElementId ?? ElementId.InvalidElementId;
                    activeOptionKnown = true;
                }
            }
            catch { activeOptionKnown = false; }
            bool mainModelActive = activeOption == ElementId.InvalidElementId;
            if (!dryRun && !mainModelActive)
                throw new Exception("A design option is active in the document. Switch the active option to Main Model before copying, so the copies are created in the main model.");

            // ---------- shapes ----------
            var shapes = new Dictionary<IdType, CcRoomShape>();
            foreach (Room r in resolved.Values)
            {
                IdType key = r.Id.GetIdValue();
                if (!shapes.ContainsKey(key)) shapes[key] = CcBuildShape(r);
            }

            // ---------- planning ----------
            var mappingResults = new JArray();
            var plannedPairs = new List<Tuple<JObject, CcRoomShape, CcRoomShape, List<CcSourceCeiling>, XYZ, CcReplacePlan>>();
            int preFailures = 0, skippedExisting = 0;
            var allSourceIds = new HashSet<IdType>();

            foreach (var spec in mappingSpecs)
            {
                Room srcRoom = resolved[spec.Key];
                CcRoomShape src = shapes[srcRoom.Id.GetIdValue()];
                var mapObj = new JObject
                {
                    ["sourceRoomNumber"] = spec.Key,
                    ["sourceRoom"] = CcRoomInfo(doc, src)
                };
                var mapWarnings = new JArray();
                var excluded = new JArray();
                string mappingError = null;

                // --- ceilings in source room ---
                var ceilings = new List<CcSourceCeiling>();
                var partial = new List<string>();
                foreach (Ceiling c in CcCeilingsNear(doc, src, boundaryTol, verticalTol))
                {
                    List<XYZ> pts; List<List<XYZ>> loops;
                    CcFootprint(c, out loops, out pts);
                    string rel = CcRelation(pts, loops, src, boundaryTol);
                    if (rel == "unrelated") continue;
                    string why = CcExclusionReason(doc, c, srcRoom);
                    if (rel == "partial")
                    {
                        if (why == null) partial.Add($"{c.Id.GetIdValue()} ({CcTypeName(doc, c)})");
                        else excluded.Add(CcExcluded(doc, c, "straddles room boundary; " + why));
                        continue;
                    }
                    if (why != null) { excluded.Add(CcExcluded(doc, c, why)); mapWarnings.Add($"Ceiling {c.Id.GetIdValue()} inside the room was NOT copied: {why}."); continue; }

                    Level cLevel = doc.GetElement(c.LevelId) as Level;
                    Parameter offP = c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    if (cLevel == null || offP == null)
                    {
                        excluded.Add(CcExcluded(doc, c, "ceiling has no level or no Height Offset From Level parameter"));
                        mapWarnings.Add($"Ceiling {c.Id.GetIdValue()} inside the room was NOT copied: no level/offset parameter.");
                        continue;
                    }
                    ceilings.Add(new CcSourceCeiling
                    {
                        Ceiling = c,
                        Box = c.get_BoundingBox(null),
                        OffsetFromRoomLevelFt = cLevel.Elevation + offP.AsDouble() - src.Level.Elevation
                    });
                }
                if (partial.Count > 0)
                    mappingError = "Ceiling(s) straddle the source room boundary and would be ambiguous to copy: " + string.Join(", ", partial)
                        + ". Split or trim them, or raise boundaryToleranceMm if they only overlap the wall.";

                // --- hosted fixtures (by host relation on the level-exact source ceilings) ---
                var otherHosted = new JArray();
                var hostedIdsAll = new HashSet<IdType>();
                List<FamilyInstance> nearSource = CcInstancesNear(doc, src, boundaryTol, verticalTol).ToList();
                foreach (CcSourceCeiling owner in ceilings)
                foreach (FamilyInstance fi in CcHostedInstances(doc, owner.Ceiling, nearSource))
                {
                    Element host = owner.Ceiling;
                    hostedIdsAll.Add(fi.Id.GetIdValue());
                    if (fi.SuperComponent != null) continue;
                    bool requested = fi.Category != null && hostedCatIds.Contains(fi.Category.Id.GetIdValue());
                    if (!requested)
                    {
                        otherHosted.Add(CcElementRef(doc, fi, $"hosted on ceiling {host.Id.GetIdValue()} but category not in hostedCategories"));
                        continue;
                    }
                    string why = CcExclusionReason(doc, fi, srcRoom);
                    XYZ fp = CcInstancePoint(fi);
                    if (why == null && (fp == null || CcClassifyPoint(fp, src.Loops, boundaryTol) < 0))
                        why = "hosted on a copied ceiling but located outside the source room";
                    if (why != null)
                    {
                        mappingError = mappingError ?? $"Fixture {fi.Id.GetIdValue()} hosted on ceiling {host.Id.GetIdValue()} cannot be copied ({why}); copying its host without it is refused.";
                        excluded.Add(CcExcluded(doc, fi, why));
                        continue;
                    }
                    owner.Fixtures.Add(fi);
                }

                // --- requested-category fixtures in the room that will NOT be copied ---
                var notCopied = new JArray();
                foreach (FamilyInstance fi in nearSource)
                {
                    if (fi.SuperComponent != null || hostedIdsAll.Contains(fi.Id.GetIdValue())) continue;
                    if (fi.Category == null || !hostedCatIds.Contains(fi.Category.Id.GetIdValue())) continue;
                    if (CcFixtureLevelId(fi) != srcRoom.LevelId) continue;
                    XYZ p = CcInstancePoint(fi);
                    if (p == null || !CcInBand(p.Z, src, verticalTol) || CcClassifyPoint(p, src.Loops, boundaryTol) < 0) continue;
                    string hostDesc = fi.Host == null ? "not hosted"
                        : $"hosted on {fi.Host.Category?.Name ?? fi.Host.GetType().Name} {fi.Host.Id.GetIdValue()} (not a copied ceiling)";
                    notCopied.Add(CcElementRef(doc, fi, hostDesc));
                }
                if (notCopied.Count > 0)
                    mapWarnings.Add($"{notCopied.Count} fixture(s) of the requested categories are in the room but not hosted on a copied ceiling; they are NOT copied (see notCopiedFixturesInRoom).");

                var ceilingArr = new JArray();
                foreach (var sc in ceilings)
                {
                    ceilingArr.Add(new JObject
                    {
                        ["id"] = sc.Ceiling.Id.GetIdValue(),
                        ["type"] = CcTypeName(doc, sc.Ceiling),
                        ["level"] = (doc.GetElement(sc.Ceiling.LevelId) as Level)?.Name,
                        ["heightAboveRoomLevelMm"] = Math.Round(sc.OffsetFromRoomLevelFt * CcMmPerFt, 1),
                        ["hostedFixtures"] = new JArray(sc.Fixtures.Select(f => CcElementRef(doc, f, null)))
                    });
                }
                mapObj["ceilings"] = ceilingArr;
                mapObj["excluded"] = excluded;
                mapObj["otherHostedElementsNotCopied"] = otherHosted;
                mapObj["notCopiedFixturesInRoom"] = notCopied;
                if (ceilings.Count == 0 && mappingError == null)
                    mappingError = "No copyable ceiling lies inside the source room (check the room's vertical extent / Upper Limit, or boundaryToleranceMm).";
                if (mappingError != null) mapObj["error"] = mappingError;

                // --- targets ---
                var targetArr = new JArray();
                foreach (string tn in spec.Value)
                {
                    CcRoomShape tgt = shapes[resolved[tn].Id.GetIdValue()];
                    var tObj = new JObject
                    {
                        ["targetRoomNumber"] = tn,
                        ["targetRoom"] = CcRoomInfo(doc, tgt)
                    };
                    var tWarnings = new JArray();
                    string tError = mappingError != null ? "Source mapping refused: " + mappingError : null;

                    XYZ delta = new XYZ(tgt.Center.X - src.Center.X, tgt.Center.Y - src.Center.Y, tgt.Level.Elevation - src.Level.Elevation);
                    tObj["translationMm"] = new JObject
                    {
                        ["dx"] = Math.Round(delta.X * CcMmPerFt, 2),
                        ["dy"] = Math.Round(delta.Y * CcMmPerFt, 2),
                        ["dz"] = Math.Round(delta.Z * CcMmPerFt, 2)
                    };
                    double dev = CcCongruence(src, tgt, delta);
                    tObj["boundaryMaxDeviationMm"] = Math.Round(dev * CcMmPerFt, 2);
                    tObj["areaM2"] = new JObject
                    {
                        ["source"] = Math.Round(UnitUtils.ConvertFromInternalUnits(src.Room.Area, UnitTypeId.SquareMeters), 3),
                        ["target"] = Math.Round(UnitUtils.ConvertFromInternalUnits(tgt.Room.Area, UnitTypeId.SquareMeters), 3)
                    };

                    if (tError == null && tgt.Level.Id == src.Level.Id)
                        tError = "Target room is on the same level as the source; this tool copies across levels only.";
                    if (tError == null && Math.Abs(delta.Z) * CcMmPerFt < 1.0)
                        tError = "Target level has the same elevation as the source level.";
                    if (tError == null && dev * CcMmPerFt > shapeTolMm)
                        tError = $"Room boundaries are not congruent under a pure translation (max deviation {Math.Round(dev * CcMmPerFt, 1)} mm > shapeToleranceMm {shapeTolMm}); refused to avoid misplacing ceilings.";
                    if (tError == null)
                    {
                        ElementId sp = src.Room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
                        ElementId tp = tgt.Room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
                        if (sp != null && tp != null && sp != tp)
                            tError = "Source and target rooms are in different phases.";
                    }
                    if (tError == null)
                    {
                        double tolZ = verticalTol;
                        foreach (var sc in ceilings)
                        {
                            double z = sc.Box != null ? sc.Box.Min.Z + delta.Z : tgt.Level.Elevation + sc.OffsetFromRoomLevelFt;
                            if (!CcInBand(z, tgt, tolZ))
                            {
                                tError = $"Ceiling {sc.Ceiling.Id.GetIdValue()} would land at {Math.Round(z * CcMmPerFt)} mm, outside the target room's vertical extent ({Math.Round(tgt.ZMin * CcMmPerFt)}..{Math.Round(tgt.ZMax * CcMmPerFt)} mm).";
                                break;
                            }
                        }
                    }

                    // --- existing content in the target (duplicate prevention; exact level + phase status) ---
                    var existingItems = new List<CcExisting>();
                    var ignoredInTarget = new JArray();
                    var existing = CcExistingInTarget(doc, tgt, ceilings, delta, hostedCatIds, boundaryTol, verticalTol, positionTol,
                        existingItems, ignoredInTarget);
                    tObj["existingInTarget"] = existing;
                    if (ignoredInTarget.Count > 0) tObj["ignoredInTarget"] = ignoredInTarget;

                    CcReplacePlan replacePlan = null;
                    if (replaceMode && tError == null && existingItems.Count > 0)
                    {
                        replacePlan = CcPlanReplace(doc, tgt, existingItems, hostedCatIds, boundaryTol);
                        tObj["replacePlan"] = replacePlan.Preview;
                    }

                    string status;
                    if (tError != null)
                    {
                        status = "failed";
                        tObj["error"] = tError;
                        preFailures++;
                    }
                    else if (existing.Count > 0 && !replaceMode)
                    {
                        string msg = $"Target room already contains {existing.Count} ceiling/fixture element(s) (see existingInTarget). Nothing is copied into it; remove or review them first.";
                        if (existingPolicy == "fail") { status = "failed"; tObj["error"] = msg; preFailures++; }
                        else { status = "skipped_existing"; tWarnings.Add(msg); skippedExisting++; }
                    }
                    else if (replacePlan != null && replacePlan.Problems.Count > 0)
                    {
                        status = "failed";
                        tObj["error"] = "existingPolicy='replace' refused for this target; nothing in it will be deleted or copied: "
                            + string.Join(" | ", replacePlan.Problems);
                        preFailures++;
                    }
                    else
                    {
                        status = "planned";
                        tObj["plannedCeilings"] = ceilings.Count;
                        tObj["plannedFixtures"] = ceilings.Sum(x => x.Fixtures.Count);
                        if (replaceMode)
                            tObj["plannedDeletions"] = replacePlan == null ? 0 : replacePlan.Authorized.Count;
                        plannedPairs.Add(Tuple.Create(tObj, src, tgt, ceilings, delta, replacePlan));
                    }
                    tObj["status"] = status;
                    tObj["warnings"] = tWarnings;
                    targetArr.Add(tObj);
                }
                mapObj["targets"] = targetArr;
                mapObj["warnings"] = mapWarnings;
                mappingResults.Add(mapObj);
                foreach (var sc in ceilings)
                {
                    allSourceIds.Add(sc.Ceiling.Id.GetIdValue());
                    foreach (var f in sc.Fixtures) allSourceIds.Add(f.Id.GetIdValue());
                }
            }

            // ---------- replace scopes must be disjoint from every source and from each other ----------
            if (replaceMode)
            {
                var claimed = new HashSet<IdType>();
                foreach (var pair in plannedPairs.ToList())
                {
                    CcReplacePlan rp = pair.Item6;
                    if (rp == null) continue;
                    var scope = rp.Authorized.Concat(rp.AllowedCascade).ToList();
                    var clash = scope.Where(v => allSourceIds.Contains(v) || claimed.Contains(v)).ToList();
                    if (clash.Count > 0)
                    {
                        pair.Item1["status"] = "failed";
                        pair.Item1["error"] = "existingPolicy='replace' refused: the deletion scope overlaps a source element or another target's scope ("
                            + string.Join(", ", clash.Take(10)) + "); nothing in this target will be deleted or copied.";
                        plannedPairs.Remove(pair);
                        preFailures++;
                        continue;
                    }
                    foreach (var v in scope) claimed.Add(v);
                }
            }

            var result = new JObject
            {
                ["dryRun"] = dryRun,
                ["atomic"] = atomic,
                ["existingPolicy"] = existingPolicy,
                ["hostedCategories"] = new JArray(hostedCategories.Select(c => c.ToString())),
                ["tolerancesMm"] = new JObject
                {
                    ["boundary"] = boundaryTolMm, ["vertical"] = verticalTolMm,
                    ["shape"] = shapeTolMm, ["position"] = positionTolMm
                },
                ["phaseFilter"] = phaseFilter?.Name,
                ["activeDesignOption"] = !activeOptionKnown ? "unknown (API unavailable)"
                    : mainModelActive ? "Main Model" : (doc.GetElement(activeOption)?.Name ?? activeOption.GetIdValue().ToString()),
                ["mappings"] = mappingResults
            };
            if (!activeOptionKnown)
                result["designOptionWarning"] = "The active design option could not be read in this Revit version; make sure Main Model is active, because copies are created in the active option.";

            // ---------- execution / trial ----------
            bool runCopies = plannedPairs.Count > 0 && (!dryRun || (verifyInDryRun && mainModelActive));
            if (dryRun && verifyInDryRun && !mainModelActive)
                result["note"] = "A design option is active, so the dry-run trial copy was skipped; switch to Main Model before executing.";
            if (!dryRun && atomic && preFailures > 0)
            {
                runCopies = false;
                result["committed"] = false;
                result["message"] = $"{preFailures} target(s) failed pre-checks; atomic=true, so nothing was {(replaceMode ? "deleted or " : "")}copied. Fix them or set atomic=false.";
            }

            int copiedPairs = 0, failedPairs = preFailures, copiedCeilings = 0, copiedFixtures = 0;
            int replacedPairs = 0, deletedElements = 0;
            if (runCopies)
            {
                using (var group = new TransactionGroup(doc, "Copy room ceilings with lighting"))
                {
                    group.Start();
                    foreach (var pair in plannedPairs)
                    {
                        JObject tObj = pair.Item1;
                        var elementResults = new JArray();
                        tObj["elements"] = elementResults;
                        string pairError = CcRunPair(doc, pair.Item2, pair.Item3, pair.Item4, pair.Item5, pair.Item6,
                            hostedCatIds, boundaryTol, verticalTol, positionTol, dryRun, elementResults, tObj);
                        if (pairError == null)
                        {
                            bool replaced = pair.Item6 != null && pair.Item6.Authorized.Count > 0;
                            tObj["status"] = dryRun ? (replaced ? "verified_trial_replace" : "verified_trial") : (replaced ? "replaced" : "copied");
                            if (replaced) { replacedPairs++; deletedElements += pair.Item6.Authorized.Count; }
                            copiedPairs++;
                            copiedCeilings += pair.Item4.Count;
                            copiedFixtures += pair.Item4.Sum(x => x.Fixtures.Count);
                        }
                        else
                        {
                            tObj["status"] = "failed";
                            tObj["error"] = pairError;
                            failedPairs++;
                        }
                    }

                    if (dryRun)
                    {
                        group.RollBack();
                        result["committed"] = false;
                        result["message"] = replaceMode
                            ? "Dry run: planned deletions, copies and verification were performed inside a transaction group that was rolled back. Element ids of trial copies are not reported; replacePlan lists exactly what would be deleted."
                            : "Dry run: copies were performed and verified inside a transaction group that was rolled back. Element ids of trial copies are not reported.";
                    }
                    else if (atomic && failedPairs > 0)
                    {
                        group.RollBack();
                        result["committed"] = false;
                        result["message"] = $"{failedPairs} target(s) failed; atomic=true, so the whole run (deletions and copies) was rolled back. Nothing was changed.";
                        foreach (var pair in plannedPairs)
                        {
                            string st = (string)pair.Item1["status"];
                            if (st == "copied" || st == "replaced")
                            {
                                pair.Item1["status"] = "rolled_back";
                                CcClearNewIds(pair.Item1["elements"]);
                            }
                        }
                        copiedPairs = 0; copiedCeilings = 0; copiedFixtures = 0; replacedPairs = 0; deletedElements = 0;
                    }
                    else
                    {
                        group.Assimilate();
                        result["committed"] = copiedPairs > 0;
                    }
                }
            }
            else if (result["committed"] == null)
            {
                result["committed"] = false;
                if (result["message"] == null)
                    result["message"] = dryRun
                        ? (plannedPairs.Count == 0 ? "Dry run: no target is eligible for copying." : "Dry run (plan only; verifyInDryRun=false).")
                        : "No target is eligible for copying; nothing was changed.";
            }

            result["summary"] = new JObject
            {
                ["targetsTotal"] = mappingSpecs.Sum(m => m.Value.Count),
                ["targetsPlanned"] = plannedPairs.Count,
                ["targetsCopiedOrVerified"] = copiedPairs,
                ["targetsSkippedExisting"] = skippedExisting,
                ["targetsFailed"] = failedPairs,
                ["ceilingsCopiedOrVerified"] = copiedCeilings,
                ["fixturesCopiedOrVerified"] = copiedFixtures
            };
            if (replaceMode)
            {
                var sum = (JObject)result["summary"];
                sum["targetsReplacedOrVerified"] = replacedPairs;
                sum["elementsDeletedOrTrialDeleted"] = deletedElements;
                sum["elementsPlannedForDeletion"] = plannedPairs.Where(p => p.Item6 != null).Sum(p => p.Item6.Authorized.Count);
            }
            return result;
        }

        /// <summary>
        /// Copies every planned ceiling (with its fixtures) for one source→target pair inside one
        /// Transaction, verifies the result, and commits. Returns null on success or an error text;
        /// the transaction is rolled back on any error.
        /// </summary>
        private string CcRunPair(Document doc, CcRoomShape src, CcRoomShape tgt, List<CcSourceCeiling> ceilings, XYZ delta,
            CcReplacePlan replace, HashSet<IdType> hostedCatIds, double boundaryTol, double verticalTol, double positionTol,
            bool dryRun, JArray elementResults, JObject tObj)
        {
            using (var t = new Transaction(doc, $"Copy ceilings {src.Room.Number} -> {tgt.Room.Number}"))
            {
                var failures = RfaUseRollbackOnError(t);
                t.Start();
                try
                {
                    // --- replace: delete the inspected scope first, in the same transaction as the copy ---
                    if (replace != null && replace.Authorized.Count > 0)
                    {
                        var ids = new List<ElementId>();
                        foreach (IdType v in replace.Authorized.OrderBy(v => v))
                        {
                            ElementId id = v.ToElementId();
                            if (doc.GetElement(id) == null)
                                throw new CcPairFailure($"Planned deletion target {v} no longer exists; replace refused.");
                            ids.Add(id);
                        }
                        ICollection<ElementId> deleted = doc.Delete(ids);
                        var deletedSet = new HashSet<IdType>((deleted ?? new List<ElementId>()).Select(d => d.GetIdValue()));
                        var unexpected = deletedSet.Where(v => !replace.Authorized.Contains(v) && !replace.AllowedCascade.Contains(v)).OrderBy(v => v).ToList();
                        if (unexpected.Count > 0)
                            throw new CcPairFailure($"Deleting the planned elements would also delete {unexpected.Count} uninspected element(s) ({string.Join(", ", unexpected.Take(20))}); replace refused.");
                        var missed = replace.Authorized.Where(v => !deletedSet.Contains(v)).OrderBy(v => v).ToList();
                        if (missed.Count > 0)
                            throw new CcPairFailure($"Revit did not delete planned element(s) {string.Join(", ", missed.Take(20))}; replace refused.");
                        doc.Regenerate();
                        tObj["replaceExecution"] = new JObject
                        {
                            ["deletedPlanned"] = replace.Authorized.Count,
                            ["deletedInternalDependents"] = deletedSet.Count - replace.Authorized.Count,
                            ["unexpectedDeletions"] = 0,
                            ["trialOnly"] = dryRun
                        };
                    }

                    var createdIds = new HashSet<IdType>();
                    foreach (var sc in ceilings)
                        CcCopyCeiling(doc, sc, src, tgt, delta, hostedCatIds, boundaryTol, positionTol, dryRun, elementResults, createdIds);

                    // --- replace: the target must now hold only the new copies ---
                    if (replace != null)
                    {
                        var after = new List<CcExisting>();
                        CcExistingInTarget(doc, tgt, ceilings, delta, hostedCatIds, boundaryTol, verticalTol, positionTol, after, null);
                        var leftovers = after.Where(i => !createdIds.Contains(i.Element.Id.GetIdValue()))
                            .Select(i => $"{i.Element.Category?.Name ?? i.Element.GetType().Name} {i.Element.Id.GetIdValue()}").ToList();
                        if (leftovers.Count > 0)
                            throw new CcPairFailure($"After replace the target still contains pre-existing element(s): {string.Join(", ", leftovers.Take(20))}.");
                    }

                    TransactionStatus st = t.Commit();
                    if (failures.Warnings.Count > 0) tObj["revitWarnings"] = new JArray(failures.Warnings);
                    if (st != TransactionStatus.Committed)
                        return $"Revit rolled the copy back ({st}){RfaFailureText(failures)}";
                    return null;
                }
                catch (Exception ex)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    if (failures.Warnings.Count > 0) tObj["revitWarnings"] = new JArray(failures.Warnings);
                    tObj.Remove("replaceExecution");
                    CcClearNewIds(elementResults);
                    return (ex is CcPairFailure ? "" : "Unexpected error: ") + ex.Message + " (pair rolled back)";
                }
            }
        }

        private void CcCopyCeiling(Document doc, CcSourceCeiling sc, CcRoomShape src, CcRoomShape tgt, XYZ delta,
            HashSet<IdType> hostedCatIds, double boundaryTol, double positionTol, bool dryRun, JArray elementResults,
            HashSet<IdType> createdIds)
        {
            Ceiling source = sc.Ceiling;
            var ids = new List<ElementId> { source.Id };
            ids.AddRange(sc.Fixtures.Select(f => f.Id));
            ICollection<ElementId> newIds = ElementTransformUtils.CopyElements(doc, ids, delta);

            // --- identify the copies ---
            var newElems = newIds.Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
            List<Ceiling> newCeilings = newElems.OfType<Ceiling>().ToList();
            if (newCeilings.Count != 1)
                throw new CcPairFailure($"Copying ceiling {source.Id.GetIdValue()} produced {newCeilings.Count} ceilings (expected 1).");
            Ceiling copy = newCeilings[0];
            createdIds.Add(copy.Id.GetIdValue());

            var fixtureCopies = new Dictionary<IdType, FamilyInstance>();
            var unmatched = newElems.OfType<FamilyInstance>().ToList();
            foreach (FamilyInstance f in sc.Fixtures)
            {
                XYZ expected = CcInstancePoint(f) + delta;
                FamilyInstance match = unmatched
                    .Where(n => n.Symbol.Id == f.Symbol.Id && CcInstancePoint(n) != null)
                    .OrderBy(n => CcInstancePoint(n).DistanceTo(expected))
                    .FirstOrDefault();
                if (match == null || CcInstancePoint(match).DistanceTo(expected) > Math.Max(positionTol, 5.0 / CcMmPerFt))
                    throw new CcPairFailure($"Copy of fixture {f.Id.GetIdValue()} on ceiling {source.Id.GetIdValue()} was not found at the expected position.");
                fixtureCopies[f.Id.GetIdValue()] = match;
                createdIds.Add(match.Id.GetIdValue());
                unmatched.Remove(match);
            }
            var dependents = new JArray();
            foreach (Element e in newElems)
            {
                if (e.Id == copy.Id || fixtureCopies.Values.Any(v => v.Id == e.Id)) continue;
                bool internalDependent = e is Opening || e is Sketch || e is SketchPlane || e is CurveElement || e.Category == null;
                if (!internalDependent)
                    throw new CcPairFailure($"Revit also copied {e.Category?.Name ?? e.GetType().Name} {e.Id.GetIdValue()} together with ceiling {source.Id.GetIdValue()}; unrequested elements are refused.");
                dependents.Add($"{e.GetType().Name} {(dryRun ? "" : e.Id.GetIdValue().ToString())}".Trim());
            }

            // --- re-level the copied ceiling to the target room level, keeping the world height ---
            Level srcCeilingLevel = doc.GetElement(source.LevelId) as Level;
            string levelAction = "unchanged";
            if (copy.LevelId != tgt.Level.Id)
            {
                Parameter lp = copy.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                if (lp == null || lp.IsReadOnly)
                    throw new CcPairFailure($"The copied ceiling's Level parameter is read-only in this Revit version; cannot move it to level {tgt.Level.Name}.");
                if (!lp.Set(tgt.Level.Id))
                    throw new CcPairFailure($"Revit refused to set the copied ceiling's level to {tgt.Level.Name}.");
                levelAction = $"{srcCeilingLevel?.Name} -> {tgt.Level.Name}";
            }
            Parameter op = copy.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
            if (op == null || op.IsReadOnly || !op.Set(sc.OffsetFromRoomLevelFt))
                throw new CcPairFailure("Could not set Height Offset From Level on the copied ceiling.");
            doc.Regenerate();

            // --- fixtures: schedule level follows the target level when the parameter is writable ---
            var fixtureLevelNotes = new Dictionary<IdType, string>();
            foreach (var kv in fixtureCopies)
            {
                FamilyInstance n = kv.Value;
                Parameter sl = n.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                if (sl == null || sl.IsReadOnly || sl.StorageType != StorageType.ElementId) continue;
                ElementId before = sl.AsElementId();
                if (before == tgt.Level.Id) continue;
                XYZ p0 = CcInstancePoint(n);
                sl.Set(tgt.Level.Id);
                doc.Regenerate();
                XYZ p1 = CcInstancePoint(n);
                if (p0 != null && p1 != null && p0.DistanceTo(p1) > positionTol)
                {
                    sl.Set(before);
                    doc.Regenerate();
                    fixtureLevelNotes[kv.Key] = "Schedule Level change moved the fixture; reverted to the source level.";
                }
                else fixtureLevelNotes[kv.Key] = $"Schedule Level set to {tgt.Level.Name}.";
            }

            // --- verification ---
            var problems = new List<string>();
            BoundingBoxXYZ nb = copy.get_BoundingBox(null);
            if (sc.Box == null || nb == null)
                problems.Add("ceiling bounding box unavailable");
            else
            {
                double dMin = nb.Min.DistanceTo(sc.Box.Min + delta), dMax = nb.Max.DistanceTo(sc.Box.Max + delta);
                if (dMin > positionTol || dMax > positionTol)
                    problems.Add($"ceiling bounding box off by {Math.Round(Math.Max(dMin, dMax) * CcMmPerFt, 1)} mm");
            }
            if (copy.LevelId != tgt.Level.Id) problems.Add("ceiling is not on the target room level");
            if (copy.GetTypeId() != source.GetTypeId()) problems.Add("ceiling type differs from source");
            List<XYZ> cPts; List<List<XYZ>> cLoops;
            CcFootprint(copy, out cLoops, out cPts);
            if (CcRelation(cPts, cLoops, tgt, boundaryTol) != "contained")
                problems.Add("copied ceiling is not contained in the target room");

            var fixtureResults = new JArray();
            foreach (FamilyInstance f in sc.Fixtures)
            {
                FamilyInstance n = fixtureCopies[f.Id.GetIdValue()];
                var fProblems = new List<string>();
                if (n.Host == null || n.Host.Id != copy.Id)
                    fProblems.Add($"host is {(n.Host == null ? "none" : n.Host.Id.GetIdValue().ToString())}, not the copied ceiling");
                XYZ exp = CcInstancePoint(f) + delta, act = CcInstancePoint(n);
                if (act == null || act.DistanceTo(exp) > positionTol)
                    fProblems.Add($"position off by {(act == null ? "?" : Math.Round(act.DistanceTo(exp) * CcMmPerFt, 1).ToString())} mm");
                if (act != null && CcClassifyPoint(act, tgt.Loops, boundaryTol) < 0)
                    fProblems.Add("outside the target room");
                problems.AddRange(fProblems.Select(p => $"fixture {f.Id.GetIdValue()}: {p}"));
                string note;
                fixtureLevelNotes.TryGetValue(f.Id.GetIdValue(), out note);
                fixtureResults.Add(new JObject
                {
                    ["sourceId"] = f.Id.GetIdValue(),
                    ["newId"] = dryRun ? null : (JToken)n.Id.GetIdValue(),
                    ["category"] = f.Category?.Name,
                    ["familyType"] = $"{f.Symbol?.FamilyName} : {f.Symbol?.Name}",
                    ["hostedOnCopiedCeiling"] = n.Host != null && n.Host.Id == copy.Id,
                    ["level"] = (doc.GetElement(n.LevelId) as Level)?.Name,
                    ["levelNote"] = note,
                    ["verified"] = fProblems.Count == 0
                });
            }

            var ceilingResult = new JObject
            {
                ["sourceId"] = source.Id.GetIdValue(),
                ["newId"] = dryRun ? null : (JToken)copy.Id.GetIdValue(),
                ["category"] = source.Category?.Name,
                ["type"] = CcTypeName(doc, source),
                ["levelChange"] = levelAction,
                ["heightAboveLevelMm"] = Math.Round(sc.OffsetFromRoomLevelFt * CcMmPerFt, 1),
                ["phaseCreatedPreserved"] = copy.CreatedPhaseId == source.CreatedPhaseId,
                ["autoCopiedDependents"] = dependents,
                ["fixtures"] = fixtureResults,
                ["verified"] = problems.Count == 0
            };
            elementResults.Add(ceilingResult);
            if (problems.Count > 0)
                throw new CcPairFailure($"Verification failed for ceiling {source.Id.GetIdValue()}: {string.Join("; ", problems)}.");
        }

        // ---------------------------------------------------------------- room resolution

        private static Room CcResolveRoom(Document doc, string number, List<Room> placed, Dictionary<string, IdType> overrides,
            Phase phaseFilter, bool allowDesignOptionRooms, List<string> errors)
        {
            IdType overrideId;
            if (overrides.TryGetValue(number, out overrideId))
            {
                Room r = doc.GetElement(overrideId.ToElementId()) as Room;
                if (r == null) { errors.Add($"roomIdOverrides['{number}'] = {overrideId} is not a Room."); return null; }
                if (r.Number != number) { errors.Add($"roomIdOverrides['{number}'] = {overrideId} is room '{r.Number}', not '{number}'."); return null; }
                if (r.Location == null || r.Area <= 0) { errors.Add($"Room {number} ({overrideId}) is not placed or not enclosed."); return null; }
                if (phaseFilter != null && r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() != phaseFilter.Id)
                { errors.Add($"Room {number} ({overrideId}) is not in phase '{phaseFilter.Name}'."); return null; }
                return r;
            }

            var byNumber = placed.Where(r => r.Number == number).ToList();
            var candidates = byNumber
                .Where(r => phaseFilter == null || r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == phaseFilter.Id)
                .Where(r => allowDesignOptionRooms || r.DesignOption == null)
                .ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0)
            {
                errors.Add(byNumber.Count == 0
                    ? $"No placed room numbered '{number}'."
                    : $"Room '{number}' exists ({string.Join("; ", byNumber.Select(r => CcCandidateText(doc, r)))}) but is excluded by the phase/design-option filter; use phaseName, allowDesignOptionRooms or roomIdOverrides.");
                return null;
            }
            errors.Add($"Room number '{number}' is ambiguous ({candidates.Count} placed rooms: {string.Join("; ", candidates.Select(r => CcCandidateText(doc, r)))}). Pass phaseName or roomIdOverrides.");
            return null;
        }

        private static string CcCandidateText(Document doc, Room r)
        {
            string phase = (doc.GetElement(r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase)?.Name;
            string opt = r.DesignOption == null ? "Main Model" : r.DesignOption.Name;
            return $"id {r.Id.GetIdValue()}, level {r.Level?.Name}, phase {phase}, option {opt}";
        }

        private static JObject CcRoomInfo(Document doc, CcRoomShape s)
        {
            Room r = s.Room;
            return new JObject
            {
                ["id"] = r.Id.GetIdValue(),
                ["number"] = r.Number,
                ["name"] = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                ["level"] = s.Level.Name,
                ["levelElevationMm"] = Math.Round(s.Level.Elevation * CcMmPerFt, 1),
                ["phase"] = (doc.GetElement(r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase)?.Name,
                ["designOption"] = r.DesignOption == null ? "Main Model" : r.DesignOption.Name,
                ["verticalExtentMm"] = new JArray(Math.Round(s.ZMin * CcMmPerFt, 1), Math.Round(s.ZMax * CcMmPerFt, 1))
            };
        }

        // ---------------------------------------------------------------- geometry

        private static CcRoomShape CcBuildShape(Room room)
        {
            var s = new CcRoomShape { Room = room, Level = room.Level };
            if (s.Level == null) throw new Exception($"Room {room.Number} has no level.");
            var opts = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            IList<IList<BoundarySegment>> segs = room.GetBoundarySegments(opts);
            if (segs != null)
                foreach (var loop in segs)
                {
                    var pts = new List<XYZ>();
                    foreach (var seg in loop)
                    {
                        IList<XYZ> tess = seg.GetCurve().Tessellate();
                        for (int i = 0; i < tess.Count - 1; i++) pts.Add(tess[i]);
                    }
                    if (pts.Count >= 3) { s.Loops.Add(pts); s.Points.AddRange(pts); }
                }
            if (s.Points.Count == 0) throw new Exception($"Room {room.Number} has no boundary.");
            s.MinX = s.Points.Min(p => p.X); s.MaxX = s.Points.Max(p => p.X);
            s.MinY = s.Points.Min(p => p.Y); s.MaxY = s.Points.Max(p => p.Y);
            s.Center = new XYZ((s.MinX + s.MaxX) / 2, (s.MinY + s.MaxY) / 2, 0);
            BoundingBoxXYZ bb = room.get_BoundingBox(null);
            s.ZMin = bb?.Min.Z ?? s.Level.Elevation;
            s.ZMax = bb?.Max.Z ?? (s.Level.Elevation + room.UnboundedHeight);
            return s;
        }

        /// <summary>Max distance between the translated source boundary and the target boundary, both directions.</summary>
        private static double CcCongruence(CcRoomShape src, CcRoomShape tgt, XYZ delta)
        {
            var shift = new XYZ(delta.X, delta.Y, 0);
            var srcShifted = src.Loops.Select(l => l.Select(p => p + shift).ToList()).ToList();
            double dev = 0;
            foreach (var p in src.Points) dev = Math.Max(dev, CcDistToLoops(p + shift, tgt.Loops));
            foreach (var p in tgt.Points) dev = Math.Max(dev, CcDistToLoops(p, srcShifted));
            return dev;
        }

        private static bool CcInBand(double z, CcRoomShape s, double tol) => z >= s.ZMin - tol && z <= s.ZMax + tol;

        /// <summary>+1 strictly inside (beyond tol), 0 within tol of the boundary, -1 strictly outside.</summary>
        private static int CcClassifyPoint(XYZ p, List<List<XYZ>> loops, double tol)
        {
            if (CcDistToLoops(p, loops) <= tol) return 0;
            return CcInside(p, loops) ? 1 : -1;
        }

        private static bool CcInside(XYZ p, List<List<XYZ>> loops)
        {
            bool inside = false;
            foreach (var loop in loops)
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    XYZ a = loop[i], b = loop[j];
                    if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
            return inside;
        }

        private static double CcDistToLoops(XYZ p, List<List<XYZ>> loops)
        {
            double best = double.MaxValue;
            foreach (var loop in loops)
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    double ax = loop[j].X, ay = loop[j].Y, bx = loop[i].X, by = loop[i].Y;
                    double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
                    double tt = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - ax) * vx + (p.Y - ay) * vy) / len2));
                    double dx = p.X - (ax + tt * vx), dy = p.Y - (ay + tt * vy);
                    best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
                }
            return best;
        }

        /// <summary>"contained" | "partial" | "unrelated" for an element footprint against a room.</summary>
        private static string CcRelation(List<XYZ> pts, List<List<XYZ>> footprintLoops, CcRoomShape room, double tol)
        {
            if (pts == null || pts.Count == 0) return "unrelated";
            int inside = 0, outside = 0;
            foreach (var p in pts)
            {
                int c = CcClassifyPoint(p, room.Loops, tol);
                if (c > 0) inside++; else if (c < 0) outside++;
            }
            if (outside == 0) return "contained";
            bool roomInsideFootprint = footprintLoops != null && footprintLoops.Count > 0
                && room.Points.Any(p => CcClassifyPoint(p, footprintLoops, tol) > 0);
            return inside > 0 || roomInsideFootprint ? "partial" : "unrelated";
        }

        /// <summary>Bottom-face loops (lowest downward planar faces) and their points; falls back to all edge points.</summary>
        private static void CcFootprint(Element e, out List<List<XYZ>> loops, out List<XYZ> pts)
        {
            loops = new List<List<XYZ>>();
            pts = new List<XYZ>();
            var faces = new List<PlanarFace>();
            var allEdgePts = new List<XYZ>();
            GeometryElement ge = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (ge == null) return;
            foreach (Solid solid in CcSolids(ge))
            {
                foreach (Face f in solid.Faces)
                {
                    PlanarFace pf = f as PlanarFace;
                    if (pf != null && pf.FaceNormal.Z < -0.99) faces.Add(pf);
                }
                foreach (Edge ed in solid.Edges) allEdgePts.AddRange(ed.Tessellate());
            }
            if (faces.Count == 0) { pts = allEdgePts; return; }
            double zMin = faces.Min(f => f.Origin.Z);
            foreach (PlanarFace pf in faces.Where(f => f.Origin.Z <= zMin + 1.0 / CcMmPerFt))
                foreach (EdgeArray ea in pf.EdgeLoops)
                {
                    var loop = new List<XYZ>();
                    foreach (Edge ed in ea)
                    {
                        IList<XYZ> tess = ed.AsCurveFollowingFace(pf).Tessellate();
                        for (int i = 0; i < tess.Count - 1; i++) loop.Add(tess[i]);
                    }
                    if (loop.Count >= 3) { loops.Add(loop); pts.AddRange(loop); }
                }
        }

        private static IEnumerable<Solid> CcSolids(GeometryElement ge)
        {
            foreach (GeometryObject go in ge)
            {
                Solid s = go as Solid;
                if (s != null && s.Volume > 1e-9) { yield return s; continue; }
                GeometryInstance gi = go as GeometryInstance;
                if (gi != null)
                    foreach (Solid inner in CcSolids(gi.GetInstanceGeometry())) yield return inner;
            }
        }

        private static XYZ CcInstancePoint(FamilyInstance fi)
        {
            LocationPoint lp = fi.Location as LocationPoint;
            if (lp != null) return lp.Point;
            BoundingBoxXYZ bb = fi.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) * 0.5;
        }

        private static Outline CcOutline(CcRoomShape s, double tol, double vtol, XYZ shift = null)
        {
            XYZ d = shift ?? XYZ.Zero;
            return new Outline(new XYZ(s.MinX - tol + d.X, s.MinY - tol + d.Y, s.ZMin - vtol),
                               new XYZ(s.MaxX + tol + d.X, s.MaxY + tol + d.Y, s.ZMax + vtol));
        }

        /// <summary>
        /// Ceilings near a room, restricted to Ceiling.LevelId == Room.LevelId. The vertical band may be widened
        /// by vtol, but the level gate means a widened band never admits a ceiling of an adjacent level.
        /// </summary>
        private static IEnumerable<Ceiling> CcCeilingsNear(Document doc, CcRoomShape s, double tol, double vtol)
        {
            ElementId roomLevelId = s.Room.LevelId;
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Ceilings)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(CcOutline(s, tol, vtol)))
                .OfType<Ceiling>()
                .Where(c => c.LevelId == roomLevelId)
                .Where(c => { var b = c.get_BoundingBox(null); return b != null && CcInBand(b.Min.Z, s, vtol); })
                .OrderBy(c => c.Id.GetIdValue())
                .ToList();
        }

        /// <summary>
        /// Level a fixture belongs to for room discovery: its host ceiling's LevelId when hosted on a ceiling
        /// (never its own level in that case), otherwise its own LevelId / Schedule Level; Invalid if unknown.
        /// </summary>
        private static ElementId CcFixtureLevelId(FamilyInstance fi)
        {
            Element host = fi.Host;
            if (host is Ceiling) return host.LevelId ?? ElementId.InvalidElementId;
            if (fi.LevelId != null && fi.LevelId != ElementId.InvalidElementId) return fi.LevelId;
            Parameter sl = fi.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
            if (sl != null && sl.StorageType == StorageType.ElementId)
            {
                ElementId id = sl.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) return id;
            }
            return ElementId.InvalidElementId;
        }

        /// <summary>Family instances whose Host is exactly this ceiling (dependents ∪ spatially found), ordered by id.</summary>
        private static List<FamilyInstance> CcHostedInstances(Document doc, Ceiling c, IEnumerable<FamilyInstance> near)
        {
            var found = new Dictionary<IdType, FamilyInstance>();
            try
            {
                foreach (ElementId id in c.GetDependentElements(new ElementClassFilter(typeof(FamilyInstance))))
                    if (doc.GetElement(id) is FamilyInstance fi && fi.Host != null && fi.Host.Id == c.Id)
                        found[fi.Id.GetIdValue()] = fi;
            }
            catch { /* fall back to the spatial set below */ }
            foreach (FamilyInstance fi in near)
                if (fi.Host != null && fi.Host.Id == c.Id) found[fi.Id.GetIdValue()] = fi;
            return found.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        }

        private static ElementId CcRoomPhaseId(Room r)
        {
            ElementId id = r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
            return id ?? ElementId.InvalidElementId;
        }

        /// <summary>Phase status text in the given phase; present = New / Existing / Temporary (or phase unknown).</summary>
        private static string CcPhaseStatus(Element e, ElementId phaseId, out bool present, out bool isNew)
        {
            present = true; isNew = false;
            if (phaseId == null || phaseId == ElementId.InvalidElementId) return "unknown";
            try
            {
                ElementOnPhaseStatus st = e.GetPhaseStatus(phaseId);
                present = st == ElementOnPhaseStatus.New || st == ElementOnPhaseStatus.Existing || st == ElementOnPhaseStatus.Temporary;
                isNew = st == ElementOnPhaseStatus.New;
                return st.ToString();
            }
            catch (Exception ex) { return "unreadable (" + ex.Message + ")"; }
        }

        private static IEnumerable<FamilyInstance> CcInstancesNear(Document doc, CcRoomShape s, double tol, double vtol)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WherePasses(new BoundingBoxIntersectsFilter(CcOutline(s, tol, vtol)))
                .Cast<FamilyInstance>()
                .OrderBy(f => f.Id.GetIdValue())
                .ToList();
        }

        /// <summary>Why an element must not be copied (null if it may be).</summary>
        private static string CcExclusionReason(Document doc, Element e, Room sourceRoom)
        {
            if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId)
                return $"member of group {e.GroupId.GetIdValue()}";
            if (e.DesignOption != null)
                return $"belongs to design option '{e.DesignOption.Name}'";
            ElementId phaseId = sourceRoom.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
            if (phaseId != null && phaseId != ElementId.InvalidElementId)
            {
                ElementOnPhaseStatus st = e.GetPhaseStatus(phaseId);
                if (st != ElementOnPhaseStatus.New && st != ElementOnPhaseStatus.Existing && st != ElementOnPhaseStatus.Temporary)
                    return $"phase status {st} in the room's phase";
            }
            return null;
        }

        /// <summary>
        /// Ceilings and requested-category fixtures already present in a target room. Only elements on exactly the
        /// target room's level (ceilings by LevelId, fixtures via their host ceiling's level) whose phase status in
        /// the target room's phase is New / Existing / Temporary count. Level-exact elements that are Demolished,
        /// Past or Future in that phase are added to <paramref name="ignored"/> instead.
        /// </summary>
        private JArray CcExistingInTarget(Document doc, CcRoomShape tgt, List<CcSourceCeiling> sources, XYZ delta,
            HashSet<IdType> hostedCatIds, double tol, double vtol, double posTol, List<CcExisting> items, JArray ignored)
        {
            var arr = new JArray();
            double dupTol = Math.Max(posTol, 10.0 / CcMmPerFt);
            ElementId levelId = tgt.Room.LevelId;
            ElementId phaseId = CcRoomPhaseId(tgt.Room);
            foreach (Ceiling c in CcCeilingsNear(doc, tgt, tol, vtol))
            {
                List<XYZ> pts; List<List<XYZ>> loops;
                CcFootprint(c, out loops, out pts);
                string rel = CcRelation(pts, loops, tgt, tol);
                if (rel == "unrelated") continue;
                bool present, isNew;
                string st = CcPhaseStatus(c, phaseId, out present, out isNew);
                if (!present)
                {
                    JObject ig = CcElementRef(doc, c, $"ceiling with phase status {st} in the target room's phase; not counted as existing and never deleted");
                    ignored?.Add(ig);
                    continue;
                }
                BoundingBoxXYZ b = c.get_BoundingBox(null);
                bool dup = b != null && sources.Any(s => s.Box != null && s.Ceiling.GetTypeId() == c.GetTypeId()
                    && b.Min.DistanceTo(s.Box.Min + delta) <= dupTol && b.Max.DistanceTo(s.Box.Max + delta) <= dupTol);
                JObject o = CcElementRef(doc, c, rel == "partial" ? "ceiling overlaps the target room" : "ceiling inside the target room");
                o["phaseStatus"] = st;
                o["looksLikeCopyOfSource"] = dup;
                arr.Add(o);
                items?.Add(new CcExisting { Element = c, IsCeiling = true, Relation = rel });
            }
            var expectedFixtures = sources.SelectMany(s => s.Fixtures).ToList();
            foreach (FamilyInstance fi in CcInstancesNear(doc, tgt, tol, vtol))
            {
                if (fi.SuperComponent != null || fi.Category == null || !hostedCatIds.Contains(fi.Category.Id.GetIdValue())) continue;
                if (CcFixtureLevelId(fi) != levelId) continue;
                XYZ p = CcInstancePoint(fi);
                if (p == null || !CcInBand(p.Z, tgt, vtol) || CcClassifyPoint(p, tgt.Loops, tol) < 0) continue;
                bool present, isNew;
                string st = CcPhaseStatus(fi, phaseId, out present, out isNew);
                if (!present)
                {
                    ignored?.Add(CcElementRef(doc, fi, $"fixture with phase status {st} in the target room's phase; not counted as existing and never deleted"));
                    continue;
                }
                bool dup = expectedFixtures.Any(f => f.Symbol.Id == fi.Symbol.Id && CcInstancePoint(f) != null
                    && (CcInstancePoint(f) + delta).DistanceTo(p) <= dupTol);
                JObject o = CcElementRef(doc, fi, fi.Host == null ? "fixture in the target room (not hosted)" : $"fixture in the target room (host {fi.Host.Id.GetIdValue()})");
                o["phaseStatus"] = st;
                o["looksLikeCopyOfSource"] = dup;
                arr.Add(o);
                items?.Add(new CcExisting { Element = fi, IsCeiling = false, Relation = "in-room" });
            }
            return arr;
        }

        /// <summary>
        /// Why an existing target element may not be deleted by existingPolicy='replace' (null if it may).
        /// Only phase-New, ungrouped, main-model, unpinned elements on the target room level qualify.
        /// </summary>
        private static string CcDeleteBlocker(Document doc, Element e, ElementId phaseId)
        {
            if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) return $"member of group {e.GroupId.GetIdValue()}";
            if (e.DesignOption != null) return $"belongs to design option '{e.DesignOption.Name}'";
            if (e.Pinned) return "pinned";
            if (phaseId == null || phaseId == ElementId.InvalidElementId) return "the target room's phase is unknown, so phase status cannot be checked";
            bool present, isNew;
            string st = CcPhaseStatus(e, phaseId, out present, out isNew);
            if (!isNew) return $"phase status {st} in the target room's phase (only elements created in that phase may be replaced)";
            if (e.DemolishedPhaseId != null && e.DemolishedPhaseId != ElementId.InvalidElementId) return "has a Phase Demolished value";
            if (doc.IsWorkshared)
            {
                try
                {
                    if (WorksharingUtils.GetCheckoutStatus(doc, e.Id) == CheckoutStatus.OwnedByOtherUser) return "owned by another user";
                }
                catch { return "worksharing checkout status could not be read"; }
            }
            return null;
        }

        private static bool CcIsSketchCategory(Element d)
        {
            if (d.Category == null) return true;
            IdType cid = d.Category.Id.GetIdValue();
            if (cid >= 0) return false;
            return ((BuiltInCategory)(int)cid).ToString().IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// existingPolicy='replace': build the exact deletion scope for one target and inspect every dependent.
        /// Any element that cannot be safely and completely scoped goes into Problems (the target is then refused).
        /// </summary>
        private CcReplacePlan CcPlanReplace(Document doc, CcRoomShape tgt, List<CcExisting> existing, HashSet<IdType> hostedCatIds, double tol)
        {
            var plan = new CcReplacePlan();
            ElementId phaseId = CcRoomPhaseId(tgt.Room);
            ElementId levelId = tgt.Room.LevelId;
            var ceilingIds = new HashSet<IdType>();

            // 1) ceilings: fully contained, level-exact, deletable
            foreach (CcExisting item in existing.Where(i => i.IsCeiling))
            {
                Element c = item.Element;
                IdType cid = c.Id.GetIdValue();
                if (item.Relation != "contained")
                { plan.Problems.Add($"ceiling {cid} straddles/overlaps the target room boundary (shared ceilings are never deleted)"); continue; }
                if (c.LevelId != levelId)
                { plan.Problems.Add($"ceiling {cid} is not on the target room level"); continue; }
                string why = CcDeleteBlocker(doc, c, phaseId);
                if (why != null) { plan.Problems.Add($"ceiling {cid} cannot be deleted: {why}"); continue; }
                plan.Ceilings.Add((Ceiling)c);
                plan.Authorized.Add(cid);
                ceilingIds.Add(cid);
            }

            // 2) breadth-first inspection of everything that would cascade with those ceilings
            var internalDeps = new JArray();
            var pendingSubs = new List<FamilyInstance>();
            var visited = new HashSet<IdType>(plan.Authorized);
            var queue = new Queue<Element>(plan.Ceilings.Cast<Element>());
            const int maxInspected = 5000;
            while (queue.Count > 0 && plan.Problems.Count < 50)
            {
                Element parent = queue.Dequeue();
                IList<ElementId> deps;
                try { deps = parent.GetDependentElements(null); }
                catch (Exception ex) { plan.Problems.Add($"dependents of {parent.Id.GetIdValue()} could not be read ({ex.Message})"); continue; }
                foreach (ElementId did in deps)
                {
                    IdType dv = did.GetIdValue();
                    if (!visited.Add(dv)) continue;
                    if (visited.Count > maxInspected) { plan.Problems.Add($"more than {maxInspected} dependent elements; scope too large to inspect"); break; }
                    Element d = doc.GetElement(did);
                    if (d == null) continue;
                    string desc = $"{d.Category?.Name ?? d.GetType().Name} {dv}";

                    FamilyInstance fi = d as FamilyInstance;
                    if (fi != null)
                    {
                        if (fi.SuperComponent != null)
                        {
                            pendingSubs.Add(fi);
                            plan.AllowedCascade.Add(dv);
                            queue.Enqueue(fi);
                            continue;
                        }
                        bool hostedOnPlanned = fi.Host != null && ceilingIds.Contains(fi.Host.Id.GetIdValue());
                        bool requested = fi.Category != null && hostedCatIds.Contains(fi.Category.Id.GetIdValue());
                        if (!hostedOnPlanned || !requested)
                        {
                            plan.Problems.Add($"{desc} ({CcTypeName(doc, fi)}) depends on {parent.Id.GetIdValue()} but is not a requested-category fixture hosted on a replaced ceiling; deleting would remove it");
                            continue;
                        }
                        string why = CcDeleteBlocker(doc, fi, phaseId);
                        XYZ p = CcInstancePoint(fi);
                        if (why == null && (p == null || CcClassifyPoint(p, tgt.Loops, tol) < 0)) why = "located outside the target room";
                        if (why != null) { plan.Problems.Add($"fixture {dv} hosted on ceiling {fi.Host.Id.GetIdValue()} cannot be deleted: {why}"); continue; }
                        plan.Fixtures.Add(fi);
                        plan.Authorized.Add(dv);
                        queue.Enqueue(fi);
                        continue;
                    }

                    bool internalDep =
                        d is Sketch || d is SketchPlane
                        || (d is CurveElement && CcIsSketchCategory(d))
                        || (d is Opening op && op.Host != null && ceilingIds.Contains(op.Host.Id.GetIdValue()))
                        || (d.Category == null && !(d is View) && !(d is ElementType));
                    if (!internalDep)
                    {
                        plan.Problems.Add($"{desc} ({d.GetType().Name}) depends on {parent.Id.GetIdValue()} and would be deleted with it; only ceilings and requested hostedCategories may be removed");
                        continue;
                    }
                    plan.AllowedCascade.Add(dv);
                    internalDeps.Add(new JObject { ["id"] = dv, ["class"] = d.GetType().Name, ["category"] = d.Category?.Name, ["dependsOn"] = parent.Id.GetIdValue() });
                    queue.Enqueue(d);
                }
            }

            // 3) nested sub-components may only cascade with an authorized fixture
            foreach (FamilyInstance sub in pendingSubs)
            {
                FamilyInstance root = sub;
                while (root.SuperComponent is FamilyInstance sup) root = sup;
                if (!plan.Authorized.Contains(root.Id.GetIdValue()))
                    plan.Problems.Add($"nested component {sub.Id.GetIdValue()} belongs to {root.Id.GetIdValue()}, which is not in the deletion scope");
            }

            // 4) every existing requested fixture must be removed by the plan, otherwise the target cannot be fully replaced
            foreach (CcExisting item in existing.Where(i => !i.IsCeiling))
                if (!plan.Authorized.Contains(item.Element.Id.GetIdValue()))
                {
                    FamilyInstance fi = item.Element as FamilyInstance;
                    string host = fi?.Host == null ? "not hosted" : $"host {fi.Host.Id.GetIdValue()}";
                    plan.Problems.Add($"requested-category fixture {item.Element.Id.GetIdValue()} ({host}) is not on a replaceable target ceiling; replace never removes it");
                }

            plan.Preview = new JObject
            {
                ["refused"] = plan.Problems.Count > 0,
                ["problems"] = new JArray(plan.Problems),
                ["deleteCeilings"] = new JArray(plan.Ceilings.Select(c =>
                {
                    JObject o = CcElementRef(doc, c, "fully contained in the target room; phase New");
                    o["level"] = (doc.GetElement(c.LevelId) as Level)?.Name;
                    return o;
                })),
                ["deleteFixtures"] = new JArray(plan.Fixtures.Select(f =>
                {
                    JObject o = CcElementRef(doc, f, "requested category, hosted on a replaced ceiling");
                    o["hostId"] = f.Host?.Id.GetIdValue();
                    return o;
                })),
                ["internalDependents"] = internalDeps,
                ["elementsToDelete"] = plan.Problems.Count > 0 ? 0 : plan.Authorized.Count,
                ["internalDependentsToCascade"] = plan.Problems.Count > 0 ? 0 : plan.AllowedCascade.Count
            };
            return plan;
        }

        // ---------------------------------------------------------------- small helpers

        private static double CcPositive(JObject p, string name, double fallback)
        {
            JToken t = p[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            double v = t.Value<double>();
            if (double.IsNaN(v) || v <= 0 || v > 5000) throw new Exception($"{name} must be > 0 and <= 5000 mm.");
            return v;
        }

        private static string CcTypeName(Document doc, Element e)
        {
            ElementType t = doc.GetElement(e.GetTypeId()) as ElementType;
            return t == null ? null : $"{t.FamilyName} : {t.Name}";
        }

        private static JObject CcElementRef(Document doc, Element e, string reason)
        {
            var o = new JObject
            {
                ["id"] = e.Id.GetIdValue(),
                ["category"] = e.Category?.Name,
                ["type"] = CcTypeName(doc, e)
            };
            if (reason != null) o["reason"] = reason;
            return o;
        }

        private static JObject CcExcluded(Document doc, Element e, string reason) => CcElementRef(doc, e, reason);

        /// <summary>Rolled-back copies no longer exist: blank every newId in the element report.</summary>
        private static void CcClearNewIds(JToken token)
        {
            if (token is JObject o)
            {
                if (o["newId"] != null) o["newId"] = null;
                foreach (var prop in o.Properties()) CcClearNewIds(prop.Value);
            }
            else if (token is JArray a)
                foreach (JToken child in a) CcClearNewIds(child);
        }
    }
}
