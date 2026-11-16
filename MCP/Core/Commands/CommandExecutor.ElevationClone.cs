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
        private object CloneElevationViewsToPlans(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            JArray sourceIdsToken = parameters["sourceViewIds"] as JArray;
            JArray targetsToken = parameters["targets"] as JArray;
            bool dryRun = parameters["dryRun"]?.Value<bool>() ?? true;
            bool copyViewTemplate = parameters["copyViewTemplate"]?.Value<bool>() ?? true;
            IdType sourcePlanViewId = parameters["sourcePlanViewId"]?.Value<IdType>() ?? 0;
            ViewPlan sourcePlan = doc.GetElement(sourcePlanViewId.ToElementId()) as ViewPlan;

            if (sourceIdsToken == null || sourceIdsToken.Count == 0)
                throw new Exception("sourceViewIds is required.");
            if (targetsToken == null || targetsToken.Count == 0)
                throw new Exception("targets is required.");
            if (sourcePlan == null || sourcePlan.ViewType != ViewType.FloorPlan)
                throw new Exception("sourcePlanViewId must identify the floor plan that displays the source elevation markers.");

            var sourceViews = sourceIdsToken
                .Select(t => doc.GetElement(t.Value<IdType>().ToElementId()) as ViewSection)
                .ToList();
            if (sourceViews.Any(v => v == null || v.ViewType != ViewType.Elevation))
                throw new Exception("Every sourceViewId must identify an elevation ViewSection.");

            var markers = new FilteredElementCollector(doc)
                .OfClass(typeof(ElevationMarker))
                .Cast<ElevationMarker>()
                .ToList();

            var sources = sourceViews.Select(view =>
            {
                ElevationMarker marker = markers.FirstOrDefault(m =>
                    Enumerable.Range(0, m.MaximumViewCount).Any(i => m.GetViewId(i) == view.Id));
                if (marker == null)
                    throw new Exception($"Cannot find ElevationMarker for source view '{view.Name}' ({view.Id.GetIdValue()}).");
                XYZ sourcePoint = ResolveElevationMarkerPoint(marker, sourcePlan);
                if (sourcePoint == null)
                    throw new Exception($"Cannot resolve marker position for source view '{view.Name}'.");
                return new ElevationCloneSource
                {
                    View = view,
                    Marker = marker,
                    Point = sourcePoint,
                    Direction = FlattenElevationDirection(view.ViewDirection)
                };
            }).ToList();

            var existingNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);
            var planned = new List<object>();
            var created = new List<object>();
            var skipped = new List<object>();

            Action createAll = () =>
            {
                foreach (JObject target in targetsToken.OfType<JObject>())
                {
                    IdType planId = target["planViewId"]?.Value<IdType>() ?? 0;
                    string sourcePrefix = target["sourcePrefix"]?.Value<string>() ?? "1F";
                    string targetPrefix = target["targetPrefix"]?.Value<string>();
                    ViewPlan plan = doc.GetElement(planId.ToElementId()) as ViewPlan;
                    if (plan == null || plan.ViewType != ViewType.FloorPlan || plan.GenLevel == null)
                    {
                        skipped.Add(new { PlanViewId = planId, Reason = "Target is not a level-based floor plan." });
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(targetPrefix))
                        targetPrefix = plan.GenLevel.Name.Replace("L", "");

                    foreach (ElevationCloneSource source in sources)
                    {
                        string newName = source.View.Name.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
                            ? targetPrefix + source.View.Name.Substring(sourcePrefix.Length)
                            : targetPrefix + "_" + source.View.Name;
                        XYZ point = new XYZ(source.Point.X, source.Point.Y, plan.GenLevel.Elevation);

                        planned.Add(new
                        {
                            SourceViewId = source.View.Id.GetIdValue(),
                            SourceViewName = source.View.Name,
                            TargetPlanId = plan.Id.GetIdValue(),
                            TargetPlanName = plan.Name,
                            NewViewName = newName,
                            Xmm = UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters),
                            Ymm = UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters),
                            Zmm = UnitUtils.ConvertFromInternalUnits(point.Z, UnitTypeId.Millimeters)
                        });

                        if (dryRun)
                            continue;
                        if (existingNames.Contains(newName))
                        {
                            skipped.Add(new { TargetPlanId = plan.Id.GetIdValue(), NewViewName = newName, Reason = "View name already exists." });
                            continue;
                        }

                        try
                        {
                            ElevationMarker marker = ElevationMarker.CreateElevationMarker(
                                doc, source.View.GetTypeId(), point, source.View.Scale);
                            ViewSection view = marker.CreateElevation(doc, plan.Id, 0);
                            doc.Regenerate();

                            XYZ current = FlattenElevationDirection(view.ViewDirection);
                            double angle = Math.Atan2(
                                current.X * source.Direction.Y - current.Y * source.Direction.X,
                                current.X * source.Direction.X + current.Y * source.Direction.Y);
                            if (Math.Abs(angle) > 1e-9)
                            {
                                ElementTransformUtils.RotateElement(doc, marker.Id,
                                    Line.CreateBound(point, point + XYZ.BasisZ), angle);
                                doc.Regenerate();
                            }

                            view.Name = newName;
                            view.Scale = source.View.Scale;
                            if (copyViewTemplate && source.View.ViewTemplateId != ElementId.InvalidElementId)
                                view.ViewTemplateId = source.View.ViewTemplateId;
                            existingNames.Add(newName);

                            XYZ finalDirection = FlattenElevationDirection(view.ViewDirection);
                            created.Add(new
                            {
                                MarkerId = marker.Id.GetIdValue(),
                                ViewId = view.Id.GetIdValue(),
                                ViewName = view.Name,
                                ViewTypeId = view.GetTypeId().GetIdValue(),
                                TargetPlanId = plan.Id.GetIdValue(),
                                TargetPlanName = plan.Name,
                                Xmm = UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters),
                                Ymm = UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters),
                                Zmm = UnitUtils.ConvertFromInternalUnits(point.Z, UnitTypeId.Millimeters),
                                DirectionX = finalDirection.X,
                                DirectionY = finalDirection.Y,
                                Scale = view.Scale
                            });
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new { TargetPlanId = plan.Id.GetIdValue(), NewViewName = newName, Reason = ex.Message });
                        }
                    }
                }
            };

            if (dryRun)
            {
                createAll();
            }
            else
            {
                using (Transaction tx = TransactionHelper.Begin(doc, "Clone elevation views to floor plans"))
                {
                    tx.Start();
                    createAll();
                    tx.Commit();
                }
            }

            return new
            {
                DryRun = dryRun,
                PlannedCount = planned.Count,
                CreatedCount = created.Count,
                SkippedCount = skipped.Count,
                Planned = planned,
                Created = created,
                Skipped = skipped
            };
        }

        private static XYZ FlattenElevationDirection(XYZ direction)
        {
            XYZ flat = new XYZ(direction.X, direction.Y, 0.0);
            if (flat.GetLength() < 1e-9)
                throw new Exception("Elevation view direction is not horizontal.");
            return flat.Normalize();
        }

        private static XYZ ResolveElevationMarkerPoint(ElevationMarker marker, ViewPlan sourcePlan)
        {
            LocationPoint location = marker.Location as LocationPoint;
            if (location != null)
                return location.Point;

            Options options = new Options { View = sourcePlan, IncludeNonVisibleObjects = true };
            GeometryElement geometry = marker.get_Geometry(options);
            if (geometry != null)
            {
                foreach (GeometryObject obj in geometry)
                {
                    GeometryInstance instance = obj as GeometryInstance;
                    if (instance != null)
                        return instance.Transform.Origin;
                }
            }

            BoundingBoxXYZ box = marker.get_BoundingBox(sourcePlan);
            if (box != null)
                return (box.Min + box.Max) * 0.5;
            return null;
        }

        private sealed class ElevationCloneSource
        {
            public ViewSection View { get; set; }
            public ElevationMarker Marker { get; set; }
            public XYZ Point { get; set; }
            public XYZ Direction { get; set; }
        }
    }
}
