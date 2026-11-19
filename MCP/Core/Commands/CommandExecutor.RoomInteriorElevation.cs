using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>
        /// Place one 4-way ElevationMarker at a room's location point, create the four
        /// interior elevation views, and crop each view to the room boundary extents.
        /// </summary>
        private object CreateRoomInteriorElevations(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            bool dryRun = parameters["dryRun"]?.Value<bool>() ?? true;
            IdType roomIdValue = parameters["roomId"]?.Value<IdType>() ?? 0;
            string roomNumber = parameters["roomNumber"]?.Value<string>();
            string typeName = parameters["viewFamilyTypeName"]?.Value<string>() ?? "Interior Elevation";
            int scale = parameters["scale"]?.Value<int>() ?? 100;
            bool fitToHosts = parameters["fitToHosts"]?.Value<bool>() ?? true;
            double offsetMm = parameters["offsetMm"]?.Value<double>() ?? 0.0;
            string namePrefix = parameters["namePrefix"]?.Value<string>();
            IdType planViewIdValue = parameters["planViewId"]?.Value<IdType>() ?? 0;
            IdType templateIdValue = parameters["viewTemplateId"]?.Value<IdType>() ?? 0;
            IdType templateFromViewIdValue = parameters["copyTemplateFromViewId"]?.Value<IdType>() ?? 0;

            // --- Resolve room ---
            Room room = null;
            if (roomIdValue != 0)
                room = doc.GetElement(roomIdValue.ToElementId()) as Room;
            else if (!string.IsNullOrWhiteSpace(roomNumber))
                room = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .FirstOrDefault(r => r.Area > 0 && string.Equals(r.Number, roomNumber, StringComparison.OrdinalIgnoreCase));
            if (room == null)
                throw new Exception("Room not found. Provide roomId or roomNumber of a placed room.");
            LocationPoint roomLocation = room.Location as LocationPoint;
            if (roomLocation == null || room.Level == null)
                throw new Exception($"Room {room.Number} is not placed or has no level.");

            // --- Resolve host floor plan on the room's level ---
            ViewPlan plan = null;
            if (planViewIdValue != 0)
                plan = doc.GetElement(planViewIdValue.ToElementId()) as ViewPlan;
            if (plan == null)
            {
                ViewPlan active = doc.ActiveView as ViewPlan;
                if (active != null && !active.IsTemplate && active.ViewType == ViewType.FloorPlan
                    && active.GenLevel != null && active.GenLevel.Id == room.LevelId)
                    plan = active;
            }
            if (plan == null)
                plan = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan
                        && v.GenLevel != null && v.GenLevel.Id == room.LevelId);
            if (plan == null || plan.ViewType != ViewType.FloorPlan)
                throw new Exception("No floor plan found on the room's level; pass planViewId.");

            // --- Resolve elevation view family type ---
            ViewFamilyType vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.Elevation
                    && string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (vft == null)
                throw new Exception($"Elevation ViewFamilyType '{typeName}' not found.");

            // --- Resolve view template ---
            ElementId templateId = ElementId.InvalidElementId;
            if (templateIdValue != 0)
                templateId = templateIdValue.ToElementId();
            else if (templateFromViewIdValue != 0)
            {
                View src = doc.GetElement(templateFromViewIdValue.ToElementId()) as View;
                if (src == null)
                    throw new Exception("copyTemplateFromViewId does not identify a view.");
                templateId = src.ViewTemplateId;
            }

            // --- Geometry: room boundary points and vertical extent ---
            var boundaryOptions = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };
            var boundaryPoints = new List<XYZ>();
            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(boundaryOptions);
            if (loops != null)
                foreach (var loop in loops)
                    foreach (var seg in loop)
                        boundaryPoints.AddRange(seg.GetCurve().Tessellate());
            if (boundaryPoints.Count == 0)
                throw new Exception($"Room {room.Number} has no boundary segments.");

            BoundingBoxXYZ roomBox = room.get_BoundingBox(null);
            double zBottom = roomBox?.Min.Z ?? room.Level.Elevation;
            double zTop = roomBox?.Max.Z ?? (room.Level.Elevation + room.UnboundedHeight);
            double offset = UnitUtils.ConvertToInternalUnits(offsetMm, UnitTypeId.Millimeters);

            // --- Fit to host elements: bounding wall thickness, slab below, slab above ---
            double sideExtra = 0.0;
            string floorBelowInfo = null, floorAboveInfo = null;
            if (fitToHosts)
            {
                if (loops != null)
                    foreach (var loop in loops)
                        foreach (var seg in loop)
                        {
                            Wall w = doc.GetElement(seg.ElementId) as Wall;
                            if (w != null) sideExtra = Math.Max(sideExtra, w.Width);
                        }

                double bMinX = boundaryPoints.Min(p => p.X), bMaxX = boundaryPoints.Max(p => p.X);
                double bMinY = boundaryPoints.Min(p => p.Y), bMaxY = boundaryPoints.Max(p => p.Y);
                double baseZ = zBottom;
                double tol = UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Millimeters);
                var floors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .Select(f => new { F = f, B = f.get_BoundingBox(null) })
                    .Where(x => x.B != null
                        && x.B.Min.X < bMaxX && x.B.Max.X > bMinX
                        && x.B.Min.Y < bMaxY && x.B.Max.Y > bMinY)
                    .ToList();

                var below = floors
                    .Where(x => x.B.Max.Z <= baseZ + tol * 0.5 && x.B.Max.Z >= baseZ - tol)
                    .OrderByDescending(x => x.B.Max.Z).FirstOrDefault();
                if (below != null)
                {
                    zBottom = Math.Min(zBottom, below.B.Min.Z);
                    floorBelowInfo = $"{below.F.Id.GetIdValue()} {below.F.Name}";
                }

                var above = floors
                    .Where(x => x.B.Min.Z >= baseZ + tol * 0.5)
                    .OrderBy(x => x.B.Min.Z).FirstOrDefault();
                if (above != null)
                {
                    zTop = above.B.Max.Z;
                    floorAboveInfo = $"{above.F.Id.GetIdValue()} {above.F.Name}";
                }
            }

            XYZ markerPoint = new XYZ(roomLocation.Point.X, roomLocation.Point.Y, room.Level.Elevation);
            string roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(namePrefix))
                namePrefix = room.Number + roomName.Trim();

            var names = Enumerable.Range(1, 4).Select(i => namePrefix + i).ToList();
            var existingNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);
            var conflicts = names.Where(existingNames.Contains).ToList();

            Func<double, double> mm = v => Math.Round(UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Millimeters), 1);
            var summary = new
            {
                RoomId = room.Id.GetIdValue(),
                RoomNumber = room.Number,
                RoomName = roomName,
                Level = room.Level.Name,
                PlanViewId = plan.Id.GetIdValue(),
                PlanViewName = plan.Name,
                ViewFamilyTypeId = vft.Id.GetIdValue(),
                ViewFamilyTypeName = vft.Name,
                ViewTemplateId = templateId.GetIdValue(),
                Scale = scale,
                MarkerXmm = mm(markerPoint.X),
                MarkerYmm = mm(markerPoint.Y),
                MarkerZmm = mm(markerPoint.Z),
                FitToHosts = fitToHosts,
                MaxWallThicknessMm = mm(sideExtra),
                FloorBelow = floorBelowInfo,
                FloorAbove = floorAboveInfo,
                CropBottomZmm = mm(zBottom - offset),
                CropTopZmm = mm(zTop + offset),
                PlannedNames = names,
                NameConflicts = conflicts
            };

            if (dryRun)
                return new { DryRun = true, Plan = summary };
            if (conflicts.Count > 0)
                throw new Exception("View names already exist: " + string.Join(", ", conflicts));

            var created = new List<object>();
            ElevationMarker marker;
            using (Transaction tx = TransactionHelper.Begin(doc, "Create room interior elevations"))
            {
                tx.Start();
                marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, markerPoint, scale);

                for (int i = 0; i < 4; i++)
                {
                    ViewSection view = marker.CreateElevation(doc, plan.Id, i);
                    view.Name = names[i];
                    if (templateId != ElementId.InvalidElementId)
                        view.ViewTemplateId = templateId;
                    doc.Regenerate();

                    // Crop to room extents in view-local coordinates.
                    BoundingBoxXYZ crop = view.CropBox;
                    Transform inv = crop.Transform.Inverse;
                    var local = boundaryPoints.Select(p => inv.OfPoint(p)).ToList();
                    double minX = local.Min(p => p.X) - sideExtra - offset;
                    double maxX = local.Max(p => p.X) + sideExtra + offset;
                    double minY = inv.OfPoint(new XYZ(markerPoint.X, markerPoint.Y, zBottom)).Y - offset;
                    double maxY = inv.OfPoint(new XYZ(markerPoint.X, markerPoint.Y, zTop)).Y + offset;
                    // Points in front of the view plane have negative local Z.
                    double depth = local.Max(p => -p.Z) + sideExtra + offset;

                    view.CropBoxActive = true;
                    view.CropBoxVisible = true;
                    var newCrop = new BoundingBoxXYZ
                    {
                        Transform = crop.Transform,
                        Min = new XYZ(minX, minY, crop.Min.Z),
                        Max = new XYZ(maxX, maxY, crop.Max.Z)
                    };
                    view.CropBox = newCrop;

                    string farClipNote = null;
                    try
                    {
                        Parameter clip = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                        if (clip != null && !clip.IsReadOnly) clip.Set(1);
                        Parameter far = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                        if (far != null && !far.IsReadOnly && depth > 0) far.Set(depth);
                        else farClipNote = "Far clip offset is read-only (likely controlled by view template).";
                    }
                    catch (Exception ex) { farClipNote = ex.Message; }

                    XYZ dir = view.ViewDirection;
                    created.Add(new
                    {
                        ViewId = view.Id.GetIdValue(),
                        ViewName = view.Name,
                        LooksTowardX = Math.Round(-dir.X, 3),
                        LooksTowardY = Math.Round(-dir.Y, 3),
                        CropWidthMm = mm(maxX - minX),
                        CropHeightMm = mm(maxY - minY),
                        FarClipMm = mm(depth),
                        Note = farClipNote
                    });
                }
                tx.Commit();
            }

            return new
            {
                DryRun = false,
                Plan = summary,
                MarkerId = marker.Id.GetIdValue(),
                CreatedCount = created.Count,
                Created = created
            };
        }
    }
}
