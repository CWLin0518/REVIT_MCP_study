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
        private object SyncElevationViewExtents(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType sourcePlanId = parameters["sourcePlanViewId"]?.Value<IdType>() ?? 0;
            ViewPlan sourcePlan = doc.GetElement(sourcePlanId.ToElementId()) as ViewPlan;
            JArray mappings = parameters["mappings"] as JArray;
            bool dryRun = parameters["dryRun"]?.Value<bool>() ?? true;

            if (sourcePlan == null || sourcePlan.GenLevel == null)
                throw new Exception("sourcePlanViewId must identify a level-based floor plan.");
            if (mappings == null || mappings.Count == 0)
                throw new Exception("mappings is required.");

            var planned = new List<object>();
            var updated = new List<object>();
            var skipped = new List<object>();

            Action process = () =>
            {
                foreach (JObject item in mappings.OfType<JObject>())
                {
                    IdType sourceViewId = item["sourceViewId"]?.Value<IdType>() ?? 0;
                    IdType targetViewId = item["targetViewId"]?.Value<IdType>() ?? 0;
                    IdType targetPlanId = item["targetPlanViewId"]?.Value<IdType>() ?? 0;
                    ViewSection source = doc.GetElement(sourceViewId.ToElementId()) as ViewSection;
                    ViewSection target = doc.GetElement(targetViewId.ToElementId()) as ViewSection;
                    ViewPlan targetPlan = doc.GetElement(targetPlanId.ToElementId()) as ViewPlan;
                    if (source == null || source.ViewType != ViewType.Elevation ||
                        target == null || target.ViewType != ViewType.Elevation ||
                        targetPlan == null || targetPlan.GenLevel == null)
                    {
                        skipped.Add(new { SourceViewId = sourceViewId, TargetViewId = targetViewId, TargetPlanViewId = targetPlanId, Reason = "Invalid elevation or target plan." });
                        continue;
                    }

                    try
                    {
                        BoundingBoxXYZ sourceBox = source.CropBox;
                        BoundingBoxXYZ targetBox = target.CropBox;
                        double levelDelta = targetPlan.GenLevel.Elevation - sourcePlan.GenLevel.Elevation;
                        XYZ shift = XYZ.BasisZ * levelDelta;
                        Transform targetInverse = targetBox.Transform.Inverse;
                        var localCorners = new List<XYZ>();
                        foreach (double x in new[] { sourceBox.Min.X, sourceBox.Max.X })
                        foreach (double y in new[] { sourceBox.Min.Y, sourceBox.Max.Y })
                        foreach (double z in new[] { sourceBox.Min.Z, sourceBox.Max.Z })
                        {
                            XYZ world = sourceBox.Transform.OfPoint(new XYZ(x, y, z)) + shift;
                            localCorners.Add(targetInverse.OfPoint(world));
                        }

                        XYZ newMin = new XYZ(localCorners.Min(p => p.X), localCorners.Min(p => p.Y), localCorners.Min(p => p.Z));
                        XYZ newMax = new XYZ(localCorners.Max(p => p.X), localCorners.Max(p => p.Y), localCorners.Max(p => p.Z));
                        double sourceBottomWorld = GetCropWorldZ(sourceBox, false);
                        double sourceTopWorld = GetCropWorldZ(sourceBox, true);
                        double bottomOffset = sourceBottomWorld - sourcePlan.GenLevel.Elevation;
                        double topOffset = sourceTopWorld - sourcePlan.GenLevel.Elevation;
                        double sourceFar = GetFarClipOffset(source);

                        var report = new
                        {
                            SourceViewId = source.Id.GetIdValue(),
                            SourceViewName = source.Name,
                            TargetViewId = target.Id.GetIdValue(),
                            TargetViewName = target.Name,
                            TargetLevel = targetPlan.GenLevel.Name,
                            BottomOffsetMm = UnitUtils.ConvertFromInternalUnits(bottomOffset, UnitTypeId.Millimeters),
                            TopOffsetMm = UnitUtils.ConvertFromInternalUnits(topOffset, UnitTypeId.Millimeters),
                            WidthMm = UnitUtils.ConvertFromInternalUnits(newMax.X - newMin.X, UnitTypeId.Millimeters),
                            HeightMm = UnitUtils.ConvertFromInternalUnits(newMax.Y - newMin.Y, UnitTypeId.Millimeters),
                            DepthMm = UnitUtils.ConvertFromInternalUnits(newMax.Z - newMin.Z, UnitTypeId.Millimeters),
                            FarClipOffsetMm = sourceFar > 0 ? UnitUtils.ConvertFromInternalUnits(sourceFar, UnitTypeId.Millimeters) : (double?)null
                        };
                        planned.Add(report);

                        if (!dryRun)
                        {
                            targetBox.Min = newMin;
                            targetBox.Max = newMax;
                            target.CropBox = targetBox;
                            target.CropBoxActive = source.CropBoxActive;
                            target.CropBoxVisible = source.CropBoxVisible;
                            doc.Regenerate();
                            CopyFarClipSettings(source, target);
                            doc.Regenerate();
                            updated.Add(report);
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(new { SourceViewId = sourceViewId, TargetViewId = targetViewId, Reason = ex.Message });
                    }
                }
            };

            if (dryRun)
            {
                process();
            }
            else
            {
                using (Transaction tx = TransactionHelper.Begin(doc, "Sync elevation view extents"))
                {
                    tx.Start();
                    process();
                    tx.Commit();
                }
            }

            return new { DryRun = dryRun, PlannedCount = planned.Count, UpdatedCount = updated.Count, SkippedCount = skipped.Count, Planned = planned, Updated = updated, Skipped = skipped };
        }

        private static double GetCropWorldZ(BoundingBoxXYZ box, bool top)
        {
            double localY = top ? box.Max.Y : box.Min.Y;
            double z1 = box.Transform.OfPoint(new XYZ(box.Min.X, localY, box.Min.Z)).Z;
            double z2 = box.Transform.OfPoint(new XYZ(box.Max.X, localY, box.Max.Z)).Z;
            return top ? Math.Max(z1, z2) : Math.Min(z1, z2);
        }

        private static double GetFarClipOffset(ViewSection view)
        {
            Parameter p = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : -1.0;
        }

        private static void CopyFarClipSettings(ViewSection source, ViewSection target)
        {
            Parameter sourceMode = source.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
            Parameter targetMode = target.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
            if (sourceMode != null && targetMode != null && !targetMode.IsReadOnly)
                targetMode.Set(sourceMode.AsInteger());

            Parameter sourceOffset = source.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            Parameter targetOffset = target.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            if (sourceOffset != null && targetOffset != null && !targetOffset.IsReadOnly)
                targetOffset.Set(sourceOffset.AsDouble());
        }
    }
}
