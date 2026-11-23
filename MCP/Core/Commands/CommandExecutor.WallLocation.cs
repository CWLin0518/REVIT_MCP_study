using System;
using System.Collections.Generic;
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
    public partial class CommandExecutor
    {
        #region Wall location lines

        private const double WallLocationMmPerFoot = 304.8;
        private const double WallLocationToleranceMm = 0.5;

        private sealed class WallLocationEdit
        {
            public IdType Id;
            public Wall Wall;
            public XYZ OldStart;
            public XYZ OldEnd;
            public XYZ NewStart;
            public XYZ NewEnd;
            public bool JoinAllowedAtStart;
            public bool JoinAllowedAtEnd;
            public Dictionary<ElementId, XYZ> InsertPoints = new Dictionary<ElementId, XYZ>();
        }

        private sealed class WallLocationFailurePreprocessor : IFailuresPreprocessor
        {
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public readonly HashSet<string> JoinsDetached = new HashSet<string>();
            private readonly HashSet<ElementId> _detachableWalls;

            /// <param name="detachableWalls">Walls whose "cannot keep joined" failures may be resolved by detaching; null disables it.</param>
            public WallLocationFailurePreprocessor(HashSet<ElementId> detachableWalls = null)
            {
                _detachableWalls = detachableWalls;
            }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool resolved = false;
                foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
                {
                    if (message.GetSeverity() == FailureSeverity.Warning)
                    {
                        Warnings.Add(message.GetDescriptionText());
                        accessor.DeleteWarning(message);
                        continue;
                    }

                    List<ElementId> elementIds = message.GetFailingElementIds()
                        .Concat(message.GetAdditionalElementIds())
                        .Distinct()
                        .ToList();
                    string idText = string.Join(", ", elementIds.Select(id => id.ToString()));

                    if (_detachableWalls != null &&
                        message.GetFailureDefinitionId().Guid == BuiltInFailures.JoinElementsFailures.CannotKeepJoined.Guid &&
                        elementIds.Any(_detachableWalls.Contains) &&
                        message.HasResolutionOfType(FailureResolutionType.DetachElements))
                    {
                        message.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                        accessor.ResolveFailure(message);
                        JoinsDetached.Add(idText);
                        resolved = true;
                        continue;
                    }

                    Errors.Add($"{message.GetDescriptionText()} [elements: {idText}]");
                }

                if (Errors.Count > 0) return FailureProcessingResult.ProceedWithRollBack;
                return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }

        /// <summary>
        /// Atomically rewrites the location lines of straight walls. Coordinates are in mm.
        /// Defaults to a rolled-back dry run; Z and wall direction are preserved.
        /// </summary>
        private object SetWallLocationLines(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            bool dryRun = parameters["dryRun"]?.Value<bool>() ?? true;
            double maxShiftMm = parameters["maxShiftMm"]?.Value<double>() ?? 150;
            JArray items = parameters["walls"] as JArray;
            if (items == null || items.Count == 0)
                throw new Exception("walls is required and must not be empty.");

            var edits = new List<WallLocationEdit>();
            var problems = new List<string>();
            var seen = new HashSet<IdType>();

            foreach (JToken item in items)
            {
                IdType id = item["wallId"]?.Value<IdType>() ?? 0;
                if (!seen.Add(id)) { problems.Add($"{id}: duplicated"); continue; }

                Wall wall = doc.GetElement(new ElementId(id)) as Wall;
                if (wall == null) { problems.Add($"{id}: not a wall"); continue; }
                if (wall.Pinned) { problems.Add($"{id}: pinned"); continue; }
                if (wall.GroupId != ElementId.InvalidElementId) { problems.Add($"{id}: in a group"); continue; }

                Line line = (wall.Location as LocationCurve)?.Curve as Line;
                if (line == null) { problems.Add($"{id}: location is not a straight line"); continue; }

                XYZ oldStart = line.GetEndPoint(0);
                XYZ oldEnd = line.GetEndPoint(1);
                XYZ newStart = new XYZ(item["startX"].Value<double>() / WallLocationMmPerFoot, item["startY"].Value<double>() / WallLocationMmPerFoot, oldStart.Z);
                XYZ newEnd = new XYZ(item["endX"].Value<double>() / WallLocationMmPerFoot, item["endY"].Value<double>() / WallLocationMmPerFoot, oldEnd.Z);

                double startShift = newStart.DistanceTo(oldStart) * WallLocationMmPerFoot;
                double endShift = newEnd.DistanceTo(oldEnd) * WallLocationMmPerFoot;
                if (startShift > maxShiftMm || endShift > maxShiftMm)
                {
                    problems.Add($"{id}: endpoint shift {Math.Max(startShift, endShift):F1} mm exceeds maxShiftMm {maxShiftMm:F1}");
                    continue;
                }
                if (newStart.DistanceTo(newEnd) * WallLocationMmPerFoot < 1 ||
                    (newEnd - newStart).Normalize().DotProduct(line.Direction) < 0.999999)
                {
                    problems.Add($"{id}: new line changes the wall direction");
                    continue;
                }

                var edit = new WallLocationEdit { Id = id, Wall = wall, OldStart = oldStart, OldEnd = oldEnd, NewStart = newStart, NewEnd = newEnd };
                foreach (ElementId insertId in wall.FindInserts(true, false, false, false))
                {
                    if (doc.GetElement(insertId)?.Location is LocationPoint point)
                        edit.InsertPoints[insertId] = point.Point;
                }
                edits.Add(edit);
            }

            if (problems.Count > 0)
                return new { Success = false, Committed = false, Message = "No wall was changed.", Problems = problems };

            bool resolveJoinFailures = parameters["resolveJoinFailures"]?.Value<bool>() ?? false;
            var preprocessor = new WallLocationFailurePreprocessor(
                resolveJoinFailures ? new HashSet<ElementId>(edits.Select(e => e.Wall.Id)) : null);
            JArray sketchEdges = parameters["rebuildSketchEdges"] as JArray;
            var sketchEdgesRebuilt = new List<string>();
            // The group lets a dry run really commit, so Revit's commit-time failures
            // (joins, dependent sketches) are detected, and then roll everything back.
            using (TransactionGroup group = new TransactionGroup(doc, "Set wall location lines"))
            using (Transaction trans = new Transaction(doc, "Set wall location lines"))
            {
                group.Start();

                if (sketchEdges != null)
                {
                    foreach (JToken edge in sketchEdges)
                    {
                        string rebuildProblem = RebuildCeilingSketchEdge(doc, edge, sketchEdgesRebuilt);
                        if (rebuildProblem != null)
                        {
                            group.RollBack();
                            return new { Success = false, Committed = false, DryRun = dryRun, Message = "Rolled back: a ceiling sketch edge could not be rebuilt.", Problems = new List<string> { rebuildProblem } };
                        }
                    }
                }

                FailureHandlingOptions options = trans.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(preprocessor);
                options.SetClearAfterRollback(true);
                trans.SetFailureHandlingOptions(options);
                trans.Start();

                // Optional: release top attachments first; the wall top must stay at the same height.
                var topsDetached = new List<string>();
                if (parameters["detachTopAttachments"] is JArray detachTops)
                {
                    foreach (JToken detach in detachTops)
                    {
                        IdType detachWallId = detach["wallId"]?.Value<IdType>() ?? 0;
                        Wall detachWall = edits.FirstOrDefault(e => e.Id == detachWallId)?.Wall;
                        string detachProblem = null;
                        if (detachWall == null)
                        {
                            detachProblem = $"{detachWallId}: not in the walls list";
                        }
                        else
                        {
                            double topBefore = detachWall.get_BoundingBox(null).Max.Z;
                            try
                            {
#if REVIT2025_OR_GREATER
                                foreach (JToken target in (detach["targetIds"] as JArray) ?? new JArray())
                                    detachWall.RemoveAttachment(new ElementId(target.Value<IdType>()));
#else
                                throw new NotSupportedException("Wall.RemoveAttachment is not available in this Revit version.");
#endif
                                doc.Regenerate();
                                double topShift = Math.Abs(detachWall.get_BoundingBox(null).Max.Z - topBefore) * WallLocationMmPerFoot;
                                if (topShift > WallLocationToleranceMm)
                                    detachProblem = $"{detachWallId}: top moved {topShift:F2} mm after detaching";
                                else
                                    topsDetached.Add(detachWallId.ToString());
                            }
                            catch (Exception ex)
                            {
                                detachProblem = $"{detachWallId}: cannot detach top ({ex.Message})";
                            }
                        }

                        if (detachProblem != null)
                        {
                            trans.RollBack();
                            group.RollBack();
                            return new { Success = false, Committed = false, DryRun = dryRun, Message = "Rolled back: a wall top attachment could not be released.", Problems = new List<string> { detachProblem } };
                        }
                    }
                }

                foreach (WallLocationEdit edit in edits)
                {
                    edit.JoinAllowedAtStart = WallUtils.IsWallJoinAllowedAtEnd(edit.Wall, 0);
                    edit.JoinAllowedAtEnd = WallUtils.IsWallJoinAllowedAtEnd(edit.Wall, 1);
                    if (edit.JoinAllowedAtStart) WallUtils.DisallowWallJoinAtEnd(edit.Wall, 0);
                    if (edit.JoinAllowedAtEnd) WallUtils.DisallowWallJoinAtEnd(edit.Wall, 1);
                }
                doc.Regenerate();

                foreach (WallLocationEdit edit in edits)
                    ((LocationCurve)edit.Wall.Location).Curve = Line.CreateBound(edit.NewStart, edit.NewEnd);
                doc.Regenerate();

                foreach (WallLocationEdit edit in edits)
                {
                    if (edit.JoinAllowedAtStart) WallUtils.AllowWallJoinAtEnd(edit.Wall, 0);
                    if (edit.JoinAllowedAtEnd) WallUtils.AllowWallJoinAtEnd(edit.Wall, 1);
                }
                doc.Regenerate();

                // Re-allowing a join can snap an end onto the neighbour's centreline.
                // The requested line wins: that end stays unjoined and is reported.
                var endsLeftUnjoined = new List<string>();
                bool reset = false;
                foreach (WallLocationEdit edit in edits)
                {
                    Curve curve = ((LocationCurve)edit.Wall.Location).Curve;
                    bool startSnapped = curve.GetEndPoint(0).DistanceTo(edit.NewStart) * WallLocationMmPerFoot > WallLocationToleranceMm;
                    bool endSnapped = curve.GetEndPoint(1).DistanceTo(edit.NewEnd) * WallLocationMmPerFoot > WallLocationToleranceMm;
                    if (!startSnapped && !endSnapped) continue;

                    if (startSnapped && WallUtils.IsWallJoinAllowedAtEnd(edit.Wall, 0))
                    {
                        WallUtils.DisallowWallJoinAtEnd(edit.Wall, 0);
                        endsLeftUnjoined.Add($"{edit.Id}:start");
                    }
                    if (endSnapped && WallUtils.IsWallJoinAllowedAtEnd(edit.Wall, 1))
                    {
                        WallUtils.DisallowWallJoinAtEnd(edit.Wall, 1);
                        endsLeftUnjoined.Add($"{edit.Id}:end");
                    }
                    reset = true;
                }
                if (reset)
                {
                    doc.Regenerate();
                    foreach (WallLocationEdit edit in edits)
                    {
                        Curve curve = ((LocationCurve)edit.Wall.Location).Curve;
                        if (curve.GetEndPoint(0).DistanceTo(edit.NewStart) * WallLocationMmPerFoot > WallLocationToleranceMm ||
                            curve.GetEndPoint(1).DistanceTo(edit.NewEnd) * WallLocationMmPerFoot > WallLocationToleranceMm)
                        {
                            ((LocationCurve)edit.Wall.Location).Curve = Line.CreateBound(edit.NewStart, edit.NewEnd);
                        }
                    }
                    doc.Regenerate();
                }

                // Inserts keep their original along-wall position and follow only the perpendicular shift.
                int insertsRestored = 0;
                var expectedInserts = new List<KeyValuePair<WallLocationEdit, KeyValuePair<ElementId, XYZ>>>();
                foreach (WallLocationEdit edit in edits)
                {
                    XYZ direction = (edit.OldEnd - edit.OldStart).Normalize();
                    XYZ startDelta = edit.NewStart - edit.OldStart;
                    XYZ perpendicular = startDelta - direction * startDelta.DotProduct(direction);
                    foreach (KeyValuePair<ElementId, XYZ> insert in edit.InsertPoints)
                    {
                        XYZ expected = insert.Value + new XYZ(perpendicular.X, perpendicular.Y, 0);
                        expectedInserts.Add(new KeyValuePair<WallLocationEdit, KeyValuePair<ElementId, XYZ>>(
                            edit, new KeyValuePair<ElementId, XYZ>(insert.Key, expected)));
                        XYZ now = (doc.GetElement(insert.Key)?.Location as LocationPoint)?.Point;
                        if (now == null) continue;
                        if (now.DistanceTo(expected) * WallLocationMmPerFoot > WallLocationToleranceMm)
                        {
                            ElementTransformUtils.MoveElement(doc, insert.Key, expected - now);
                            insertsRestored++;
                        }
                    }
                }
                doc.Regenerate();

                var mismatches = new List<string>();
                foreach (WallLocationEdit edit in edits)
                {
                    Curve curve = ((LocationCurve)edit.Wall.Location).Curve;
                    double startError = curve.GetEndPoint(0).DistanceTo(edit.NewStart) * WallLocationMmPerFoot;
                    double endError = curve.GetEndPoint(1).DistanceTo(edit.NewEnd) * WallLocationMmPerFoot;
                    if (startError > WallLocationToleranceMm || endError > WallLocationToleranceMm)
                    {
                        XYZ actualStart = curve.GetEndPoint(0) * WallLocationMmPerFoot;
                        XYZ actualEnd = curve.GetEndPoint(1) * WallLocationMmPerFoot;
                        mismatches.Add($"{edit.Id}: start error {startError:F2} mm, end error {endError:F2} mm, actual ({actualStart.X:F2}, {actualStart.Y:F2}) -> ({actualEnd.X:F2}, {actualEnd.Y:F2})");
                    }
                }
                foreach (var entry in expectedInserts)
                {
                    XYZ now = (doc.GetElement(entry.Value.Key)?.Location as LocationPoint)?.Point;
                    if (now == null)
                    {
                        mismatches.Add($"{entry.Key.Id}: insert {entry.Value.Key} lost its location");
                        continue;
                    }
                    double error = now.DistanceTo(entry.Value.Value) * WallLocationMmPerFoot;
                    if (error > WallLocationToleranceMm)
                        mismatches.Add($"{entry.Key.Id}: insert {entry.Value.Key} is {error:F2} mm off its expected position");
                }

                if (mismatches.Count > 0)
                {
                    trans.RollBack();
                    group.RollBack();
                    return new { Success = false, Committed = false, Message = "Rolled back: walls or inserts did not reach the expected positions.", Problems = mismatches };
                }

                TransactionStatus status = trans.Commit();
                if (status != TransactionStatus.Committed)
                {
                    group.RollBack();
                    return new { Success = false, Committed = false, DryRun = dryRun, Message = $"Transaction ended as {status}.", Problems = preprocessor.Errors };
                }

                if (dryRun)
                {
                    group.RollBack();
                    return new { Success = true, Committed = false, DryRun = true, WallCount = edits.Count, InsertCount = expectedInserts.Count, InsertsRestored = insertsRestored, EndsLeftUnjoined = endsLeftUnjoined, JoinsDetached = preprocessor.JoinsDetached.ToList(), SketchEdgesRebuilt = sketchEdgesRebuilt, TopsDetached = topsDetached, Warnings = preprocessor.Warnings.Distinct().ToList() };
                }

                group.Assimilate();
                return new { Success = true, Committed = true, DryRun = false, WallCount = edits.Count, InsertCount = expectedInserts.Count, InsertsRestored = insertsRestored, EndsLeftUnjoined = endsLeftUnjoined, JoinsDetached = preprocessor.JoinsDetached.ToList(), SketchEdgesRebuilt = sketchEdgesRebuilt, TopsDetached = topsDetached, Warnings = preprocessor.Warnings.Distinct().ToList() };
            }
        }

        /// <summary>
        /// Replaces the straight ceiling sketch lines on one plan axis with new lines at the same
        /// position, which drops their association with walls. Returns a problem text, or null.
        /// Must run with no open transaction.
        /// </summary>
        private static string RebuildCeilingSketchEdge(Document doc, JToken edge, List<string> rebuilt)
        {
            IdType ceilingId = edge["elementId"]?.Value<IdType>() ?? 0;
            string axis = (edge["axis"]?.Value<string>() ?? "").ToLowerInvariant();
            double? valueMm = edge["valueMm"]?.Value<double>();
            if ((axis != "x" && axis != "y") || !valueMm.HasValue)
                return $"{ceilingId}: axis must be x or y, and valueMm is required";

            Ceiling ceiling = doc.GetElement(new ElementId(ceilingId)) as Ceiling;
            if (ceiling == null) return $"{ceilingId}: not a ceiling";
            Sketch sketch = doc.GetElement(ceiling.SketchId) as Sketch;
            if (sketch == null) return $"{ceilingId}: sketch not found";

            double target = valueMm.Value / WallLocationMmPerFoot;
            double tolerance = WallLocationToleranceMm / WallLocationMmPerFoot;
            Func<XYZ, double> coordinate = p => axis == "x" ? p.X : p.Y;
            List<CurveElement> lines = sketch.GetAllElements()
                .Select(id => doc.GetElement(id))
                .OfType<CurveElement>()
                .Where(c => c.GeometryCurve is Line line &&
                            Math.Abs(coordinate(line.GetEndPoint(0)) - target) <= tolerance &&
                            Math.Abs(coordinate(line.GetEndPoint(1)) - target) <= tolerance)
                .ToList();
            if (lines.Count == 0) return $"{ceilingId}: no straight sketch line on {axis} = {valueMm.Value:F2}";

            var preprocessor = new WallLocationFailurePreprocessor();
            using (SketchEditScope scope = new SketchEditScope(doc, "Rebuild ceiling sketch edge"))
            {
                scope.Start(sketch.Id);
                using (Transaction edit = new Transaction(doc, "Rebuild ceiling sketch edge"))
                {
                    edit.Start();
                    List<Curve> curves = lines.Select(c => c.GeometryCurve).ToList();
                    doc.Delete(lines.Select(c => c.Id).ToList());
                    foreach (Curve curve in curves)
                        doc.Create.NewModelCurve(curve, sketch.SketchPlane);
                    edit.Commit();
                }
                scope.Commit(preprocessor);
            }

            if (preprocessor.Errors.Count > 0)
                return $"{ceilingId}: {string.Join("; ", preprocessor.Errors)}";
            rebuilt.Add($"{ceilingId}: {lines.Count} line(s) on {axis} = {valueMm.Value:F2}");
            return null;
        }

        #endregion
    }
}
