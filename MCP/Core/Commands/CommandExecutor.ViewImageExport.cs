#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        #region export_view_image

        // export_view_image: export a plan view (or a region of it) to PNG for remote preview.
        //
        // Read-only for the model: every change (temporary duplicate view, crop box, annotation crop,
        // scope box removal) is made inside a TransactionGroup that is ALWAYS rolled back after
        // Document.ExportImage. The only lasting side effect is the PNG written to outputDir.
        //
        // Coordinate frame used in the response ("mapping frame"):
        //   x' = P . viewRight, y' = P . viewUp   (P = project internal XY, mm, from the internal origin)
        // For an unrotated plan (viewRight=(1,0), viewUp=(0,1)) this IS project internal XY.
        // crop, region.outline, origin and grids are all expressed in this frame, so the pixel
        // mapping formula holds for every returned coordinate.

        private const double VieMmPerFt = 304.8;

        private sealed class VieFrame
        {
            public XYZ Right;
            public XYZ Up;
            public double[] Of(XYZ p) { return new[] { p.X * Right.X + p.Y * Right.Y, p.X * Up.X + p.Y * Up.Y }; }
            public XYZ ToWorld(double fx, double fy) { return new XYZ(fx * Right.X + fy * Up.X, fx * Right.Y + fy * Up.Y, 0); }
        }

        private sealed class VieBox
        {
            public double MinX, MinY, MaxX, MaxY; // feet, mapping frame
            public double W { get { return MaxX - MinX; } }
            public double H { get { return MaxY - MinY; } }
        }

        private object ExportViewImage(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            var warnings = new JArray();

            // ---- parameters ----
            string outputDir = parameters["outputDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(outputDir) || !Path.IsPathRooted(outputDir))
                throw new Exception("outputDir is required and must be an absolute folder path.");
            Directory.CreateDirectory(outputDir);

            int pixelWidth = parameters["pixelWidth"]?.Value<int?>() ?? 2000;
            if (pixelWidth < 200 || pixelWidth > 15000)
                throw new Exception("pixelWidth must be between 200 and 15000.");

            JToken marginTok = parameters["marginMm"];
            bool marginGiven = marginTok != null && marginTok.Type != JTokenType.Null;
            double marginMm = marginGiven ? marginTok.Value<double>() : 1000.0;
            if (marginMm < 0) throw new Exception("marginMm must be >= 0.");

            ViewPlan view = VieResolveView(doc, parameters, warnings);

            BoundingBoxXYZ srcCrop = view.CropBox;
            var srcFrame = new VieFrame { Right = VieHorizontalUnit(srcCrop.Transform.BasisX), Up = VieHorizontalUnit(srcCrop.Transform.BasisY) };

            // ---- region (resolved in world XY, feet) ----
            JObject region = parameters["region"] as JObject;
            string regionType = "crop";
            string regionLabel = null;
            List<XYZ> outlineWorld;
            XYZ originWorld = null;
            string originLabel = null;
            string[] regionKeys = { "grids", "roomNumber", "roomId", "areaId", "bboxMm" };
            var givenKeys = region == null ? new List<string>()
                : regionKeys.Where(k => region[k] != null && region[k].Type != JTokenType.Null).ToList();
            if (givenKeys.Count > 1)
                throw new Exception("region accepts at most one of grids, roomNumber, roomId, areaId, bboxMm; got: " + string.Join(", ", givenKeys));
            string key = givenKeys.FirstOrDefault();

            if (key == "grids")
            {
                regionType = "grids";
                outlineWorld = VieGridRegion(doc, region["grids"] as JObject, srcFrame, warnings, out regionLabel, out originWorld, out originLabel);
            }
            else if (key == "roomNumber" || key == "roomId")
            {
                regionType = "room";
                Room room = VieResolveRoom(doc, view, region, key, warnings);
                outlineWorld = VieSpatialOutline(room);
                regionLabel = (room.Number + " " + room.Name).Trim();
            }
            else if (key == "areaId")
            {
                regionType = "area";
                IdType areaId = region["areaId"].Value<IdType>();
                var area = doc.GetElement(areaId.ToElementId()) as Area;
                if (area == null) throw new Exception($"areaId {areaId} is not an Area element.");
                if (area.Area <= 0) throw new Exception($"Area {areaId} is not placed or not enclosed (area = 0).");
                outlineWorld = VieSpatialOutline(area);
                regionLabel = (area.Number + " " + area.Name).Trim();
            }
            else if (key == "bboxMm")
            {
                regionType = "bbox";
                var b = region["bboxMm"] as JObject;
                if (b == null) throw new Exception("bboxMm must be an object {minX,minY,maxX,maxY} in mm.");
                double bx0 = b["minX"].Value<double>() / VieMmPerFt, by0 = b["minY"].Value<double>() / VieMmPerFt;
                double bx1 = b["maxX"].Value<double>() / VieMmPerFt, by1 = b["maxY"].Value<double>() / VieMmPerFt;
                if (bx1 <= bx0 || by1 <= by0) throw new Exception("bboxMm requires maxX > minX and maxY > minY.");
                outlineWorld = new List<XYZ> { new XYZ(bx0, by0, 0), new XYZ(bx1, by0, 0), new XYZ(bx1, by1, 0), new XYZ(bx0, by1, 0) };
                regionLabel = "bbox";
            }
            else
            {
                regionType = "crop";
                if (!view.CropBoxActive)
                    warnings.Add("The view's crop region was not active; its stored crop box was used and activated on the temporary view.");
                Transform t = srcCrop.Transform;
                outlineWorld = new List<XYZ>
                {
                    t.OfPoint(new XYZ(srcCrop.Min.X, srcCrop.Min.Y, 0)), t.OfPoint(new XYZ(srcCrop.Max.X, srcCrop.Min.Y, 0)),
                    t.OfPoint(new XYZ(srcCrop.Max.X, srcCrop.Max.Y, 0)), t.OfPoint(new XYZ(srcCrop.Min.X, srcCrop.Max.Y, 0))
                };
                regionLabel = "view crop";
                if (!marginGiven) marginMm = 0; // "export the view's current crop": no margin unless asked
            }
            double marginFt = marginMm / VieMmPerFt;

            // ---- temporary view, crop, export (always rolled back) ----
            string tmpDir = Path.Combine(outputDir, ".export_tmp_" + Guid.NewGuid().ToString("N"));
            string exportedTmpFile = null;
            string tempStrategy = null;
            VieFrame frame = srcFrame;
            VieBox modelCrop = null, extent = null;
            var gridsOut = new JArray();
            JObject mappingCheck = new JObject();
            double outlineW = 0, outlineH = 0;
            BoundingBoxUV viewOutline = null;
            int viewScale = view.Scale;

            try
            {
                using (var tg = new TransactionGroup(doc, "MCP export_view_image (rolled back)"))
                {
                    tg.Start();
                    try
                    {
                        View exportView;
                        using (Transaction t = TransactionHelper.Begin(doc, "Temporary crop for image export"))
                        {
                            t.Start();
                            exportView = VieMakeTempView(doc, view, warnings, out tempStrategy);

                            BoundingBoxXYZ tmpCrop = exportView.CropBox;
                            Transform tf = tmpCrop.Transform;
                            frame = new VieFrame { Right = VieHorizontalUnit(tf.BasisX), Up = VieHorizontalUnit(tf.BasisY) };

                            var regionBox = VieBoxOf(outlineWorld.Select(frame.Of));
                            var want = new VieBox
                            {
                                MinX = regionBox.MinX - marginFt, MinY = regionBox.MinY - marginFt,
                                MaxX = regionBox.MaxX + marginFt, MaxY = regionBox.MaxY + marginFt
                            };

                            Parameter sb = exportView.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                            if (sb != null && sb.AsElementId() != ElementId.InvalidElementId)
                            {
                                if (sb.IsReadOnly) throw new Exception("The view's crop is controlled by a scope box that cannot be cleared on the temporary view.");
                                sb.Set(ElementId.InvalidElementId);
                            }

                            exportView.CropBoxActive = true;
                            exportView.CropBoxVisible = false;
                            try
                            {
                                ViewCropRegionShapeManager mgr = exportView.GetCropRegionShapeManager();
                                if (mgr != null && mgr.ShapeSet) mgr.RemoveCropRegionShape();
                            }
                            catch (Exception ex) { warnings.Add("Could not reset a non-rectangular crop shape: " + ex.Message); }

                            Parameter ac = exportView.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                            if (ac != null && !ac.IsReadOnly) ac.Set(0);

                            // frame -> crop-local: local.X = x' - O.right, local.Y = y' - O.up
                            double[] o = frame.Of(tf.Origin);
                            exportView.CropBox = new BoundingBoxXYZ
                            {
                                Transform = tf,
                                Min = new XYZ(want.MinX - o[0], want.MinY - o[1], tmpCrop.Min.Z),
                                Max = new XYZ(want.MaxX - o[0], want.MaxY - o[1], tmpCrop.Max.Z)
                            };

                            if (t.Commit() != TransactionStatus.Committed)
                                throw new Exception("Temporary crop transaction did not commit.");
                        }

                        // Read back what Revit actually applied (it may clamp the crop).
                        BoundingBoxXYZ applied = exportView.CropBox;
                        Transform atf = applied.Transform;
                        modelCrop = VieBoxOf(new[]
                        {
                            frame.Of(atf.OfPoint(new XYZ(applied.Min.X, applied.Min.Y, 0))),
                            frame.Of(atf.OfPoint(new XYZ(applied.Max.X, applied.Max.Y, 0)))
                        });
                        viewScale = exportView.Scale;
                        try { viewOutline = exportView.Outline; } catch { viewOutline = null; }

                        // Grids visible in the temporary view (its crop = the exported extent).
                        var gridData = new List<Tuple<string, double[], double[]>>();
                        foreach (Grid g in new FilteredElementCollector(doc, exportView.Id).OfClass(typeof(Grid)).Cast<Grid>())
                        {
                            if (g.IsCurved) continue;
                            Line line = null;
                            try
                            {
                                IList<Curve> cs = g.GetCurvesInView(DatumExtentType.ViewSpecific, exportView);
                                line = cs?.OfType<Line>().FirstOrDefault();
                            }
                            catch { }
                            if (line == null) line = g.Curve as Line;
                            if (line == null) continue;
                            gridData.Add(Tuple.Create(g.Name, frame.Of(line.GetEndPoint(0)), frame.Of(line.GetEndPoint(1))));
                        }

                        Directory.CreateDirectory(tmpDir);
                        var opts = new ImageExportOptions
                        {
                            ExportRange = ExportRange.SetOfViews,
                            FilePath = Path.Combine(tmpDir, "export"),
                            ZoomType = ZoomFitType.FitToPage,
                            FitDirection = FitDirectionType.Horizontal,
                            PixelSize = pixelWidth,
                            HLRandWFViewsFileType = ImageFileType.PNG,
                            ShadowViewsFileType = ImageFileType.PNG,
                            ImageResolution = ImageResolution.DPI_150
                        };
                        opts.SetViewsAndSheets(new List<ElementId> { exportView.Id });
                        doc.ExportImage(opts);

                        exportedTmpFile = Directory.GetFiles(tmpDir)
                            .Where(f => string.Equals(Path.GetExtension(f), ".png", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                            .FirstOrDefault();
                        if (exportedTmpFile == null)
                            throw new Exception("Revit ExportImage finished but no PNG was written.");

                        // stash grid data for clipping after the extent is known
                        foreach (var gd in gridData)
                            gridsOut.Add(new JArray(gd.Item1, gd.Item2[0], gd.Item2[1], gd.Item3[0], gd.Item3[1]));
                    }
                    finally
                    {
                        if (tg.GetStatus() == TransactionStatus.Started) tg.RollBack();
                    }
                }

                // ---- final file ----
                string baseName = VieSanitize(view.Name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string finalPath = Path.Combine(outputDir, baseName + ".png");
                for (int i = 1; File.Exists(finalPath); i++) finalPath = Path.Combine(outputDir, baseName + "_" + i + ".png");
                File.Move(exportedTmpFile, finalPath);

                int imgW, imgH;
                VieReadPngSize(finalPath, out imgW, out imgH);

                // ---- exported extent / mapping verification ----
                extent = modelCrop;
                double imgAspect = (double)imgW / imgH;
                double aspectTol = 0.005 + 2.0 / Math.Min(imgW, imgH);
                string status;
                if (viewOutline != null)
                {
                    outlineW = (viewOutline.Max.U - viewOutline.Min.U) * viewScale;
                    outlineH = (viewOutline.Max.V - viewOutline.Min.V) * viewScale;
                    var olBox = new VieBox
                    {
                        MinX = viewOutline.Min.U * viewScale, MinY = viewOutline.Min.V * viewScale,
                        MaxX = viewOutline.Max.U * viewScale, MaxY = viewOutline.Max.V * viewScale
                    };
                    double tolFt = Math.Max(0.01, 0.002 * Math.Max(modelCrop.W, modelCrop.H));
                    bool sizeMatches = Math.Abs(outlineW - modelCrop.W) <= tolFt && Math.Abs(outlineH - modelCrop.H) <= tolFt;
                    bool contains = olBox.MinX <= modelCrop.MinX + tolFt && olBox.MinY <= modelCrop.MinY + tolFt
                                    && olBox.MaxX >= modelCrop.MaxX - tolFt && olBox.MaxY >= modelCrop.MaxY - tolFt;
                    if (sizeMatches)
                        status = "outline_matches_crop";
                    else if (contains && Math.Abs(imgAspect - olBox.W / olBox.H) / (olBox.W / olBox.H) <= aspectTol)
                    {
                        status = "outline_extends_crop";
                        extent = olBox;
                        warnings.Add("Annotations (e.g. grid heads) extend beyond the model crop, so the image covers more than the crop. 'crop' is the larger exported extent derived from View.Outline (paper outline x scale); 'modelCrop' is the crop box that was set. Verify on a live test.");
                    }
                    else
                    {
                        status = "unverified";
                        warnings.Add($"Mapping unverified: View.Outline x scale = {VieMm(outlineW)} x {VieMm(outlineH)} mm, crop = {VieMm(modelCrop.W)} x {VieMm(modelCrop.H)} mm. The mapping assumes the image edges are the crop edges; check against a known grid intersection.");
                    }
                }
                else
                {
                    status = "unverified";
                    warnings.Add("View.Outline was unavailable; the mapping assumes the image edges are the crop edges.");
                }
                double extentAspect = extent.W / extent.H;
                if (Math.Abs(imgAspect - extentAspect) / extentAspect > aspectTol)
                {
                    warnings.Add($"Image aspect {imgAspect:F4} differs from the exported-extent aspect {extentAspect:F4}; the pixel mapping is likely off (annotations outside the crop or export padding). Check against a known grid intersection.");
                    if (status != "unverified") status = "aspect_mismatch";
                }
                mappingCheck["status"] = status;
                mappingCheck["imageAspect"] = Math.Round(imgAspect, 5);
                mappingCheck["extentAspect"] = Math.Round(extentAspect, 5);
                if (viewOutline != null)
                    mappingCheck["outlineTimesScaleMm"] = new JObject { ["w"] = VieMm(outlineW), ["h"] = VieMm(outlineH) };

                // ---- response ----
                bool axisAligned = Math.Abs(frame.Right.X - 1) < 1e-6 && Math.Abs(frame.Up.Y - 1) < 1e-6;
                if (!axisAligned)
                    warnings.Add($"View is rotated relative to project internal coordinates (viewRight=({frame.Right.X:F6},{frame.Right.Y:F6})). crop, region.outline, origin and grids are NOT internal XY: they are in the view-aligned frame x' = X*viewRight.x + Y*viewRight.y, y' = X*viewUp.x + Y*viewUp.y (mm, from the internal origin), so the mapping formula holds in that frame. cropWorldCorners gives the exported extent's corners in internal XY.");

                var outline = new JArray(outlineWorld.Select(p => { var f = frame.Of(p); return VieXY(f[0], f[1]); }));
                double[] originF;
                if (originWorld != null) originF = frame.Of(originWorld);
                else
                {
                    var rb = VieBoxOf(outlineWorld.Select(frame.Of));
                    originF = new[] { rb.MinX, rb.MaxY };
                    originLabel = "top-left of region bbox";
                }

                var grids = new JArray();
                foreach (JArray gd in gridsOut)
                {
                    double x0 = gd[1].Value<double>(), y0 = gd[2].Value<double>(), x1 = gd[3].Value<double>(), y1 = gd[4].Value<double>();
                    if (VieClip(extent, ref x0, ref y0, ref x1, ref y1))
                        grids.Add(new JObject { ["name"] = gd[0].Value<string>(), ["start"] = VieXY(x0, y0), ["end"] = VieXY(x1, y1) });
                }

                var cropCorners = new JArray(
                    new[] { new[] { extent.MinX, extent.MinY }, new[] { extent.MaxX, extent.MinY }, new[] { extent.MaxX, extent.MaxY }, new[] { extent.MinX, extent.MaxY } }
                    .Select(c => { XYZ w = frame.ToWorld(c[0], c[1]); return VieXY(w.X, w.Y); }));

                var result = new JObject
                {
                    ["imagePath"] = finalPath,
                    ["pixelWidth"] = imgW,
                    ["pixelHeight"] = imgH,
                    ["view"] = new JObject
                    {
                        ["id"] = view.Id.GetIdValue(),
                        ["name"] = view.Name,
                        ["levelName"] = view.GenLevel?.Name,
                        ["scale"] = view.Scale
                    },
                    ["crop"] = VieBoxJson(extent),
                    ["modelCrop"] = VieBoxJson(modelCrop),
                    ["cropWorldCorners"] = cropCorners,
                    ["frame"] = axisAligned ? "internal" : "view-aligned",
                    ["viewRight"] = new JObject { ["x"] = Math.Round(frame.Right.X, 6), ["y"] = Math.Round(frame.Right.Y, 6) },
                    ["viewUp"] = new JObject { ["x"] = Math.Round(frame.Up.X, 6), ["y"] = Math.Round(frame.Up.Y, 6) },
                    ["mapping"] = "px = (x - crop.minX) / (crop.maxX - crop.minX) * pixelWidth; py = (crop.maxY - y) / (crop.maxY - crop.minY) * pixelHeight",
                    ["mappingCheck"] = mappingCheck,
                    ["marginMm"] = marginMm,
                    ["region"] = new JObject { ["type"] = regionType, ["label"] = regionLabel, ["outline"] = outline },
                    ["origin"] = new JObject { ["x"] = VieMm(originF[0]), ["y"] = VieMm(originF[1]), ["label"] = originLabel },
                    ["grids"] = grids,
                    ["tempView"] = tempStrategy,
                    ["modelChanged"] = false,
                    ["warnings"] = warnings
                };
                return result;
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }

        private ViewPlan VieResolveView(Document doc, JObject parameters, JArray warnings)
        {
            ViewPlan view;
            JToken idTok = parameters["viewId"];
            string viewName = parameters["viewName"]?.Value<string>();
            if (idTok != null && idTok.Type != JTokenType.Null)
            {
                IdType id = idTok.Value<IdType>();
                view = doc.GetElement(id.ToElementId()) as ViewPlan;
                if (view == null) throw new Exception($"viewId {id} is not a plan view.");
            }
            else if (!string.IsNullOrWhiteSpace(viewName))
            {
                var matches = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.Name == viewName).ToList();
                if (matches.Count == 0) throw new Exception($"No plan view named '{viewName}'.");
                view = matches.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan) ?? matches[0];
                if (matches.Count > 1)
                    warnings.Add($"{matches.Count} plan views are named '{viewName}' (ids {string.Join(", ", matches.Select(v => v.Id.GetIdValue()))}); used {view.Id.GetIdValue()} ({view.ViewType}). Pass viewId to choose.");
            }
            else throw new Exception("viewName or viewId is required.");

            if (view.IsTemplate) throw new Exception("The given view is a view template.");
            if (view.ViewType != ViewType.FloorPlan && view.ViewType != ViewType.CeilingPlan
                && view.ViewType != ViewType.AreaPlan && view.ViewType != ViewType.EngineeringPlan)
                throw new Exception($"View type {view.ViewType} is not supported; use a floor, ceiling, structural or area plan.");
            return view;
        }

        private View VieMakeTempView(Document doc, ViewPlan view, JArray warnings, out string strategy)
        {
            // Prefer a dependent of the primary view (same annotations and visibility as what the user sees),
            // then fall back to independent duplicates. A new element avoids editing (and, in workshared
            // models, borrowing) the user's view; the whole group is rolled back anyway.
            View primary = view;
            ElementId primaryId = view.GetPrimaryViewId();
            if (primaryId != ElementId.InvalidElementId && doc.GetElement(primaryId) is View pv) primary = pv;

            var attempts = new List<Tuple<View, ViewDuplicateOption, string>>
            {
                Tuple.Create(primary, ViewDuplicateOption.AsDependent, "dependent"),
                Tuple.Create((View)view, ViewDuplicateOption.WithDetailing, "duplicate-with-detailing"),
                Tuple.Create((View)view, ViewDuplicateOption.Duplicate, "duplicate")
            };
            foreach (var a in attempts)
            {
                if (!a.Item1.CanViewBeDuplicated(a.Item2)) continue;
                try
                {
                    ElementId newId = a.Item1.Duplicate(a.Item2);
                    var v = doc.GetElement(newId) as View;
                    if (v != null)
                    {
                        strategy = a.Item3;
                        if (a.Item3 != "dependent")
                            warnings.Add($"Temporary view created as '{a.Item3}' (dependent duplicate unavailable); annotations may differ from the source view.");
                        return v;
                    }
                }
                catch (Exception ex) { warnings.Add($"Temporary {a.Item3} view failed: {ex.Message}"); }
            }
            throw new Exception("Could not create a temporary duplicate of the view for export.");
        }

        private List<XYZ> VieGridRegion(Document doc, JObject grids, VieFrame frame, JArray warnings,
            out string label, out XYZ originWorld, out string originLabel)
        {
            if (grids == null) throw new Exception("region.grids must be an object {x:[a,b], y:[c,d]}.");
            string[] xs = (grids["x"] as JArray)?.Select(t => t.Value<string>()).ToArray();
            string[] ys = (grids["y"] as JArray)?.Select(t => t.Value<string>()).ToArray();
            if (xs == null || ys == null || xs.Length != 2 || ys.Length != 2)
                throw new Exception("region.grids needs exactly two grid names in x and two in y.");

            var all = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            Func<string, Tuple<string, double[], double[]>> find = name =>
            {
                Grid g = all.FirstOrDefault(x => x.Name == name);
                if (g == null) throw new Exception($"Grid '{name}' not found.");
                Line l = g.Curve as Line;
                if (g.IsCurved || l == null) throw new Exception($"Grid '{name}' is not a straight grid.");
                double[] p = frame.Of(l.GetEndPoint(0)), q = frame.Of(l.GetEndPoint(1));
                return Tuple.Create(name, p, new[] { q[0] - p[0], q[1] - p[1] });
            };
            Func<Tuple<string, double[], double[]>, bool> runsVertical = g => Math.Abs(g.Item3[1]) > Math.Abs(g.Item3[0]);

            var X = xs.Select(find).ToArray();
            var Y = ys.Select(find).ToArray();
            if (X.All(runsVertical) && Y.All(g => !runsVertical(g))) { }
            else if (X.All(g => !runsVertical(g)) && Y.All(runsVertical))
            {
                var tmp = X; X = Y; Y = tmp;
                warnings.Add($"Grids given as x ({string.Join(",", xs)}) run horizontally in the view and y ({string.Join(",", ys)}) run vertically; swapped them.");
            }
            else
                throw new Exception($"Grid pairs are mixed: x needs two grids running vertically in the view and y two running horizontally (x={string.Join(",", xs)}, y={string.Join(",", ys)}).");

            Func<Tuple<string, double[], double[]>, Tuple<string, double[], double[]>, double[]> intersect = (a, b) =>
            {
                double den = a.Item3[0] * b.Item3[1] - a.Item3[1] * b.Item3[0];
                if (Math.Abs(den) < 1e-9) throw new Exception($"Grids '{a.Item1}' and '{b.Item1}' are parallel.");
                double dx = b.Item2[0] - a.Item2[0], dy = b.Item2[1] - a.Item2[1];
                double s = (dx * b.Item3[1] - dy * b.Item3[0]) / den;
                return new[] { a.Item2[0] + s * a.Item3[0], a.Item2[1] + s * a.Item3[1] };
            };

            var corners = new List<Tuple<double[], string>>
            {
                Tuple.Create(intersect(X[0], Y[0]), X[0].Item1 + "/" + Y[0].Item1),
                Tuple.Create(intersect(X[1], Y[0]), X[1].Item1 + "/" + Y[0].Item1),
                Tuple.Create(intersect(X[1], Y[1]), X[1].Item1 + "/" + Y[1].Item1),
                Tuple.Create(intersect(X[0], Y[1]), X[0].Item1 + "/" + Y[1].Item1)
            };
            var box = VieBoxOf(corners.Select(c => c.Item1));
            var tl = corners.OrderBy(c => (c.Item1[0] - box.MinX) + (box.MaxY - c.Item1[1])).First();
            originWorld = frame.ToWorld(tl.Item1[0], tl.Item1[1]);
            originLabel = tl.Item2;
            label = X[0].Item1 + "-" + X[1].Item1 + "/" + Y[0].Item1 + "-" + Y[1].Item1;
            return corners.Select(c => frame.ToWorld(c.Item1[0], c.Item1[1])).ToList();
        }

        private Room VieResolveRoom(Document doc, ViewPlan view, JObject region, string key, JArray warnings)
        {
            Room room;
            ElementId viewLevel = view.GenLevel?.Id ?? ElementId.InvalidElementId;
            if (key == "roomId")
            {
                IdType id = region["roomId"].Value<IdType>();
                room = doc.GetElement(id.ToElementId()) as Room;
                if (room == null) throw new Exception($"roomId {id} is not a Room.");
            }
            else
            {
                string number = region["roomNumber"].Value<string>();
                var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType().OfType<Room>().Where(r => r.Number == number && r.Area > 0).ToList();
                if (rooms.Count == 0) throw new Exception($"No placed, enclosed room numbered '{number}'.");
                var onLevel = rooms.Where(r => r.LevelId == viewLevel).ToList();
                room = onLevel.FirstOrDefault() ?? rooms[0];
                if ((onLevel.Count > 0 ? onLevel.Count : rooms.Count) > 1)
                    warnings.Add($"Several rooms are numbered '{number}'; used room {room.Id.GetIdValue()}. Pass roomId to choose.");
            }
            if (room.Area <= 0) throw new Exception($"Room {room.Id.GetIdValue()} is not placed or not enclosed (area = 0).");
            if (viewLevel != ElementId.InvalidElementId && room.LevelId != viewLevel)
                warnings.Add($"Room {room.Id.GetIdValue()} is on a different level than the view.");
            return room;
        }

        private static List<XYZ> VieSpatialOutline(SpatialElement se)
        {
            var loops = se.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (loops == null || loops.Count == 0) throw new Exception($"Element {se.Id.GetIdValue()} has no boundary.");
            List<XYZ> best = null;
            double bestArea = -1;
            foreach (var loop in loops)
            {
                var pts = new List<XYZ>();
                foreach (BoundarySegment seg in loop)
                {
                    IList<XYZ> tess = seg.GetCurve().Tessellate();
                    for (int i = 0; i < tess.Count - 1; i++) pts.Add(tess[i]);
                }
                if (pts.Count < 3) continue;
                double a = (pts.Max(p => p.X) - pts.Min(p => p.X)) * (pts.Max(p => p.Y) - pts.Min(p => p.Y));
                if (a > bestArea) { bestArea = a; best = pts; }
            }
            if (best == null) throw new Exception($"Element {se.Id.GetIdValue()} has no usable boundary loop.");
            return best;
        }

        private static XYZ VieHorizontalUnit(XYZ v)
        {
            var h = new XYZ(v.X, v.Y, 0);
            if (h.GetLength() < 1e-9) throw new Exception("View direction is not horizontal; only plan views are supported.");
            return h.Normalize();
        }

        private static VieBox VieBoxOf(IEnumerable<double[]> pts)
        {
            var list = pts.ToList();
            return new VieBox
            {
                MinX = list.Min(p => p[0]), MinY = list.Min(p => p[1]),
                MaxX = list.Max(p => p[0]), MaxY = list.Max(p => p[1])
            };
        }

        // Liang-Barsky segment clip against an axis-aligned box (mapping frame). Returns false if outside.
        private static bool VieClip(VieBox b, ref double x0, ref double y0, ref double x1, ref double y1)
        {
            double dx = x1 - x0, dy = y1 - y0, t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x0 - b.MinX, b.MaxX - x0, y0 - b.MinY, b.MaxY - y0 };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12) { if (q[i] < 0) return false; continue; }
                double r = q[i] / p[i];
                if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            double nx0 = x0 + t0 * dx, ny0 = y0 + t0 * dy, nx1 = x0 + t1 * dx, ny1 = y0 + t1 * dy;
            x0 = nx0; y0 = ny0; x1 = nx1; y1 = ny1;
            return true;
        }

        private static void VieReadPngSize(string path, out int w, out int h)
        {
            byte[] hdr = new byte[24];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(hdr, 0, 24) != 24 || hdr[0] != 0x89 || hdr[1] != 0x50 || hdr[12] != 0x49 || hdr[13] != 0x48)
                    throw new Exception("Exported file is not a PNG: " + path);
            }
            w = (hdr[16] << 24) | (hdr[17] << 16) | (hdr[18] << 8) | hdr[19];
            h = (hdr[20] << 24) | (hdr[21] << 16) | (hdr[22] << 8) | hdr[23];
        }

        private static string VieSanitize(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            string s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
            return s.Length == 0 ? "view" : s;
        }

        private static double VieMm(double ft) { return Math.Round(ft * VieMmPerFt, 1); }
        private static JObject VieXY(double xFt, double yFt) { return new JObject { ["x"] = VieMm(xFt), ["y"] = VieMm(yFt) }; }
        private static JObject VieBoxJson(VieBox b)
        {
            return new JObject { ["minX"] = VieMm(b.MinX), ["minY"] = VieMm(b.MinY), ["maxX"] = VieMm(b.MaxX), ["maxY"] = VieMm(b.MaxY) };
        }

        #endregion
    }
}
