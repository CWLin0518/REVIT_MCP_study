using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
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
        // redraw_room_ceilings_with_lighting
        //
        // Rebuilds native ceilings and their ceiling-hosted fixtures in target rooms from data
        // EXTRACTED from a source room. Nothing is copied: no ElementTransformUtils.CopyElement(s),
        // no copy/paste, no duplication. Every target element is a brand-new native element:
        //   * ceilings  -> Ceiling.Create(doc, profileLoops, sourceCeilingTypeId, targetLevelId)
        //   * fixtures  -> Document.Create.NewFamilyInstance(...) against the NEW target ceiling
        //                  (face reference for face/work-plane-based families, host element for
        //                  ceiling-based OneLevelBasedHosted families)
        //
        // Read-only helpers shared with copy_room_ceilings_with_lighting (room resolution, room
        // shapes, point/loop classification, level-exact discovery) are reused; the write path is
        // entirely separate.
        //
        // Safety model (refuse rather than approximate):
        //  * Rooms resolved deterministically up front (phaseName, allowDesignOptionRooms,
        //    roomIdOverrides); any unknown / ambiguous number aborts before any transaction.
        //  * Source extraction is level-exact (Ceiling.LevelId == Room.LevelId) and phase-exact
        //    (status New/Existing in the room's phase, no Phase Demolished). Only ceilings fully
        //    inside the source room are redrawn; a straddling ceiling refuses the mapping.
        //  * Profiles come from the ceiling's native sketch (all loops incl. holes). Only bound Line
        //    and Arc curves on a horizontal sketch plane are supported. Sloped / shape-edited /
        //    non-planar geometry, extra sketch elements (e.g. slope arrows), groups, design options,
        //    pinned or other-user-owned elements, and any dependent/hosted element that this tool
        //    would not recreate (openings, tags, other categories, nested components of unrequested
        //    hosts, MEP-connected fixtures) refuse the mapping. Requested fixtures hosted on a
        //    source ceiling but located outside the source room refuse that ceiling (and mapping).
        //  * Translation = room finish-boundary centre difference in XY; the target level replaces
        //    the source level and "Height Offset From Level" is reproduced. Boundaries must be
        //    congruent under that translation (shapeToleranceMm).
        //  * existingPolicy 'fail' (default) | 'skip': a target that already holds level-exact,
        //    phase-present ceilings or requested fixtures is never written to. This tool never
        //    deletes anything.
        //  * Every new element is verified before commit (type/symbol, exact level, profile loop and
        //    hole counts, curve-by-curve geometry, height, bounding box, containment, host = new
        //    ceiling, host face, position, full orientation/flip/mirror state, phase, parameters).
        //  * dryRun=true (default) + verifyInDryRun performs the full create+verify inside a
        //    TransactionGroup that is always rolled back; trial ids are never reported.
        //    atomic=true (default) commits only if every target passes. Created ids are reported
        //    only after the final commit.
        // ------------------------------------------------------------------------------------

        private sealed class RrParamValue
        {
            public string Key;
            public string Name;
            public StorageType Storage;
            public double D;
            public int I;
            public string S;
            public ElementId E;
        }

        private sealed class RrFixtureSpec
        {
            public FamilyInstance Source;
            public FamilySymbol Symbol;
            public bool FaceHosted;
            public string Placement;
            public XYZ Origin;
            public Transform Xf;
            public XYZ Hand, Facing;
            public bool HandFlipped, FacingFlipped, Mirrored;
            public XYZ FaceNormal;
            public double FaceZ;
            public int SubComponentCount;
            public bool OwnLevelIsRoomLevel;
            public ElementId CreatedPhaseId;
            public List<RrParamValue> Params = new List<RrParamValue>();
            public List<string> ParamsNotCopied = new List<string>();
        }

        private sealed class RrCeilingSpec
        {
            public Ceiling Source;
            public ElementId TypeId;
            public string TypeName;
            public double OffsetFt;
            public List<List<Curve>> Loops;
            public int OuterLoops, HoleLoops;
            public double PerimeterFt;
            public BoundingBoxXYZ Box;
            public double BottomArea;
            public ElementId CreatedPhaseId;
            public List<RrParamValue> Params = new List<RrParamValue>();
            public List<string> ParamsNotCopied = new List<string>();
            public List<RrFixtureSpec> Fixtures = new List<RrFixtureSpec>();
        }

        private sealed class RrFailure : Exception
        {
            public RrFailure(string message) : base(message) { }
        }

        private sealed class RrTolerances
        {
            public double Boundary, Vertical, Position, RotationRad;
        }

        private static readonly HashSet<int> RrExcludedBip = new HashSet<int>(new[]
        {
            BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM, BuiltInParameter.ELEM_FAMILY_PARAM, BuiltInParameter.ELEM_TYPE_PARAM,
            BuiltInParameter.SYMBOL_ID_PARAM, BuiltInParameter.ELEM_CATEGORY_PARAM, BuiltInParameter.ELEM_CATEGORY_PARAM_MT,
            BuiltInParameter.LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM,
            BuiltInParameter.HOST_ID_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_PARAM,
            BuiltInParameter.PHASE_CREATED, BuiltInParameter.PHASE_DEMOLISHED, BuiltInParameter.DESIGN_OPTION_ID,
            BuiltInParameter.ALL_MODEL_MARK, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM,
            BuiltInParameter.IFC_GUID, BuiltInParameter.IFC_TYPE_GUID
        }.Select(b => (int)b));

        private object RedrawRoomCeilingsWithLighting(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) throw new Exception("No active document.");
            if (doc.IsFamilyDocument) throw new Exception("redraw_room_ceilings_with_lighting works on project documents only.");

            bool dryRun = parameters["dryRun"]?.Value<bool>() ?? true;
            bool verifyInDryRun = parameters["verifyInDryRun"]?.Value<bool>() ?? true;
            bool atomic = parameters["atomic"]?.Value<bool>() ?? true;
            bool allowDesignOptionRooms = parameters["allowDesignOptionRooms"]?.Value<bool>() ?? false;
            bool ignoreNonCeilingHosted = parameters["ignoreNonCeilingHostedFixtures"]?.Value<bool>() ?? false;
            string existingPolicy = (parameters["existingPolicy"]?.Value<string>() ?? "fail").Trim().ToLowerInvariant();
            if (existingPolicy != "fail" && existingPolicy != "skip")
                throw new Exception("existingPolicy must be 'fail' or 'skip'. This tool never deletes; clear targets with a separate, reviewed step.");
            string phaseName = parameters["phaseName"]?.Value<string>();

            double boundaryTolMm = CcPositive(parameters, "boundaryToleranceMm", 150.0);
            double verticalTolMm = CcPositive(parameters, "verticalToleranceMm", 50.0);
            double shapeTolMm = CcPositive(parameters, "shapeToleranceMm", 50.0);
            double positionTolMm = CcPositive(parameters, "positionToleranceMm", 2.0);
            double rotationTolDeg = parameters["rotationToleranceDeg"]?.Value<double>() ?? 0.1;
            if (double.IsNaN(rotationTolDeg) || rotationTolDeg <= 0 || rotationTolDeg > 5)
                throw new Exception("rotationToleranceDeg must be > 0 and <= 5.");
            var tol = new RrTolerances
            {
                Boundary = boundaryTolMm / CcMmPerFt,
                Vertical = verticalTolMm / CcMmPerFt,
                Position = positionTolMm / CcMmPerFt,
                RotationRad = rotationTolDeg * Math.PI / 180.0
            };

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

            // ---------- mappings ----------
            JArray mappingArray = parameters["mappings"] as JArray;
            if (mappingArray == null || mappingArray.Count == 0)
                throw new Exception("mappings is required: [{ sourceRoomNumber, targetRoomNumbers: [...] }].");
            if (mappingArray.Count > 50) throw new Exception("At most 50 mappings per call.");

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
                    errors.Add($"Room {t} is both a source and a target; chained redraws are refused.");
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

            // ---------- room resolution ----------
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

            // ---------- active design option must be Main Model to create anything ----------
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
            bool mainModelActive = activeOptionKnown && activeOption == ElementId.InvalidElementId;
            if (!dryRun && !mainModelActive)
                throw new Exception(activeOptionKnown
                    ? "A design option is active. Switch the active option to Main Model before redrawing, so new elements are created in the main model."
                    : "The active design option cannot be read in this Revit version; redraw execution is refused because new elements could land in a design option.");

            // ---------- shapes ----------
            var shapes = new Dictionary<IdType, CcRoomShape>();
            foreach (Room r in resolved.Values)
            {
                IdType key = r.Id.GetIdValue();
                if (!shapes.ContainsKey(key)) shapes[key] = CcBuildShape(r);
            }

            // ---------- planning ----------
            var mappingResults = new JArray();
            var plannedPairs = new List<Tuple<JObject, CcRoomShape, CcRoomShape, List<RrCeilingSpec>, XYZ>>();
            int preFailures = 0, skippedExisting = 0;

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
                var problems = new List<string>();
                List<RrCeilingSpec> ceilings = RrExtractSource(doc, src, hostedCatIds, tol, ignoreNonCeilingHosted, problems, mapWarnings, mapObj);
                string mappingError = problems.Count > 0
                    ? "Source refused (nothing can be redrawn from it): " + string.Join(" | ", problems.Take(30))
                    : null;
                if (mappingError == null && ceilings.Count == 0)
                    mappingError = "No redrawable ceiling lies inside the source room (check the room's vertical extent / Upper Limit, or boundaryToleranceMm).";

                mapObj["ceilings"] = new JArray(ceilings.Select(c => RrCeilingPlanJson(doc, c)));
                if (mappingError != null) mapObj["error"] = mappingError;

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
                        ["levelElevationDeltaMm"] = Math.Round(delta.Z * CcMmPerFt, 2)
                    };
                    double dev = CcCongruence(src, tgt, delta);
                    tObj["boundaryMaxDeviationMm"] = Math.Round(dev * CcMmPerFt, 2);

                    if (tError == null && tgt.Room.Id == src.Room.Id)
                        tError = "Target resolves to the source room itself.";
                    if (tError == null && tgt.Room.DesignOption != null)
                        tError = $"Target room belongs to design option '{tgt.Room.DesignOption.Name}'; redraw creates main-model elements only.";
                    if (tError == null && dev * CcMmPerFt > shapeTolMm)
                        tError = $"Room boundaries are not congruent under an XY translation (max deviation {Math.Round(dev * CcMmPerFt, 1)} mm > shapeToleranceMm {shapeTolMm}); refused to avoid misplacing ceilings.";
                    if (tError == null)
                    {
                        ElementId sp = CcRoomPhaseId(src.Room), tp = CcRoomPhaseId(tgt.Room);
                        if (sp == ElementId.InvalidElementId || tp == ElementId.InvalidElementId || sp != tp)
                            tError = "Source and target rooms are not in the same (known) phase.";
                    }
                    if (tError == null)
                        foreach (var sc in ceilings)
                        {
                            if (sc.Box == null) { tError = $"Source ceiling {sc.Source.Id.GetIdValue()} has no bounding box."; break; }
                            double z = sc.Box.Min.Z + delta.Z;
                            if (!CcInBand(z, tgt, tol.Vertical))
                            {
                                tError = $"Ceiling {sc.Source.Id.GetIdValue()} would land at {Math.Round(z * CcMmPerFt)} mm, outside the target room's vertical extent ({Math.Round(tgt.ZMin * CcMmPerFt)}..{Math.Round(tgt.ZMax * CcMmPerFt)} mm).";
                                break;
                            }
                        }

                    var ignored = new JArray();
                    JArray existing = RrExistingInTarget(doc, tgt, hostedCatIds, tol, ignored);
                    tObj["existingInTarget"] = existing;
                    if (ignored.Count > 0) tObj["ignoredInTarget"] = ignored;

                    string status;
                    if (tError != null)
                    {
                        status = "failed";
                        tObj["error"] = tError;
                        preFailures++;
                    }
                    else if (existing.Count > 0)
                    {
                        string msg = $"Target room already contains {existing.Count} current-phase ceiling/fixture element(s) on its level (see existingInTarget). Nothing is redrawn into it; clear it with a separate reviewed step first.";
                        if (existingPolicy == "fail") { status = "failed"; tObj["error"] = msg; preFailures++; }
                        else { status = "skipped_existing"; tWarnings.Add(msg); skippedExisting++; }
                    }
                    else
                    {
                        status = "planned";
                        tObj["plannedCeilings"] = ceilings.Count;
                        tObj["plannedFixtures"] = ceilings.Sum(x => x.Fixtures.Count);
                        plannedPairs.Add(Tuple.Create(tObj, src, tgt, ceilings, delta));
                    }
                    tObj["status"] = status;
                    tObj["warnings"] = tWarnings;
                    targetArr.Add(tObj);
                }
                mapObj["targets"] = targetArr;
                mapObj["warnings"] = mapWarnings;
                mappingResults.Add(mapObj);
            }

            var result = new JObject
            {
                ["method"] = "redraw (Ceiling.Create + NewFamilyInstance from extracted source data; no element copying)",
                ["dryRun"] = dryRun,
                ["atomic"] = atomic,
                ["existingPolicy"] = existingPolicy,
                ["hostedCategories"] = new JArray(hostedCategories.Select(c => c.ToString())),
                ["tolerances"] = new JObject
                {
                    ["boundaryMm"] = boundaryTolMm, ["verticalMm"] = verticalTolMm, ["shapeMm"] = shapeTolMm,
                    ["positionMm"] = positionTolMm, ["rotationDeg"] = rotationTolDeg
                },
                ["phaseFilter"] = phaseFilter?.Name,
                ["activeDesignOption"] = !activeOptionKnown ? "unknown (API unavailable)"
                    : mainModelActive ? "Main Model" : (doc.GetElement(activeOption)?.Name ?? activeOption.GetIdValue().ToString()),
                ["mappings"] = mappingResults
            };

            // ---------- execution / trial ----------
            bool run = plannedPairs.Count > 0 && (!dryRun || (verifyInDryRun && mainModelActive));
            if (dryRun && verifyInDryRun && !mainModelActive && plannedPairs.Count > 0)
                result["note"] = "Main Model is not (verifiably) the active design option, so the dry-run trial was skipped; switch to Main Model.";
            if (!dryRun && atomic && preFailures > 0)
            {
                run = false;
                result["committed"] = false;
                result["message"] = $"{preFailures} target(s) failed pre-checks; atomic=true, so nothing was created. Fix them or set atomic=false.";
            }

            int donePairs = 0, failedPairs = preFailures, doneCeilings = 0, doneFixtures = 0;
            var pendingIds = new List<KeyValuePair<JObject, ElementId>>();
            if (run)
            {
                using (var group = new TransactionGroup(doc, "Redraw room ceilings with lighting"))
                {
                    group.Start();
                    foreach (var pair in plannedPairs)
                    {
                        JObject tObj = pair.Item1;
                        var elementResults = new JArray();
                        tObj["elements"] = elementResults;
                        var pairIds = new List<KeyValuePair<JObject, ElementId>>();
                        string pairError = RrRunPair(doc, pair.Item2, pair.Item3, pair.Item4, pair.Item5, tol, dryRun, elementResults, tObj, pairIds);
                        if (pairError == null)
                        {
                            tObj["status"] = dryRun ? "verified_trial" : "redrawn_pending_commit";
                            donePairs++;
                            doneCeilings += pair.Item4.Count;
                            doneFixtures += pair.Item4.Sum(x => x.Fixtures.Count);
                            pendingIds.AddRange(pairIds);
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
                        result["message"] = "Dry run: every planned ceiling and fixture was created and verified inside a transaction group that was rolled back. Trial element ids are not reported.";
                    }
                    else if (atomic && failedPairs > 0)
                    {
                        group.RollBack();
                        result["committed"] = false;
                        result["message"] = $"{failedPairs} target(s) failed; atomic=true, so the whole run was rolled back. Nothing was created.";
                        foreach (var pair in plannedPairs)
                            if ((string)pair.Item1["status"] == "redrawn_pending_commit") pair.Item1["status"] = "rolled_back";
                        donePairs = 0; doneCeilings = 0; doneFixtures = 0;
                        pendingIds.Clear();
                    }
                    else
                    {
                        TransactionStatus gs = group.Assimilate();
                        bool ok = gs == TransactionStatus.Committed;
                        result["committed"] = ok && donePairs > 0;
                        foreach (var pair in plannedPairs)
                            if ((string)pair.Item1["status"] == "redrawn_pending_commit") pair.Item1["status"] = ok ? "redrawn" : "rolled_back";
                        if (ok)
                            foreach (var kv in pendingIds)
                                kv.Key["newId"] = doc.GetElement(kv.Value) != null ? (JToken)kv.Value.GetIdValue() : null;
                        else
                        {
                            result["message"] = $"Revit did not commit the transaction group ({gs}); nothing was created.";
                            donePairs = 0; doneCeilings = 0; doneFixtures = 0;
                        }
                    }
                }
            }
            else if (result["committed"] == null)
            {
                result["committed"] = false;
                if (result["message"] == null)
                    result["message"] = dryRun
                        ? (plannedPairs.Count == 0 ? "Dry run: no target is eligible." : "Dry run (plan only; verifyInDryRun=false or trial skipped).")
                        : "No target is eligible; nothing was created.";
            }

            result["summary"] = new JObject
            {
                ["targetsTotal"] = mappingSpecs.Sum(m => m.Value.Count),
                ["targetsPlanned"] = plannedPairs.Count,
                ["targetsRedrawnOrVerified"] = donePairs,
                ["targetsSkippedExisting"] = skippedExisting,
                ["targetsFailed"] = failedPairs,
                ["ceilingsRedrawnOrVerified"] = doneCeilings,
                ["fixturesRedrawnOrVerified"] = doneFixtures,
                ["elementsDeleted"] = 0
            };
            return result;
        }

        // ================================================================== source extraction

        private List<RrCeilingSpec> RrExtractSource(Document doc, CcRoomShape src, HashSet<IdType> hostedCatIds, RrTolerances tol,
            bool ignoreNonCeilingHosted, List<string> problems, JArray warnings, JObject mapObj)
        {
            var specs = new List<RrCeilingSpec>();
            Room room = src.Room;
            ElementId phaseId = CcRoomPhaseId(room);
            if (phaseId == ElementId.InvalidElementId)
            {
                problems.Add("the source room's phase is unknown");
                return specs;
            }

            List<FamilyInstance> near = RrInstancesInPlan(doc, src, tol.Boundary);
            var hostedAll = new HashSet<IdType>();

            foreach (Ceiling c in RrCeilingsOnLevelInPlan(doc, src, tol.Boundary))
            {
                IdType cid = c.Id.GetIdValue();
                BoundingBoxXYZ cb = c.get_BoundingBox(null);
                bool inBand = cb != null && CcInBand(cb.Min.Z, src, tol.Vertical);
                List<List<Curve>> loops;
                string sketchError;
                int sketchCurveCount;
                bool sketchOk = RrReadSketch(doc, c, tol.Position, out loops, out sketchCurveCount, out sketchError);
                List<List<XYZ>> fpLoops; List<XYZ> fpPts;
                RrFootprintOf(c, sketchOk ? loops : null, out fpLoops, out fpPts);
                string rel = CcRelation(fpPts, fpLoops, src, tol.Boundary);
                if (rel == "unrelated") continue;
                if (!inBand)
                {
                    problems.Add($"ceiling {cid} ({CcTypeName(doc, c)}) is on the source room level and over the room in plan but outside the room's vertical extent; raise verticalToleranceMm or the room's Upper Limit so it is redrawn or explicitly reviewed (never silently omitted)");
                    continue;
                }
                if (rel == "partial")
                {
                    problems.Add($"ceiling {cid} ({CcTypeName(doc, c)}) straddles the source room boundary (ambiguous; split/trim it or raise boundaryToleranceMm if it only overlaps the wall)");
                    continue;
                }

                string why = RrSourceBlocker(doc, c, phaseId);
                if (why != null) { problems.Add($"ceiling {cid}: {why}"); continue; }
                if (!sketchOk) { problems.Add($"ceiling {cid}: {sketchError}"); continue; }

                double bottomZ, bottomArea;
                string flat = RrFlatnessProblem(c, tol.Position, out bottomZ, out bottomArea);
                if (flat != null) { problems.Add($"ceiling {cid}: {flat}"); continue; }

                Parameter offP = c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                if (offP == null || offP.StorageType != StorageType.Double)
                { problems.Add($"ceiling {cid}: no Height Offset From Level parameter"); continue; }
                if (c.LevelId != room.LevelId)
                { problems.Add($"ceiling {cid}: not on the source room level"); continue; }

                var spec = new RrCeilingSpec
                {
                    Source = c,
                    TypeId = c.GetTypeId(),
                    TypeName = CcTypeName(doc, c),
                    OffsetFt = offP.AsDouble(),
                    Loops = loops,
                    Box = c.get_BoundingBox(null),
                    BottomArea = bottomArea,
                    CreatedPhaseId = c.CreatedPhaseId,
                    PerimeterFt = loops.Sum(l => l.Sum(cv => cv.Length))
                };
                int outer, holes;
                RrLoopNesting(loops, out outer, out holes);
                spec.OuterLoops = outer;
                spec.HoleLoops = holes;
                RrCaptureParams(c, spec.Params, spec.ParamsNotCopied);

                // --- every dependent / hosted element must be something this tool recreates ---
                int sketchElementCurves = 0;
                var fixtures = new List<FamilyInstance>();
                var depProblems = new List<string>();
                RrInspectDependents(doc, c, hostedCatIds, near, fixtures, depProblems, ref sketchElementCurves);
                foreach (string p in depProblems) problems.Add($"ceiling {cid}: {p}");
                if (sketchElementCurves > sketchCurveCount)
                    problems.Add($"ceiling {cid}: its sketch holds {sketchElementCurves} curve elements but only {sketchCurveCount} boundary curves (slope arrow or other sketch element); not supported");

                foreach (FamilyInstance fi in fixtures)
                {
                    hostedAll.Add(fi.Id.GetIdValue());
                    string fwhy;
                    RrFixtureSpec fs = RrExtractFixture(doc, fi, c, src, phaseId, tol, out fwhy);
                    if (fs == null)
                    {
                        problems.Add($"fixture {fi.Id.GetIdValue()} hosted on ceiling {cid}: {fwhy}; the ceiling cannot be redrawn without it");
                        continue;
                    }
                    spec.Fixtures.Add(fs);
                }
                specs.Add(spec);
            }

            // --- requested-category fixtures in the room that are NOT hosted on a redrawable ceiling ---
            var notRedrawn = new JArray();
            foreach (FamilyInstance fi in near)
            {
                if (fi.SuperComponent != null || hostedAll.Contains(fi.Id.GetIdValue())) continue;
                if (fi.Category == null || !hostedCatIds.Contains(fi.Category.Id.GetIdValue())) continue;
                if (CcFixtureLevelId(fi) != room.LevelId) continue;
                XYZ p = CcInstancePoint(fi);
                if (p == null || CcClassifyPoint(p, src.Loops, tol.Boundary) < 0) continue;
                bool present, isNew;
                CcPhaseStatus(fi, phaseId, out present, out isNew);
                if (!present) continue;
                string hostDesc = fi.Host == null ? "not hosted"
                    : $"hosted on {fi.Host.Category?.Name ?? fi.Host.GetType().Name} {fi.Host.Id.GetIdValue()} (not a redrawable source ceiling)";
                notRedrawn.Add(CcElementRef(doc, fi, hostDesc));
            }
            mapObj["requestedFixturesNotOnRedrawnCeilings"] = notRedrawn;
            if (notRedrawn.Count > 0)
            {
                string msg = $"{notRedrawn.Count} requested-category fixture(s) in the source room are not hosted on a redrawable ceiling and would NOT be redrawn (see requestedFixturesNotOnRedrawnCeilings)";
                if (ignoreNonCeilingHosted) warnings.Add(msg + "; proceeding because ignoreNonCeilingHostedFixtures=true.");
                else problems.Add(msg + "; pass ignoreNonCeilingHostedFixtures=true to accept that explicitly");
            }
            return specs;
        }

        /// <summary>Why a source element must not be used (null if it may).</summary>
        private static string RrSourceBlocker(Document doc, Element e, ElementId roomPhaseId)
        {
            if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) return $"member of group {e.GroupId.GetIdValue()}";
            if (e.DesignOption != null) return $"belongs to design option '{e.DesignOption.Name}'";
            if (e.Pinned) return "pinned";
            if (doc.IsWorkshared)
            {
                try
                {
                    if (WorksharingUtils.GetCheckoutStatus(doc, e.Id) == CheckoutStatus.OwnedByOtherUser) return "owned by another user";
                }
                catch { return "worksharing checkout status could not be read"; }
            }
            if (e.DemolishedPhaseId != null && e.DemolishedPhaseId != ElementId.InvalidElementId) return "has a Phase Demolished value";
            ElementOnPhaseStatus st;
            try { st = e.GetPhaseStatus(roomPhaseId); }
            catch (Exception ex) { return "phase status unreadable (" + ex.Message + ")"; }
            if (st != ElementOnPhaseStatus.New && st != ElementOnPhaseStatus.Existing)
                return $"phase status {st} in the room's phase (only New/Existing are redrawn)";
            return null;
        }

        /// <summary>
        /// Walks every dependent of the ceiling (and of the fixtures hosted on it). Allowed: the ceiling's
        /// own sketch, sketch plane, sketch curves and category-less internals; requested-category family
        /// instances hosted on the ceiling; nested sub-components of those fixtures. Anything else is a problem.
        /// </summary>
        private static void RrInspectDependents(Document doc, Ceiling c, HashSet<IdType> hostedCatIds, List<FamilyInstance> near,
            List<FamilyInstance> fixtures, List<string> problems, ref int sketchCurves)
        {
            var visited = new HashSet<IdType> { c.Id.GetIdValue() };
            var queue = new Queue<Element>();
            queue.Enqueue(c);
            var fixtureIds = new HashSet<IdType>();
            var pendingSubs = new List<FamilyInstance>();
            const int maxInspected = 5000;
            int curves = 0;

            Action<Element, Element> classify = (d, parent) =>
            {
                IdType dv = d.Id.GetIdValue();
                string desc = $"{d.Category?.Name ?? d.GetType().Name} {dv}";
                FamilyInstance fi = d as FamilyInstance;
                if (fi != null)
                {
                    if (fi.SuperComponent != null) { pendingSubs.Add(fi); queue.Enqueue(fi); return; }
                    bool hostedHere = fi.Host != null && fi.Host.Id == c.Id;
                    bool requested = fi.Category != null && hostedCatIds.Contains(fi.Category.Id.GetIdValue());
                    if (hostedHere && requested)
                    {
                        if (fixtureIds.Add(dv)) fixtures.Add(fi);
                        queue.Enqueue(fi);
                        return;
                    }
                    problems.Add(hostedHere
                        ? $"{desc} ({CcTypeName(doc, fi)}) is hosted on the ceiling but its category is not in hostedCategories; it would not be redrawn"
                        : $"{desc} ({CcTypeName(doc, fi)}) depends on {parent.Id.GetIdValue()} and would not be redrawn");
                    return;
                }
                bool parentIsCeilingSide = parent.Id == c.Id || parent is Sketch || parent is SketchPlane || parent is CurveElement;
                if (parentIsCeilingSide && (d is Sketch || d is SketchPlane))
                { queue.Enqueue(d); return; }
                if (parentIsCeilingSide && d is CurveElement && CcIsSketchCategory(d))
                { curves++; queue.Enqueue(d); return; }
                if (d.Category == null && !(d is View) && !(d is ElementType))
                { queue.Enqueue(d); return; }
                problems.Add($"{desc} ({d.GetType().Name}) depends on {parent.Id.GetIdValue()} and would not be redrawn (openings, tags, dimensions and other dependents are not supported)");
            };

            while (queue.Count > 0 && problems.Count < 50)
            {
                Element parent = queue.Dequeue();
                IList<ElementId> deps;
                try { deps = parent.GetDependentElements(null); }
                catch (Exception ex) { problems.Add($"dependents of {parent.Id.GetIdValue()} could not be read ({ex.Message})"); continue; }
                foreach (ElementId did in deps)
                {
                    if (!visited.Add(did.GetIdValue())) continue;
                    if (visited.Count > maxInspected) { problems.Add($"more than {maxInspected} dependent elements"); break; }
                    Element d = doc.GetElement(did);
                    if (d != null) classify(d, parent);
                }
                if (queue.Count == 0)
                {
                    // spatially found instances hosted on this ceiling that the dependency walk missed
                    foreach (FamilyInstance fi in CcHostedInstances(doc, c, near))
                        if (visited.Add(fi.Id.GetIdValue())) classify(fi, c);
                }
            }

            foreach (FamilyInstance sub in pendingSubs)
            {
                FamilyInstance root = sub;
                while (root.SuperComponent is FamilyInstance sup) root = sup;
                if (!fixtureIds.Contains(root.Id.GetIdValue()))
                    problems.Add($"nested component {sub.Id.GetIdValue()} belongs to {root.Id.GetIdValue()}, which is not a redrawn fixture");
            }
            sketchCurves = curves;
        }

        private RrFixtureSpec RrExtractFixture(Document doc, FamilyInstance fi, Ceiling host, CcRoomShape src, ElementId phaseId,
            RrTolerances tol, out string why)
        {
            why = RrSourceBlocker(doc, fi, phaseId);
            if (why != null) return null;
            if (fi.SuperComponent != null) { why = "is a nested/shared sub-component"; return null; }
            LocationPoint lp = fi.Location as LocationPoint;
            if (lp == null) { why = "has no point location (curve-based / non-point placement is not supported)"; return null; }
            XYZ origin = lp.Point;
            if (CcClassifyPoint(origin, src.Loops, tol.Boundary) < 0) { why = "is hosted on a source ceiling but located outside the source room"; return null; }
            FamilySymbol sym = fi.Symbol;
            if (sym == null || sym.Family == null) { why = "has no family type"; return null; }
            string conn = RrConnectionProblem(fi);
            if (conn != null) { why = conn; return null; }

            FamilyPlacementType pt;
            try { pt = sym.Family.FamilyPlacementType; }
            catch { why = "family placement type unreadable"; return null; }

            var fs = new RrFixtureSpec
            {
                Source = fi,
                Symbol = sym,
                Origin = origin,
                Xf = fi.GetTransform(),
                Hand = fi.HandOrientation,
                Facing = fi.FacingOrientation,
                HandFlipped = fi.HandFlipped,
                FacingFlipped = fi.FacingFlipped,
                Mirrored = fi.Mirrored,
                SubComponentCount = fi.GetSubComponentIds().Count,
                CreatedPhaseId = fi.CreatedPhaseId,
                Placement = pt.ToString()
            };

            if (pt == FamilyPlacementType.WorkPlaneBased)
            {
                if (fi.HostFace == null) { why = "work-plane/face-based instance without a host face reference"; return null; }
                PlanarFace face = null;
                try { face = host.GetGeometryObjectFromReference(fi.HostFace) as PlanarFace; } catch { }
                if (face == null) { why = "host face is not a planar face of the ceiling"; return null; }
                if (Math.Abs(Math.Abs(face.FaceNormal.Z) - 1.0) > 1e-6) { why = "host face is not horizontal"; return null; }
                fs.FaceHosted = true;
                fs.FaceNormal = face.FaceNormal.Normalize();
                fs.FaceZ = face.Origin.Z;
                int matches;
                Reference dummy;
                RrFindFace(host, fs.FaceNormal, fs.FaceZ, origin, tol.Position, out dummy, out matches);
                if (matches != 1) { why = $"host face is ambiguous on the source ceiling ({matches} candidate faces)"; return null; }
            }
            else if (pt == FamilyPlacementType.OneLevelBasedHosted)
            {
                fs.FaceHosted = false;
                fs.FaceNormal = XYZ.BasisZ;
                fs.FaceZ = origin.Z;
            }
            else
            {
                why = $"placement type {pt} is not supported (only face/work-plane-based on a ceiling face, or ceiling-based OneLevelBasedHosted)";
                return null;
            }

            ElementId own = RrOwnLevelId(fi);
            if (own == ElementId.InvalidElementId) fs.OwnLevelIsRoomLevel = false;
            else if (own == src.Room.LevelId) fs.OwnLevelIsRoomLevel = true;
            else { why = "its own (schedule) level differs from the source room level; that relation cannot be reproduced"; return null; }

            RrCaptureParams(fi, fs.Params, fs.ParamsNotCopied);
            return fs;
        }

        private static ElementId RrOwnLevelId(FamilyInstance fi)
        {
            if (fi.LevelId != null && fi.LevelId != ElementId.InvalidElementId) return fi.LevelId;
            Parameter sl = fi.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
            if (sl != null && sl.StorageType == StorageType.ElementId)
            {
                ElementId id = sl.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) return id;
            }
            return ElementId.InvalidElementId;
        }

        /// <summary>A fixture connected to a circuit/system or another element cannot be recreated faithfully.</summary>
        private static string RrConnectionProblem(FamilyInstance fi)
        {
            try
            {
                ConnectorManager cm = fi.MEPModel?.ConnectorManager;
                if (cm == null) return null;
                foreach (Connector cn in cm.Connectors)
                {
                    if (cn.IsConnected) return "has a connected MEP connector (circuit/system connections are not recreated)";
                    try
                    {
                        foreach (Connector r in cn.AllRefs)
                            if (r?.Owner != null && r.Owner.Id != fi.Id)
                                return $"connector references {r.Owner.Category?.Name ?? r.Owner.GetType().Name} {r.Owner.Id.GetIdValue()} (circuit/system connections are not recreated)";
                    }
                    catch { }
                }
            }
            catch (Exception ex) { return "MEP connectors unreadable (" + ex.Message + ")"; }
            return null;
        }

        // ================================================================== sketch / geometry

        /// <summary>
        /// Reads the ceiling's native sketch: exactly one Sketch on a horizontal plane, every curve a bound
        /// Line or Arc in that plane; every loop chained into a closed, contiguous, oriented curve list.
        /// </summary>
        private static bool RrReadSketch(Document doc, Ceiling c, double tol, out List<List<Curve>> loops, out int curveCount, out string error)
        {
            loops = new List<List<Curve>>();
            curveCount = 0;
            error = null;
            IList<ElementId> sketchIds;
            try { sketchIds = c.GetDependentElements(new ElementClassFilter(typeof(Sketch))); }
            catch (Exception ex) { error = "sketch unreadable (" + ex.Message + ")"; return false; }
            var sketches = sketchIds.Select(id => doc.GetElement(id)).OfType<Sketch>().ToList();
            if (sketches.Count != 1) { error = $"expected exactly one boundary sketch, found {sketches.Count}"; return false; }
            Sketch sk = sketches[0];
            Plane plane;
            try { plane = sk.SketchPlane?.GetPlane(); } catch { plane = null; }
            if (plane == null || Math.Abs(Math.Abs(plane.Normal.Z) - 1.0) > 1e-6) { error = "sketch plane is missing or not horizontal"; return false; }
            double z0 = plane.Origin.Z;
            CurveArrArray profile = sk.Profile;
            if (profile == null || profile.Size == 0) { error = "sketch has no profile"; return false; }

            foreach (CurveArray arr in profile)
            {
                var raw = new List<Curve>();
                foreach (Curve cv in arr)
                {
                    curveCount++;
                    if (cv == null || !cv.IsBound) { error = "sketch contains an unbound curve"; return false; }
                    if (!(cv is Line) && !(cv is Arc)) { error = $"sketch contains an unsupported {cv.GetType().Name} curve (only lines and arcs are supported)"; return false; }
                    if (cv is Arc a && Math.Abs(Math.Abs(a.Normal.Z) - 1.0) > 1e-6) { error = "sketch contains a non-horizontal arc"; return false; }
                    if (Math.Abs(cv.GetEndPoint(0).Z - z0) > tol || Math.Abs(cv.GetEndPoint(1).Z - z0) > tol)
                    { error = "sketch curve is not in the sketch plane"; return false; }
                    raw.Add(cv);
                }
                if (raw.Count == 0) continue;
                List<Curve> chain = RrChain(raw, tol);
                if (chain == null) { error = "a sketch loop is not a closed contiguous chain"; return false; }
                try
                {
                    CurveLoop test = CurveLoop.Create(chain);
                    if (test.IsOpen()) { error = "a sketch loop is open"; return false; }
                }
                catch (Exception ex) { error = "a sketch loop is not a valid curve loop (" + ex.Message + ")"; return false; }
                loops.Add(chain);
            }
            if (loops.Count == 0) { error = "sketch has no closed loop"; return false; }
            return true;
        }

        private static List<Curve> RrChain(List<Curve> raw, double tol)
        {
            var remaining = new List<Curve>(raw);
            var chain = new List<Curve> { remaining[0] };
            remaining.RemoveAt(0);
            while (remaining.Count > 0)
            {
                XYZ end = chain[chain.Count - 1].GetEndPoint(1);
                int idx = -1; bool reverse = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (remaining[i].GetEndPoint(0).DistanceTo(end) <= tol) { idx = i; reverse = false; break; }
                    if (remaining[i].GetEndPoint(1).DistanceTo(end) <= tol) { idx = i; reverse = true; break; }
                }
                if (idx < 0) return null;
                Curve next = remaining[idx];
                remaining.RemoveAt(idx);
                chain.Add(reverse ? next.CreateReversed() : next);
            }
            if (chain[chain.Count - 1].GetEndPoint(1).DistanceTo(chain[0].GetEndPoint(0)) > tol) return null;
            return chain;
        }

        /// <summary>Counts loops at even nesting depth (outer boundaries) and odd depth (holes).</summary>
        private static void RrLoopNesting(List<List<Curve>> loops, out int outer, out int holes)
        {
            outer = 0; holes = 0;
            var polys = loops.Select(RrTessellate).ToList();
            for (int i = 0; i < loops.Count; i++)
            {
                XYZ probe = loops[i][0].Evaluate(0.5, true);
                int depth = 0;
                for (int j = 0; j < loops.Count; j++)
                    if (j != i && CcInside(probe, new List<List<XYZ>> { polys[j] })) depth++;
                if (depth % 2 == 0) outer++; else holes++;
            }
        }

        private static List<XYZ> RrTessellate(List<Curve> loop)
        {
            var pts = new List<XYZ>();
            foreach (Curve cv in loop)
            {
                IList<XYZ> t = cv.Tessellate();
                for (int i = 0; i < t.Count - 1; i++) pts.Add(t[i]);
            }
            return pts;
        }

        /// <summary>Footprint from sketch loops when available; otherwise the solid footprint; otherwise the bounding box.</summary>
        private static void RrFootprintOf(Ceiling c, List<List<Curve>> sketchLoops, out List<List<XYZ>> loops, out List<XYZ> pts)
        {
            if (sketchLoops != null && sketchLoops.Count > 0)
            {
                loops = sketchLoops.Select(RrTessellate).Where(l => l.Count >= 3).ToList();
                pts = loops.SelectMany(l => l).ToList();
                if (pts.Count > 0) return;
            }
            CcFootprint(c, out loops, out pts);
            if (pts.Count > 0) return;
            BoundingBoxXYZ b = c.get_BoundingBox(null);
            loops = new List<List<XYZ>>();
            pts = new List<XYZ>();
            if (b == null) return;
            var rect = new List<XYZ>
            {
                new XYZ(b.Min.X, b.Min.Y, b.Min.Z), new XYZ(b.Max.X, b.Min.Y, b.Min.Z),
                new XYZ(b.Max.X, b.Max.Y, b.Min.Z), new XYZ(b.Min.X, b.Max.Y, b.Min.Z)
            };
            loops.Add(rect);
            pts.AddRange(rect);
        }

        /// <summary>
        /// Null when every face is horizontal planar (at most two distinct heights: bottom and top), vertical planar,
        /// or a vertical-axis cylinder (arc edges). Anything else = sloped / shape-edited / unsupported.
        /// </summary>
        private static string RrFlatnessProblem(Element e, double tol, out double bottomZ, out double bottomArea)
        {
            bottomZ = double.NaN; bottomArea = 0;
            GeometryElement ge = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (ge == null) return "no geometry to verify";
            var horiz = new List<PlanarFace>();
            int faces = 0;
            foreach (GeometryObject go in ge)
            {
                Solid s = go as Solid;
                if (s == null) { if (go is GeometryInstance) return "geometry contains instances (unsupported)"; continue; }
                foreach (Face f in s.Faces)
                {
                    faces++;
                    if (f is PlanarFace pf)
                    {
                        double nz = Math.Abs(pf.FaceNormal.Z);
                        if (nz > 1 - 1e-6) horiz.Add(pf);
                        else if (nz > 1e-6) return "has a sloped face (sloped ceilings are not supported)";
                    }
                    else if (f is CylindricalFace cf)
                    {
                        if (Math.Abs(Math.Abs(cf.Axis.Z) - 1.0) > 1e-6) return "has a non-vertical curved face";
                    }
                    else return $"has an unsupported {f.GetType().Name} (shape-edited or free-form geometry)";
                }
            }
            if (faces == 0 || horiz.Count == 0) return "no horizontal faces found";
            var levels = new List<double>();
            foreach (PlanarFace pf in horiz)
                if (!levels.Any(z => Math.Abs(z - pf.Origin.Z) <= tol)) levels.Add(pf.Origin.Z);
            if (levels.Count > 2) return $"horizontal faces at {levels.Count} different heights (shape-edited or stepped geometry)";
            double zMin = levels.Min();
            bottomZ = zMin;
            bottomArea = horiz.Where(pf => Math.Abs(pf.Origin.Z - zMin) <= tol).Sum(pf => pf.Area);
            return null;
        }

        /// <summary>Planar faces of the ceiling with the given normal, at height z, containing the point; returns the reference when unique.</summary>
        private static PlanarFace RrFindFace(Ceiling c, XYZ normal, double z, XYZ point, double tol, out Reference reference, out int matches)
        {
            reference = null; matches = 0;
            PlanarFace found = null;
            GeometryElement ge = c.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
            if (ge == null) return null;
            XYZ probe = new XYZ(point.X, point.Y, z);
            foreach (GeometryObject go in ge)
            {
                Solid s = go as Solid;
                if (s == null) continue;
                foreach (Face f in s.Faces)
                {
                    PlanarFace pf = f as PlanarFace;
                    if (pf == null || pf.FaceNormal.DotProduct(normal) < 1 - 1e-6 || Math.Abs(pf.Origin.Z - z) > tol) continue;
                    IntersectionResult ir = pf.Project(probe);
                    if (ir == null || ir.Distance > tol) continue;
                    matches++;
                    found = pf;
                }
            }
            if (matches == 1) reference = found.Reference;
            return matches == 1 ? found : null;
        }

        // ================================================================== parameters

        private static string RrParamKey(Parameter p) => $"{p.Id.GetIdValue()}|{p.Definition?.Name}";

        private static void RrCaptureParams(Element e, List<RrParamValue> values, List<string> notCopied)
        {
            var byKey = new Dictionary<string, RrParamValue>();
            var dup = new HashSet<string>();
            foreach (Parameter p in e.Parameters)
            {
                if (p?.Definition == null || p.StorageType == StorageType.None) continue;
                string name = p.Definition.Name ?? "";
                IdType pid = p.Id.GetIdValue();
                if (pid < 0 && RrExcludedBip.Contains((int)pid))
                {
                    if (pid == (IdType)(int)BuiltInParameter.ALL_MODEL_MARK && !string.IsNullOrEmpty(p.AsString()))
                        notCopied.Add($"{name} (unique identifier; left empty on the new element)");
                    continue;
                }
                if (name.IndexOf("ifcguid", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (p.IsReadOnly) continue;
                string key = RrParamKey(p);
                if (byKey.ContainsKey(key)) { dup.Add(key); continue; }
                var v = new RrParamValue { Key = key, Name = name, Storage = p.StorageType };
                switch (p.StorageType)
                {
                    case StorageType.Double: v.D = p.AsDouble(); break;
                    case StorageType.Integer: v.I = p.AsInteger(); break;
                    case StorageType.String: v.S = p.AsString(); break;
                    case StorageType.ElementId: v.E = p.AsElementId() ?? ElementId.InvalidElementId; break;
                }
                byKey[key] = v;
            }
            foreach (string k in dup)
            {
                notCopied.Add($"{byKey[k].Name} (ambiguous: several parameters share the same identity)");
                byKey.Remove(k);
            }
            values.AddRange(byKey.Values);
        }

        private static bool RrParamEquals(Parameter p, RrParamValue v)
        {
            if (p.StorageType != v.Storage) return false;
            switch (v.Storage)
            {
                case StorageType.Double: return Math.Abs(p.AsDouble() - v.D) <= 1e-6 * Math.Max(1.0, Math.Abs(v.D));
                case StorageType.Integer: return p.AsInteger() == v.I;
                case StorageType.String: return (p.AsString() ?? "") == (v.S ?? "");
                case StorageType.ElementId: return (p.AsElementId() ?? ElementId.InvalidElementId) == v.E;
            }
            return false;
        }

        private static Dictionary<string, Parameter> RrParamMap(Element e)
        {
            var map = new Dictionary<string, Parameter>();
            var dup = new HashSet<string>();
            foreach (Parameter p in e.Parameters)
            {
                if (p?.Definition == null) continue;
                string k = RrParamKey(p);
                if (map.ContainsKey(k)) dup.Add(k); else map[k] = p;
            }
            foreach (string k in dup) map.Remove(k);
            return map;
        }

        /// <summary>Sets every captured value that differs on the new element. Returns the number of values written.</summary>
        private static int RrApplyParams(Element target, List<RrParamValue> values, List<string> problems)
        {
            var map = RrParamMap(target);
            int written = 0;
            foreach (RrParamValue v in values)
            {
                Parameter p;
                if (!map.TryGetValue(v.Key, out p)) { problems.Add($"parameter '{v.Name}' does not exist on the new element"); continue; }
                if (RrParamEquals(p, v)) continue;
                if (p.IsReadOnly) { problems.Add($"parameter '{v.Name}' is read-only on the new element and differs from the source"); continue; }
                bool ok;
                try
                {
                    switch (v.Storage)
                    {
                        case StorageType.Double: ok = p.Set(v.D); break;
                        case StorageType.Integer: ok = p.Set(v.I); break;
                        case StorageType.String: ok = p.Set(v.S ?? ""); break;
                        case StorageType.ElementId: ok = p.Set(v.E); break;
                        default: ok = false; break;
                    }
                }
                catch (Exception ex) { problems.Add($"parameter '{v.Name}' could not be set ({ex.Message})"); continue; }
                if (!ok) problems.Add($"Revit refused to set parameter '{v.Name}'");
                else written++;
            }
            return written;
        }

        private static List<string> RrVerifyParams(Element target, List<RrParamValue> values)
        {
            var map = RrParamMap(target);
            var bad = new List<string>();
            foreach (RrParamValue v in values)
            {
                Parameter p;
                if (!map.TryGetValue(v.Key, out p) || !RrParamEquals(p, v)) bad.Add(v.Name);
            }
            return bad;
        }

        private static string RrSetPhaseCreated(Element e, ElementId phaseId)
        {
            if (e.CreatedPhaseId == phaseId) return null;
            Parameter p = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
            try
            {
                if (p != null && !p.IsReadOnly && p.Set(phaseId)) return null;
                e.CreatedPhaseId = phaseId;
                return null;
            }
            catch (Exception ex) { return "Phase Created could not be set (" + ex.Message + ")"; }
        }

        // ================================================================== target state

        /// <summary>XY outline of the room (+tol) with an unbounded vertical range.</summary>
        private static Outline RrPlanOutline(CcRoomShape s, double tol)
            => new Outline(new XYZ(s.MinX - tol, s.MinY - tol, -100000), new XYZ(s.MaxX + tol, s.MaxY + tol, 100000));

        /// <summary>Ceilings on exactly the room's level whose bounding box overlaps the room in plan, at any height.</summary>
        private static List<Ceiling> RrCeilingsOnLevelInPlan(Document doc, CcRoomShape s, double tol)
        {
            ElementId levelId = s.Room.LevelId;
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Ceilings)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(RrPlanOutline(s, tol)))
                .OfType<Ceiling>()
                .Where(c => c.LevelId == levelId)
                .OrderBy(c => c.Id.GetIdValue())
                .ToList();
        }

        /// <summary>Family instances whose bounding box overlaps the room in plan, at any height (callers apply level gates).</summary>
        private static List<FamilyInstance> RrInstancesInPlan(Document doc, CcRoomShape s, double tol)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WherePasses(new BoundingBoxIntersectsFilter(RrPlanOutline(s, tol)))
                .Cast<FamilyInstance>()
                .OrderBy(f => f.Id.GetIdValue())
                .ToList();
        }

        private JArray RrExistingInTarget(Document doc, CcRoomShape tgt, HashSet<IdType> hostedCatIds, RrTolerances tol, JArray ignored)
        {
            var arr = new JArray();
            ElementId levelId = tgt.Room.LevelId;
            ElementId phaseId = CcRoomPhaseId(tgt.Room);
            // Level-exact and plan-based at ANY height: stricter than the source band on purpose (no duplicates).
            foreach (Ceiling c in RrCeilingsOnLevelInPlan(doc, tgt, tol.Boundary))
            {
                List<List<Curve>> sl; int n; string err;
                bool ok = RrReadSketch(doc, c, tol.Position, out sl, out n, out err);
                List<List<XYZ>> loops; List<XYZ> pts;
                RrFootprintOf(c, ok ? sl : null, out loops, out pts);
                string rel = CcRelation(pts, loops, tgt, tol.Boundary);
                if (rel == "unrelated") continue;
                bool present, isNew;
                string st = CcPhaseStatus(c, phaseId, out present, out isNew);
                if (!present) { ignored?.Add(CcElementRef(doc, c, $"ceiling with phase status {st} in the target room's phase; not counted")); continue; }
                JObject o = CcElementRef(doc, c, rel == "partial" ? "ceiling overlaps the target room" : "ceiling inside the target room");
                o["phaseStatus"] = st;
                arr.Add(o);
            }
            foreach (FamilyInstance fi in RrInstancesInPlan(doc, tgt, tol.Boundary))
            {
                if (fi.SuperComponent != null || fi.Category == null || !hostedCatIds.Contains(fi.Category.Id.GetIdValue())) continue;
                if (CcFixtureLevelId(fi) != levelId) continue;
                XYZ p = CcInstancePoint(fi);
                if (p == null || CcClassifyPoint(p, tgt.Loops, tol.Boundary) < 0) continue;
                bool present, isNew;
                string st = CcPhaseStatus(fi, phaseId, out present, out isNew);
                if (!present) { ignored?.Add(CcElementRef(doc, fi, $"fixture with phase status {st} in the target room's phase; not counted")); continue; }
                JObject o = CcElementRef(doc, fi, fi.Host == null ? "fixture in the target room (not hosted)" : $"fixture in the target room (host {fi.Host.Id.GetIdValue()})");
                o["phaseStatus"] = st;
                arr.Add(o);
            }
            return arr;
        }

        // ================================================================== execution

        private string RrRunPair(Document doc, CcRoomShape src, CcRoomShape tgt, List<RrCeilingSpec> ceilings, XYZ delta,
            RrTolerances tol, bool dryRun, JArray elementResults, JObject tObj, List<KeyValuePair<JObject, ElementId>> pairIds)
        {
            using (var pairGroup = new TransactionGroup(doc, $"Redraw ceilings {src.Room.Number} -> {tgt.Room.Number}"))
            {
                pairGroup.Start();
                var created = new List<KeyValuePair<RrCeilingSpec, Ceiling>>();
                var createdFixtures = new List<Tuple<RrFixtureSpec, ElementId, JObject>>();
                RrRollbackFailures failures = null;
                try
                {
                    using (var t = new Transaction(doc, $"Redraw ceilings {src.Room.Number} -> {tgt.Room.Number}"))
                    {
                        failures = RrUseRollbackOnError(t);
                        t.Start();
                        try
                        {
                            foreach (RrCeilingSpec sc in ceilings)
                            {
                                Ceiling nc;
                                JObject cRes = RrRedrawCeiling(doc, sc, tgt, delta, tol, out nc, createdFixtures);
                                elementResults.Add(cRes);
                                created.Add(new KeyValuePair<RrCeilingSpec, Ceiling>(sc, nc));
                                if (!dryRun) pairIds.Add(new KeyValuePair<JObject, ElementId>(cRes, nc.Id));
                            }
                            if (!dryRun)
                                foreach (var cf in createdFixtures) pairIds.Add(new KeyValuePair<JObject, ElementId>(cf.Item3, cf.Item2));
                            TransactionStatus st = t.Commit();
                            if (st != TransactionStatus.Committed)
                                throw new RrFailure($"Revit rolled the redraw back ({st}){RrFailureText(failures)}");
                        }
                        catch
                        {
                            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                            throw;
                        }
                    }
                    if (failures.Warnings.Count > 0) tObj["revitWarnings"] = new JArray(failures.Warnings);

                    // post-commit re-check: Revit's commit-time processing must not have removed or re-hosted anything
                    var post = new List<string>();
                    foreach (var kv in created)
                        if (doc.GetElement(kv.Value.Id) == null) post.Add($"new ceiling for source {kv.Key.Source.Id.GetIdValue()} vanished at commit");
                    foreach (var cf in createdFixtures)
                    {
                        FamilyInstance f = doc.GetElement(cf.Item2) as FamilyInstance;
                        Ceiling expectedHost = created.FirstOrDefault(c => c.Key.Fixtures.Contains(cf.Item1)).Value;
                        if (f == null) post.Add($"new fixture for source {cf.Item1.Source.Id.GetIdValue()} vanished at commit");
                        else if (expectedHost == null || f.Host == null || f.Host.Id != expectedHost.Id)
                            post.Add($"new fixture for source {cf.Item1.Source.Id.GetIdValue()} lost its host at commit");
                    }
                    if (post.Count > 0) throw new RrFailure("Post-commit check failed: " + string.Join("; ", post));

                    pairGroup.Assimilate();
                    return null;
                }
                catch (Exception ex)
                {
                    if (pairGroup.HasStarted() && !pairGroup.HasEnded()) pairGroup.RollBack();
                    if (failures != null && failures.Warnings.Count > 0) tObj["revitWarnings"] = new JArray(failures.Warnings);
                    pairIds.Clear();
                    return (ex is RrFailure ? "" : "Unexpected error: ") + ex.Message + " (target rolled back)";
                }
            }
        }

        private JObject RrRedrawCeiling(Document doc, RrCeilingSpec sc, CcRoomShape tgt, XYZ delta, RrTolerances tol, out Ceiling nc,
            List<Tuple<RrFixtureSpec, ElementId, JObject>> createdFixtures)
        {
            IdType sid = sc.Source.Id.GetIdValue();
            double zProfile = tgt.Level.Elevation;
            var loops = new List<CurveLoop>();
            foreach (List<Curve> loop in sc.Loops)
            {
                var moved = loop.Select(cv => cv.CreateTransformed(Transform.CreateTranslation(
                    new XYZ(delta.X, delta.Y, zProfile - cv.GetEndPoint(0).Z)))).ToList();
                loops.Add(CurveLoop.Create(moved));
            }

            nc = Ceiling.Create(doc, loops, sc.TypeId, tgt.Level.Id);
            if (nc == null) throw new RrFailure($"Ceiling.Create returned null for source ceiling {sid}.");

            var problems = new List<string>();
            Parameter off = nc.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
            if (off == null || off.IsReadOnly || !off.Set(sc.OffsetFt))
                throw new RrFailure($"Could not set Height Offset From Level on the new ceiling for source {sid}.");
            string ph = RrSetPhaseCreated(nc, sc.CreatedPhaseId);
            if (ph != null) problems.Add(ph);
            int written = RrApplyParams(nc, sc.Params, problems);
            doc.Regenerate();

            // ---------------- ceiling verification ----------------
            var checks = new JObject();
            if (nc.GetTypeId() != sc.TypeId) problems.Add("type differs from source");
            if (nc.LevelId != tgt.Level.Id) problems.Add("not on the target room level");
            if (nc.DesignOption != null) problems.Add("created inside a design option");
            if (nc.GroupId != null && nc.GroupId != ElementId.InvalidElementId) problems.Add("created inside a group");
            if (nc.CreatedPhaseId != sc.CreatedPhaseId) problems.Add("Phase Created differs from source");
            Parameter offAfter = nc.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
            double offDiff = offAfter == null ? double.MaxValue : Math.Abs(offAfter.AsDouble() - sc.OffsetFt);
            if (offDiff > tol.Position) problems.Add("Height Offset From Level differs from source");
            checks["heightAboveLevelMm"] = Math.Round(sc.OffsetFt * CcMmPerFt, 1);

            BoundingBoxXYZ nb = nc.get_BoundingBox(null);
            if (sc.Box == null || nb == null) problems.Add("bounding box unavailable");
            else
            {
                double d = Math.Max(nb.Min.DistanceTo(sc.Box.Min + delta), nb.Max.DistanceTo(sc.Box.Max + delta));
                checks["boundingBoxDeviationMm"] = Math.Round(d * CcMmPerFt, 2);
                if (d > tol.Position) problems.Add($"bounding box off by {Math.Round(d * CcMmPerFt, 1)} mm");
            }

            List<List<Curve>> newLoops; int newCurveCount; string skErr;
            if (!RrReadSketch(doc, nc, tol.Position, out newLoops, out newCurveCount, out skErr))
                problems.Add("new ceiling sketch unreadable: " + skErr);
            else
            {
                int no, nh;
                RrLoopNesting(newLoops, out no, out nh);
                checks["loops"] = new JObject { ["source"] = sc.Loops.Count, ["new"] = newLoops.Count };
                checks["holes"] = new JObject { ["source"] = sc.HoleLoops, ["new"] = nh };
                if (newLoops.Count != sc.Loops.Count) problems.Add($"loop count {newLoops.Count} != source {sc.Loops.Count}");
                if (no != sc.OuterLoops || nh != sc.HoleLoops) problems.Add($"outer/hole loops {no}/{nh} != source {sc.OuterLoops}/{sc.HoleLoops}");
                string geo = RrCompareProfiles(sc.Loops, newLoops, delta, tol.Position);
                if (geo != null) problems.Add(geo);
                double newPerim = newLoops.Sum(l => l.Sum(cv => cv.Length));
                checks["perimeterMm"] = new JObject { ["source"] = Math.Round(sc.PerimeterFt * CcMmPerFt, 1), ["new"] = Math.Round(newPerim * CcMmPerFt, 1) };
                if (Math.Abs(newPerim - sc.PerimeterFt) > tol.Position * Math.Max(1, sc.Loops.Sum(l => l.Count))) problems.Add("profile perimeter differs from source");
            }

            double nBottomZ, nArea;
            string flat = RrFlatnessProblem(nc, tol.Position, out nBottomZ, out nArea);
            if (flat != null) problems.Add("new ceiling geometry: " + flat);
            else
            {
                checks["bottomAreaM2"] = new JObject
                {
                    ["source"] = Math.Round(sc.BottomArea * 0.09290304, 4),
                    ["new"] = Math.Round(nArea * 0.09290304, 4)
                };
                if (Math.Abs(nArea - sc.BottomArea) > Math.Max(1e-4 * sc.BottomArea, 0.01)) problems.Add("bottom face area differs from source");
            }

            List<List<XYZ>> fl; List<XYZ> fp;
            RrFootprintOf(nc, newLoops, out fl, out fp);
            if (CcRelation(fp, fl, tgt, tol.Boundary) != "contained") problems.Add("not contained in the target room");
            List<string> badParams = RrVerifyParams(nc, sc.Params);
            if (badParams.Count > 0) problems.Add("parameters differ from source: " + string.Join(", ", badParams.Take(20)));

            var res = new JObject
            {
                ["sourceId"] = sid,
                ["newId"] = null,
                ["category"] = sc.Source.Category?.Name,
                ["type"] = sc.TypeName,
                ["created"] = "Ceiling.Create",
                ["level"] = tgt.Level.Name,
                ["parametersWritten"] = written,
                ["parametersNotCopied"] = new JArray(sc.ParamsNotCopied),
                ["checks"] = checks
            };
            if (problems.Count > 0)
            {
                res["verified"] = false;
                throw new RrFailure($"Verification failed for the ceiling redrawn from {sid}: {string.Join("; ", problems)}.");
            }

            var fixtureResults = new JArray();
            res["fixtures"] = fixtureResults;
            foreach (RrFixtureSpec fs in sc.Fixtures)
            {
                FamilyInstance nf;
                JObject fRes = RrRedrawFixture(doc, fs, nc, tgt, delta, tol, out nf);
                fixtureResults.Add(fRes);
                createdFixtures.Add(Tuple.Create(fs, nf.Id, fRes));
            }
            res["verified"] = true;
            return res;
        }

        private static string RrCompareProfiles(List<List<Curve>> source, List<List<Curve>> created, XYZ delta, double tol)
        {
            var expected = source.SelectMany(l => l).ToList();
            var pool = created.SelectMany(l => l).ToList();
            if (expected.Count != pool.Count) return $"profile has {pool.Count} curves, source {expected.Count}";
            var shift = new XYZ(delta.X, delta.Y, 0);
            foreach (Curve e in expected)
            {
                int hit = -1;
                for (int i = 0; i < pool.Count; i++)
                    if (RrCurveMatches(e, pool[i], shift, tol)) { hit = i; break; }
                if (hit < 0)
                {
                    XYZ a = e.GetEndPoint(0) + shift;
                    return $"no matching {e.GetType().Name} in the new profile near ({Math.Round(a.X * CcMmPerFt)}, {Math.Round(a.Y * CcMmPerFt)}) mm";
                }
                pool.RemoveAt(hit);
            }
            return null;
        }

        private static bool RrCurveMatches(Curve expected, Curve actual, XYZ shift, double tol)
        {
            if (expected.GetType() != actual.GetType()) return false;
            Func<XYZ, XYZ, bool> near = (p, q) => Math.Abs(p.X - q.X) <= tol && Math.Abs(p.Y - q.Y) <= tol;
            XYZ e0 = expected.GetEndPoint(0) + shift, e1 = expected.GetEndPoint(1) + shift;
            XYZ a0 = actual.GetEndPoint(0), a1 = actual.GetEndPoint(1);
            bool ends = (near(e0, a0) && near(e1, a1)) || (near(e0, a1) && near(e1, a0));
            if (!ends) return false;
            if (expected is Arc ea && actual is Arc aa)
            {
                if (Math.Abs(ea.Radius - aa.Radius) > tol) return false;
                if (!near(ea.Center + shift, aa.Center)) return false;
                if (!near(ea.Evaluate(0.5, true) + shift, aa.Evaluate(0.5, true))) return false;
            }
            return Math.Abs(expected.Length - actual.Length) <= tol;
        }

        private JObject RrRedrawFixture(Document doc, RrFixtureSpec fs, Ceiling nc, CcRoomShape tgt, XYZ delta, RrTolerances tol, out FamilyInstance nf)
        {
            IdType sid = fs.Source.Id.GetIdValue();
            XYZ expected = fs.Origin + delta;
            FamilySymbol sym = fs.Symbol;
            if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

            string method;
            XYZ axis;
            if (fs.FaceHosted)
            {
                Reference faceRef; int matches;
                PlanarFace face = RrFindFace(nc, fs.FaceNormal, fs.FaceZ + delta.Z, expected, tol.Position, out faceRef, out matches);
                if (face == null || faceRef == null)
                    throw new RrFailure($"Fixture {sid}: the matching face on the new ceiling is {(matches == 0 ? "missing" : "ambiguous (" + matches + " faces)")}; not placed.");
                axis = face.FaceNormal.Normalize();
                XYZ onFace = expected - axis.Multiply((expected - face.Origin).DotProduct(axis));
                XYZ dir = fs.Hand - axis.Multiply(fs.Hand.DotProduct(axis));
                if (dir.GetLength() < 1e-6) dir = fs.Facing - axis.Multiply(fs.Facing.DotProduct(axis));
                if (dir.GetLength() < 1e-6) throw new RrFailure($"Fixture {sid}: orientation lies along the face normal; cannot derive a reference direction.");
                nf = doc.Create.NewFamilyInstance(faceRef, onFace, dir.Normalize(), sym);
                method = "NewFamilyInstance(face reference on the new ceiling, point, referenceDirection, symbol)";
            }
            else
            {
                axis = XYZ.BasisZ;
                nf = doc.Create.NewFamilyInstance(expected, sym, nc, tgt.Level, StructuralType.NonStructural);
                method = "NewFamilyInstance(point, symbol, new ceiling host, target level, NonStructural)";
            }
            if (nf == null) throw new RrFailure($"Fixture {sid}: NewFamilyInstance returned null.");
            doc.Regenerate();

            var problems = new List<string>();
            string ph = RrSetPhaseCreated(nf, fs.CreatedPhaseId);
            if (ph != null) problems.Add(ph);
            int written = RrApplyParams(nf, fs.Params, problems);
            doc.Regenerate();

            // flips, then rotation about the host-face axis, then in-plane position correction
            var actions = new JArray();
            if (nf.HandFlipped != fs.HandFlipped)
            {
                if (nf.CanFlipHand && nf.flipHand()) actions.Add("flipHand");
                else problems.Add("hand flip state differs and the instance cannot be hand-flipped");
            }
            if (nf.FacingFlipped != fs.FacingFlipped)
            {
                if (nf.CanFlipFacing && nf.flipFacing()) actions.Add("flipFacing");
                else problems.Add("facing flip state differs and the instance cannot be facing-flipped");
            }
            doc.Regenerate();

            XYZ cur = (nf.Location as LocationPoint)?.Point;
            if (cur == null) throw new RrFailure($"Fixture {sid}: new instance has no point location.");
            double angle = RrSignedAngle(nf.HandOrientation, fs.Hand, axis);
            if (Math.Abs(angle) > tol.RotationRad * 0.5)
            {
                ElementTransformUtils.RotateElement(doc, nf.Id, Line.CreateBound(cur, cur + axis), angle);
                doc.Regenerate();
                actions.Add($"rotate {Math.Round(angle * 180 / Math.PI, 3)} deg about host normal");
            }
            cur = (nf.Location as LocationPoint)?.Point;
            XYZ diff = expected - cur;
            XYZ inPlane = diff - axis.Multiply(diff.DotProduct(axis));
            if (inPlane.GetLength() > 1e-9)
            {
                ElementTransformUtils.MoveElement(doc, nf.Id, inPlane);
                doc.Regenerate();
                actions.Add($"in-plane move {Math.Round(inPlane.GetLength() * CcMmPerFt, 3)} mm");
            }

            // own (schedule) level mirrors the source relation
            if (fs.OwnLevelIsRoomLevel && RrOwnLevelId(nf) != tgt.Level.Id)
            {
                Parameter sl = nf.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                if (sl != null && !sl.IsReadOnly && sl.StorageType == StorageType.ElementId)
                {
                    XYZ p0 = (nf.Location as LocationPoint)?.Point;
                    ElementId before = sl.AsElementId();
                    sl.Set(tgt.Level.Id);
                    doc.Regenerate();
                    XYZ p1 = (nf.Location as LocationPoint)?.Point;
                    if (p0 != null && p1 != null && p0.DistanceTo(p1) > tol.Position)
                    {
                        sl.Set(before);
                        doc.Regenerate();
                    }
                    else actions.Add("schedule level set to target level");
                }
            }

            // ---------------- fixture verification ----------------
            var checks = new JObject();
            if (nf.Symbol == null || nf.Symbol.Id != sym.Id) problems.Add("family type differs from source");
            if (nf.Host == null || nf.Host.Id != nc.Id) problems.Add($"host is {(nf.Host == null ? "none" : nf.Host.Id.GetIdValue().ToString())}, not the new ceiling");
            if (fs.FaceHosted)
            {
                PlanarFace hf = null;
                try { hf = nf.HostFace == null ? null : nc.GetGeometryObjectFromReference(nf.HostFace) as PlanarFace; } catch { }
                if (hf == null) problems.Add("host face is not a face of the new ceiling");
                else if (hf.FaceNormal.DotProduct(fs.FaceNormal) < 1 - 1e-6 || Math.Abs(hf.Origin.Z - (fs.FaceZ + delta.Z)) > tol.Position)
                    problems.Add("host face differs from the source relation (side or height)");
            }
            if (nf.DesignOption != null) problems.Add("created inside a design option");
            if (nf.GroupId != null && nf.GroupId != ElementId.InvalidElementId) problems.Add("created inside a group");
            if (nf.SuperComponent != null) problems.Add("created as a nested component");
            if (nf.CreatedPhaseId != fs.CreatedPhaseId) problems.Add("Phase Created differs from source");
            XYZ act = (nf.Location as LocationPoint)?.Point;
            double posErr = act == null ? double.MaxValue : act.DistanceTo(expected);
            checks["positionDeviationMm"] = act == null ? null : (JToken)Math.Round(posErr * CcMmPerFt, 3);
            if (posErr > tol.Position) problems.Add($"position off by {(act == null ? "?" : Math.Round(posErr * CcMmPerFt, 1).ToString())} mm");
            if (act != null && CcClassifyPoint(act, tgt.Loops, tol.Boundary) < 0) problems.Add("outside the target room");

            Transform nx = nf.GetTransform();
            double aX = RrAngle(nx.BasisX, fs.Xf.BasisX), aY = RrAngle(nx.BasisY, fs.Xf.BasisY), aZ = RrAngle(nx.BasisZ, fs.Xf.BasisZ);
            double aH = RrAngle(nf.HandOrientation, fs.Hand), aF = RrAngle(nf.FacingOrientation, fs.Facing);
            double worst = new[] { aX, aY, aZ, aH, aF }.Max();
            checks["maxOrientationDeviationDeg"] = Math.Round(worst * 180 / Math.PI, 4);
            if (worst > tol.RotationRad) problems.Add($"orientation differs by {Math.Round(worst * 180 / Math.PI, 3)} deg");
            if (nf.HandFlipped != fs.HandFlipped) problems.Add("hand flip state differs");
            if (nf.FacingFlipped != fs.FacingFlipped) problems.Add("facing flip state differs");
            if (nf.Mirrored != fs.Mirrored) problems.Add("mirrored state differs (mirrored instances cannot be recreated by placement)");
            int subs = nf.GetSubComponentIds().Count;
            if (subs != fs.SubComponentCount) problems.Add($"{subs} nested sub-components, source has {fs.SubComponentCount}");
            ElementId own = RrOwnLevelId(nf);
            checks["level"] = (doc.GetElement(own) as Level)?.Name;
            if (fs.OwnLevelIsRoomLevel && own != tgt.Level.Id) problems.Add("instance level is not the target room level");
            List<string> badParams = RrVerifyParams(nf, fs.Params);
            if (badParams.Count > 0) problems.Add("parameters differ from source: " + string.Join(", ", badParams.Take(20)));

            var res = new JObject
            {
                ["sourceId"] = sid,
                ["newId"] = null,
                ["category"] = fs.Source.Category?.Name,
                ["familyType"] = $"{sym.FamilyName} : {sym.Name}",
                ["placementType"] = fs.Placement,
                ["created"] = method,
                ["hostedOnNewCeiling"] = nf.Host != null && nf.Host.Id == nc.Id,
                ["adjustments"] = actions,
                ["parametersWritten"] = written,
                ["parametersNotCopied"] = new JArray(fs.ParamsNotCopied),
                ["checks"] = checks,
                ["verified"] = problems.Count == 0
            };
            if (problems.Count > 0)
                throw new RrFailure($"Verification failed for the fixture redrawn from {sid}: {string.Join("; ", problems)}.");
            return res;
        }

        private static double RrAngle(XYZ a, XYZ b)
        {
            if (a == null || b == null || a.GetLength() < 1e-9 || b.GetLength() < 1e-9) return Math.PI;
            double d = a.Normalize().DotProduct(b.Normalize());
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, d)));
        }

        /// <summary>Signed angle rotating <paramref name="from"/> onto <paramref name="to"/> about <paramref name="axis"/> (both projected onto the axis plane).</summary>
        private static double RrSignedAngle(XYZ from, XYZ to, XYZ axis)
        {
            XYZ a = from - axis.Multiply(from.DotProduct(axis));
            XYZ b = to - axis.Multiply(to.DotProduct(axis));
            if (a.GetLength() < 1e-9 || b.GetLength() < 1e-9) return 0;
            a = a.Normalize(); b = b.Normalize();
            return Math.Atan2(axis.DotProduct(a.CrossProduct(b)), a.DotProduct(b));
        }

        private static JObject RrCeilingPlanJson(Document doc, RrCeilingSpec c)
        {
            return new JObject
            {
                ["sourceId"] = c.Source.Id.GetIdValue(),
                ["type"] = c.TypeName,
                ["heightAboveLevelMm"] = Math.Round(c.OffsetFt * CcMmPerFt, 1),
                ["loops"] = c.Loops.Count,
                ["outerLoops"] = c.OuterLoops,
                ["holes"] = c.HoleLoops,
                ["curves"] = new JObject
                {
                    ["lines"] = c.Loops.Sum(l => l.Count(cv => cv is Line)),
                    ["arcs"] = c.Loops.Sum(l => l.Count(cv => cv is Arc))
                },
                ["perimeterMm"] = Math.Round(c.PerimeterFt * CcMmPerFt, 1),
                ["parametersToCopy"] = c.Params.Count,
                ["parametersNotCopied"] = new JArray(c.ParamsNotCopied),
                ["fixtures"] = new JArray(c.Fixtures.Select(f => new JObject
                {
                    ["sourceId"] = f.Source.Id.GetIdValue(),
                    ["category"] = f.Source.Category?.Name,
                    ["familyType"] = $"{f.Symbol.FamilyName} : {f.Symbol.Name}",
                    ["placementType"] = f.Placement,
                    ["hostFace"] = f.FaceHosted ? (f.FaceNormal.Z < 0 ? "bottom (normal down)" : "top (normal up)") : "ceiling host (OneLevelBasedHosted)",
                    ["handFlipped"] = f.HandFlipped,
                    ["facingFlipped"] = f.FacingFlipped,
                    ["parametersToCopy"] = f.Params.Count,
                    ["parametersNotCopied"] = new JArray(f.ParamsNotCopied)
                }))
            };
        }

        // ---------------------------------------------------------------- failure handling

        private sealed class RrRollbackFailures : IFailuresPreprocessor
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool hasError = false;
                foreach (FailureMessageAccessor msg in accessor.GetFailureMessages())
                {
                    string text;
                    try { text = msg.GetDescriptionText(); } catch { text = "(unreadable Revit failure message)"; }
                    FailureSeverity sev;
                    try { sev = msg.GetSeverity(); } catch { sev = FailureSeverity.Error; }
                    if (sev == FailureSeverity.None) continue;
                    if (sev == FailureSeverity.Warning)
                    {
                        if (!Warnings.Contains(text)) Warnings.Add(text);
                        try { accessor.DeleteWarning(msg); } catch { }
                    }
                    else
                    {
                        if (!Errors.Contains(text)) Errors.Add(text);
                        hasError = true;
                    }
                }
                return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        private static RrRollbackFailures RrUseRollbackOnError(Transaction t)
        {
            var failures = new RrRollbackFailures();
            FailureHandlingOptions fho = t.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(failures);
            fho.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(fho);
            return failures;
        }

        private static string RrFailureText(RrRollbackFailures failures)
            => failures != null && failures.Errors.Count > 0 ? ": " + string.Join("; ", failures.Errors) : string.Empty;
    }
}
