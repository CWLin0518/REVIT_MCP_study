#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using WpfImaging = System.Windows.Media.Imaging;

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

            string annotationMode = parameters["annotations"]?.Value<string>() ?? "keep";
            if (annotationMode != "keep" && annotationMode != "hideOutsideRegion")
                throw new Exception("annotations must be 'keep' or 'hideOutsideRegion'.");

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
            //
            // Annotations (grid heads, dimension strings, tags) are not clipped by the model crop, so the
            // exported image usually covers MORE than the crop box. Live testing showed that View.Outline
            // x scale is then exactly the extent the PNG covers. Instead of handing the caller a huge
            // extent in which the requested region is a few hundred pixels wide, the image is re-exported
            // so that the model-crop part gets ~pixelWidth px and then cropped to that part's pixel
            // rectangle. The returned crop is the pixel-snapped model crop, so the mapping stays exact.
            string tmpDir = Path.Combine(outputDir, ".export_tmp_" + Guid.NewGuid().ToString("N"));
            string exportedTmpFile = null;
            string tempStrategy = null;
            VieFrame frame = srcFrame;
            VieBox modelCrop = null, extent = null, exportedExtent = null;
            var gridData = new List<Tuple<string, double[], double[]>>();
            JObject mappingCheck = new JObject();
            BoundingBoxUV viewOutline = null;
            int viewScale = view.Scale;
            string status = "unverified";
            int imgW = 0, imgH = 0, exportWidth = pixelWidth;
            int[] cropRect = null; // x, y, w, h of the model crop inside the exported PNG

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
                            ViewCropRegionShapeManager mgr = null;
                            try
                            {
                                mgr = exportView.GetCropRegionShapeManager();
                                if (mgr != null && mgr.ShapeSet) mgr.RemoveCropRegionShape();
                            }
                            catch (Exception ex) { warnings.Add("Could not reset a non-rectangular crop shape: " + ex.Message); }

                            Parameter ac = exportView.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                            if (annotationMode == "hideOutsideRegion")
                            {
                                if (ac != null && !ac.IsReadOnly) ac.Set(1);
                                else warnings.Add("Annotation crop could not be turned on; annotations outside the region are kept.");
                                VieMinimizeAnnotationCrop(mgr, warnings);
                            }
                            else if (ac != null && !ac.IsReadOnly) ac.Set(0);

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

                        // Grids visible in the temporary view.
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

                        // ---- first export + mapping verification ----
                        exportedTmpFile = VieExportPng(doc, exportView.Id, Path.Combine(tmpDir, "a"), pixelWidth);
                        VieReadPngSize(exportedTmpFile, out imgW, out imgH);

                        string outlineNote;
                        status = VieClassifyOutline(viewOutline, viewScale, modelCrop, out extent, out outlineNote);
                        bool aspectOk = VieAspectOk(imgW, imgH, extent);
                        if (status == "outline_extends_crop" && !aspectOk)
                        {
                            // Outline contains the crop but the PNG does not have its proportions: the
                            // outline is not what was exported, so neither box can be trusted.
                            status = "unverified";
                            extent = modelCrop;
                            aspectOk = VieAspectOk(imgW, imgH, extent);
                        }
                        if (status == "unverified" && outlineNote != null) warnings.Add(outlineNote);
                        if (!aspectOk)
                        {
                            warnings.Add($"Image aspect {(double)imgW / imgH:F4} differs from the exported-extent aspect {extent.W / extent.H:F4}; the pixel mapping is likely off (annotations outside the crop or export padding). Check against a known grid intersection.");
                            if (status != "unverified") status = "aspect_mismatch";
                        }

                        // ---- annotations widened the image: re-export larger so the crop part gets ~pixelWidth px ----
                        if (status == "outline_extends_crop")
                        {
                            int required = (int)Math.Ceiling(pixelWidth * extent.W / modelCrop.W);
                            if (required > imgW + 2)
                            {
                                exportWidth = Math.Min(required, VieMaxPixels);
                                if (exportWidth < required)
                                    warnings.Add($"Annotations around the region widen the export to {VieMm(extent.W)} mm; the {VieMaxPixels} px export cap leaves the region about {(int)Math.Floor(VieMaxPixels * modelCrop.W / extent.W)} px wide instead of {pixelWidth}. Use annotations='hideOutsideRegion' or a smaller pixelWidth.");
                                string hi = VieExportPng(doc, exportView.Id, Path.Combine(tmpDir, "b"), exportWidth);
                                int hw, hh;
                                VieReadPngSize(hi, out hw, out hh);
                                if (VieAspectOk(hw, hh, extent))
                                {
                                    exportedTmpFile = hi;
                                    imgW = hw; imgH = hh;
                                }
                                else
                                {
                                    warnings.Add($"The {exportWidth} px re-export came back {hw} x {hh}, which does not match the exported extent; the {imgW} px export was cropped instead (the region has fewer pixels than requested).");
                                    exportWidth = pixelWidth;
                                }
                            }
                            cropRect = VieCropRect(extent, modelCrop, imgW, imgH);
                            if (cropRect == null)
                                warnings.Add("The model crop maps to less than one pixel of the exported image; returned the uncropped image.");
                        }
                    }
                    finally
                    {
                        if (tg.GetStatus() == TransactionStatus.Started) tg.RollBack();
                    }
                }

                // ---- final file (cropped to the model crop when annotations widened the export) ----
                string baseName = VieSanitize(view.Name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string finalPath = Path.Combine(outputDir, baseName + ".png");
                for (int i = 1; File.Exists(finalPath); i++) finalPath = Path.Combine(outputDir, baseName + "_" + i + ".png");

                exportedExtent = extent;
                int exportedW = imgW, exportedH = imgH;
                WpfImaging.BitmapSource finalBmp;
                if (cropRect != null)
                {
                    finalBmp = VieCropPngTo(exportedTmpFile, finalPath, cropRect);
                    double sx = imgW / extent.W, sy = imgH / extent.H;
                    // pixel-snapped model crop: exactly the area the cropped PNG covers
                    extent = new VieBox
                    {
                        MinX = exportedExtent.MinX + cropRect[0] / sx,
                        MaxX = exportedExtent.MinX + (cropRect[0] + cropRect[2]) / sx,
                        MaxY = exportedExtent.MaxY - cropRect[1] / sy,
                        MinY = exportedExtent.MaxY - (cropRect[1] + cropRect[3]) / sy
                    };
                    imgW = cropRect[2]; imgH = cropRect[3];
                    status = "cropped_to_model_crop";
                    mappingCheck["exportPixelSize"] = new JObject { ["w"] = exportedW, ["h"] = exportedH };
                    mappingCheck["cropPixelRect"] = new JObject { ["x"] = cropRect[0], ["y"] = cropRect[1], ["w"] = cropRect[2], ["h"] = cropRect[3] };
                }
                else
                {
                    File.Move(exportedTmpFile, finalPath);
                    finalBmp = VieLoadPng(finalPath);
                    if (status == "outline_extends_crop")
                        warnings.Add("Annotations extend beyond the model crop, so the image covers more than the crop. 'crop' is the larger exported extent derived from View.Outline (paper outline x scale); 'modelCrop' is the crop box that was set.");
                }

                double imgAspect = (double)imgW / imgH;
                double extentAspect = extent.W / extent.H;
                mappingCheck["status"] = status;
                mappingCheck["imageAspect"] = Math.Round(imgAspect, 5);
                mappingCheck["extentAspect"] = Math.Round(extentAspect, 5);
                if (viewOutline != null)
                    mappingCheck["outlineTimesScaleMm"] = new JObject
                    {
                        ["w"] = VieMm((viewOutline.Max.U - viewOutline.Min.U) * viewScale),
                        ["h"] = VieMm((viewOutline.Max.V - viewOutline.Min.V) * viewScale)
                    };

                // ---- response ----
                bool axisAligned = Math.Abs(frame.Right.X - 1) < 1e-6 && Math.Abs(frame.Up.Y - 1) < 1e-6;
                if (!axisAligned)
                    warnings.Add($"View is rotated relative to project internal coordinates (viewRight=({frame.Right.X:F6},{frame.Right.Y:F6})). crop, region.outline, origin and grids are NOT internal XY: they are in the view-aligned frame x' = X*viewRight.x + Y*viewRight.y, y' = X*viewUp.x + Y*viewUp.y (mm, from the internal origin), so the mapping formula holds in that frame. cropWorldCorners gives the exported extent's corners in internal XY.");

                var outline = new JArray(outlineWorld.Select(p => { var f = frame.Of(p); return VieXY(f[0], f[1]); }));
                double[] originF;
                string originLabelOut = originLabel;
                if (originWorld != null) originF = frame.Of(originWorld);
                else
                {
                    var rb = VieBoxOf(outlineWorld.Select(frame.Of));
                    originF = new[] { rb.MinX, rb.MaxY };
                    originLabelOut = "top-left of region bbox";
                }

                var grids = new JArray();
                var clippedGrids = new List<Tuple<string, double[], double[]>>();
                foreach (var gd in gridData)
                {
                    double x0 = gd.Item2[0], y0 = gd.Item2[1], x1 = gd.Item3[0], y1 = gd.Item3[1];
                    if (VieClip(extent, ref x0, ref y0, ref x1, ref y1))
                    {
                        grids.Add(new JObject { ["name"] = gd.Item1, ["start"] = VieXY(x0, y0), ["end"] = VieXY(x1, y1) });
                        clippedGrids.Add(Tuple.Create(gd.Item1, new[] { x0, y0 }, new[] { x1, y1 }));
                    }
                }

                // Pixel-level sanity check: is a grid line where the mapping says it is?
                try
                {
                    JObject gpc = VieGridPixelCheck(finalBmp, extent, clippedGrids);
                    if (gpc != null)
                    {
                        mappingCheck["gridPixelCheck"] = gpc;
                        if (gpc["result"]?.Value<string>() == "offset")
                            warnings.Add($"Grid '{gpc["grid"]}' was found {gpc["offsetPx"]} px from where the mapping puts it; check the mapping against a known grid intersection.");
                    }
                }
                catch (Exception ex) { mappingCheck["gridPixelCheck"] = new JObject { ["result"] = "error", ["message"] = ex.Message }; }

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
                    ["exportedExtent"] = VieBoxJson(exportedExtent),
                    ["cropWorldCorners"] = cropCorners,
                    ["frame"] = axisAligned ? "internal" : "view-aligned",
                    ["viewRight"] = new JObject { ["x"] = Math.Round(frame.Right.X, 6), ["y"] = Math.Round(frame.Right.Y, 6) },
                    ["viewUp"] = new JObject { ["x"] = Math.Round(frame.Up.X, 6), ["y"] = Math.Round(frame.Up.Y, 6) },
                    ["mapping"] = "px = (x - crop.minX) / (crop.maxX - crop.minX) * pixelWidth; py = (crop.maxY - y) / (crop.maxY - crop.minY) * pixelHeight",
                    ["mappingCheck"] = mappingCheck,
                    ["marginMm"] = marginMm,
                    ["annotations"] = annotationMode,
                    ["region"] = new JObject { ["type"] = regionType, ["label"] = regionLabel, ["outline"] = outline },
                    ["origin"] = new JObject { ["x"] = VieMm(originF[0]), ["y"] = VieMm(originF[1]), ["label"] = originLabelOut },
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

        private const int VieMaxPixels = 15000;

        private static string VieExportPng(Document doc, ElementId viewId, string dir, int pixelWidth)
        {
            Directory.CreateDirectory(dir);
            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(dir, "export"),
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = pixelWidth,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150
            };
            opts.SetViewsAndSheets(new List<ElementId> { viewId });
            doc.ExportImage(opts);

            string file = Directory.GetFiles(dir)
                .Where(f => string.Equals(Path.GetExtension(f), ".png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (file == null) throw new Exception("Revit ExportImage finished but no PNG was written.");
            return file;
        }

        // Compares View.Outline x scale (the paper extent of everything drawn, annotations included)
        // with the model crop. Returns outline_matches_crop, outline_extends_crop or unverified; extent is
        // the box the image is expected to cover.
        private static string VieClassifyOutline(BoundingBoxUV outline, int scale, VieBox modelCrop, out VieBox extent, out string note)
        {
            extent = modelCrop;
            note = null;
            if (outline == null)
            {
                note = "View.Outline was unavailable; the mapping assumes the image edges are the crop edges.";
                return "unverified";
            }
            var ol = new VieBox
            {
                MinX = outline.Min.U * scale, MinY = outline.Min.V * scale,
                MaxX = outline.Max.U * scale, MaxY = outline.Max.V * scale
            };
            double tolFt = Math.Max(0.01, 0.002 * Math.Max(modelCrop.W, modelCrop.H));
            if (Math.Abs(ol.W - modelCrop.W) <= tolFt && Math.Abs(ol.H - modelCrop.H) <= tolFt)
                return "outline_matches_crop";
            bool contains = ol.MinX <= modelCrop.MinX + tolFt && ol.MinY <= modelCrop.MinY + tolFt
                            && ol.MaxX >= modelCrop.MaxX - tolFt && ol.MaxY >= modelCrop.MaxY - tolFt;
            if (contains)
            {
                extent = ol;
                return "outline_extends_crop";
            }
            note = $"Mapping unverified: View.Outline x scale = {VieMm(ol.W)} x {VieMm(ol.H)} mm, crop = {VieMm(modelCrop.W)} x {VieMm(modelCrop.H)} mm. The mapping assumes the image edges are the crop edges; check against a known grid intersection.";
            return "unverified";
        }

        private static bool VieAspectOk(int w, int h, VieBox box)
        {
            double imgAspect = (double)w / h, boxAspect = box.W / box.H;
            double tol = 0.005 + 2.0 / Math.Min(w, h);
            return Math.Abs(imgAspect - boxAspect) / boxAspect <= tol;
        }

        // Pixel rectangle {x, y, w, h} of inner inside an image of w x h px covering outer; null if degenerate.
        private static int[] VieCropRect(VieBox outer, VieBox inner, int w, int h)
        {
            double sx = w / outer.W, sy = h / outer.H;
            int x0 = Math.Max(0, Math.Min(w, (int)Math.Round((inner.MinX - outer.MinX) * sx)));
            int x1 = Math.Max(0, Math.Min(w, (int)Math.Round((inner.MaxX - outer.MinX) * sx)));
            int y0 = Math.Max(0, Math.Min(h, (int)Math.Round((outer.MaxY - inner.MaxY) * sy)));
            int y1 = Math.Max(0, Math.Min(h, (int)Math.Round((outer.MaxY - inner.MinY) * sy)));
            if (x1 - x0 < 1 || y1 - y0 < 1) return null;
            return new[] { x0, y0, x1 - x0, y1 - y0 };
        }

        // Annotation crop as tight as Revit allows (used by annotations = "hideOutsideRegion").
        private static void VieMinimizeAnnotationCrop(ViewCropRegionShapeManager mgr, JArray warnings)
        {
            if (mgr == null) { warnings.Add("Annotation crop offsets could not be read; Revit's default offset is used."); return; }
            foreach (double off in new[] { 1.0 / 304.8, 1.0 / 96.0, 1.0 / 48.0 })
            {
                try
                {
                    mgr.LeftAnnotationCropOffset = off;
                    mgr.RightAnnotationCropOffset = off;
                    mgr.TopAnnotationCropOffset = off;
                    mgr.BottomAnnotationCropOffset = off;
                    return;
                }
                catch { }
            }
            warnings.Add("Annotation crop offsets could not be reduced; Revit's default offset is used (the image is still cropped to the model crop).");
        }

        private static WpfImaging.BitmapSource VieLoadPng(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var dec = new WpfImaging.PngBitmapDecoder(fs, WpfImaging.BitmapCreateOptions.PreservePixelFormat | WpfImaging.BitmapCreateOptions.IgnoreColorProfile,
                    WpfImaging.BitmapCacheOption.OnLoad);
                WpfImaging.BitmapSource bmp = dec.Frames[0];
                bmp.Freeze();
                return bmp;
            }
        }

        private static WpfImaging.BitmapSource VieCropPngTo(string srcPath, string dstPath, int[] r)
        {
            WpfImaging.BitmapSource src = VieLoadPng(srcPath);
            var cropped = new WpfImaging.CroppedBitmap(src, new System.Windows.Int32Rect(r[0], r[1], r[2], r[3]));
            cropped.Freeze();
            var enc = new WpfImaging.PngBitmapEncoder();
            enc.Frames.Add(WpfImaging.BitmapFrame.Create(cropped));
            using (var fs = File.Create(dstPath)) enc.Save(fs);
            return cropped;
        }

        // Looks for the grid line nearest the image centre (vertical or horizontal) within +-6 px of where
        // the mapping puts it, by counting non-background pixels per column/row in that strip.
        // Informational: result = "ok" | "offset" | "inconclusive"; null when no suitable grid is inside.
        private static JObject VieGridPixelCheck(WpfImaging.BitmapSource bmp, VieBox crop, List<Tuple<string, double[], double[]>> grids)
        {
            int W = bmp.PixelWidth, H = bmp.PixelHeight;
            const int win = 6;
            Tuple<string, bool, double> best = null; // name, vertical, expected px (column or row)
            double bestDist = double.MaxValue;
            foreach (var g in grids)
            {
                double dx = g.Item3[0] - g.Item2[0], dy = g.Item3[1] - g.Item2[1];
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;
                bool vertical = Math.Abs(dx) <= 1e-4 * len, horizontal = Math.Abs(dy) <= 1e-4 * len;
                if (!vertical && !horizontal) continue;
                double e = vertical ? (g.Item2[0] - crop.MinX) / crop.W * W : (crop.MaxY - g.Item2[1]) / crop.H * H;
                int n = vertical ? W : H;
                if (e < win + 2 || e > n - win - 2) continue;
                double d = Math.Abs(e / n - 0.5);
                if (d < bestDist) { bestDist = d; best = Tuple.Create(g.Item1, vertical, e); }
            }
            if (best == null) return null;

            var px = new WpfImaging.FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            bool vert = best.Item2;
            int center = (int)Math.Floor(best.Item3);
            int lo = Math.Max(0, center - win), hi = Math.Min((vert ? W : H) - 1, center + win);
            int span = hi - lo + 1, length = vert ? H : W;
            var rect = vert ? new System.Windows.Int32Rect(lo, 0, span, H) : new System.Windows.Int32Rect(0, lo, W, span);
            int stride = rect.Width * 4;
            var buf = new int[rect.Width * rect.Height];
            px.CopyPixels(rect, buf, stride, 0);

            int bg = buf.GroupBy(v => v).OrderByDescending(gr => gr.Count()).First().Key;
            var counts = new int[span];
            for (int r = 0; r < rect.Height; r++)
                for (int c = 0; c < rect.Width; c++)
                    if (buf[r * rect.Width + c] != bg) counts[vert ? c : r]++;
            int bi = 0;
            for (int i = 1; i < span; i++) if (counts[i] > counts[bi]) bi = i;
            var sorted = counts.OrderBy(v => v).ToArray();
            int median = sorted[span / 2];
            double offset = Math.Round(lo + bi + 0.5 - best.Item3, 1);
            bool prominent = counts[bi] >= 0.2 * length && counts[bi] >= 1.5 * Math.Max(1, median);
            return new JObject
            {
                ["grid"] = best.Item1,
                ["axis"] = vert ? "column" : "row",
                ["expectedPx"] = Math.Round(best.Item3, 1),
                ["observedPx"] = prominent ? (JToken)(lo + bi + 0.5) : JValue.CreateNull(),
                ["offsetPx"] = prominent ? (JToken)offset : JValue.CreateNull(),
                ["result"] = !prominent ? "inconclusive" : (Math.Abs(offset) <= 3 ? "ok" : "offset")
            };
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
