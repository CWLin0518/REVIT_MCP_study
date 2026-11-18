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
    /// <summary>
    /// RFA 族群編輯工具第四批：符號線（rfa_create_symbolic_line）與另存族群（rfa_save_as）。
    ///
    /// 符號線是族群裡的 2D 表現（立面開門方向線、平面門弧線），只在與其工作平面平行的視圖顯示。
    /// 它不是草圖元素，端點可以用一般的對齊／尺寸約束到參考平面，所以和參考線共用 RfaConstrainCurve。
    /// 既有符號線的約束無法經由 API 改掛到別的平面；要換約束對象就是刪掉舊線、用這支工具重畫。
    /// </summary>
    public partial class CommandExecutor
    {
        #region rfa_create_symbolic_line

        /// <summary>依名稱找族群類別底下的子類別線型；名稱可帶 " [projection]" / " [cut]" 後綴</summary>
        private static GraphicsStyle RfaFindSubcategoryStyle(Document doc, string name)
        {
            string n = name.Trim();
            GraphicsStyleType kind = GraphicsStyleType.Projection;
            int br = n.LastIndexOf('[');
            if (br > 0 && n.EndsWith("]"))
            {
                string suffix = n.Substring(br + 1, n.Length - br - 2).Trim().ToLowerInvariant();
                if (suffix == "cut") kind = GraphicsStyleType.Cut;
                else if (suffix != "projection") throw new Exception($"子類別後綴只接受 [projection] 或 [cut]，收到: [{suffix}]");
                n = n.Substring(0, br).Trim();
            }

            Category famCat = doc.OwnerFamily?.FamilyCategory ?? throw new Exception("讀不到族群類別");
            var subs = famCat.SubCategories.Cast<Category>().ToList();
            Category sub = subs.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
            if (sub == null)
                throw new Exception($"族群類別「{famCat.Name}」沒有子類別「{n}」。可用: {string.Join(", ", subs.Select(c => c.Name).OrderBy(x => x))}");
            return sub.GetGraphicsStyle(kind)
                   ?? throw new Exception($"子類別「{n}」沒有 {(kind == GraphicsStyleType.Cut ? "cut" : "projection")} 線型");
        }

        private object RfaCreateSymbolicLine(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;
            var warnings = new List<string>();
            double tol = RfaMm(0.5);

            string hostName = parameters["hostPlane"]?.Value<string>()
                              ?? throw new Exception("請提供 hostPlane（符號線所在的參考平面；符號線只在與它平行的視圖顯示）");
            ReferencePlane hostRp = RfaFindRefPlane(doc, hostName);
            XYZ hn = hostRp.Normal.Normalize();
            XYZ ho = hostRp.GetPlane().Origin;

            if (parameters["polylines"] != null && parameters["polylines"].Type != JTokenType.Null)
                return RfaCreateSymbolicPolylines(doc, parameters, hostName, hostRp, hn, ho, tol);

            Curve curve;
            bool isArc;
            if (parameters["line"] is JObject ln)
            {
                curve = Line.CreateBound(RfaReadXyzMm(ln["startMm"], "line.startMm"), RfaReadXyzMm(ln["endMm"], "line.endMm"));
                isArc = false;
            }
            else if (parameters["arc"] is JObject ar)
            {
                curve = Arc.Create(RfaReadXyzMm(ar["startMm"], "arc.startMm"), RfaReadXyzMm(ar["endMm"], "arc.endMm"), RfaReadXyzMm(ar["midMm"], "arc.midMm"));
                isArc = true;
            }
            else throw new Exception("請提供 line {startMm, endMm} 或 arc {startMm, midMm, endMm}");

            foreach (XYZ p in new[] { curve.GetEndPoint(0), curve.Evaluate(0.5, true), curve.GetEndPoint(1) })
                if (Math.Abs((p - ho).DotProduct(hn)) > tol)
                    throw new Exception($"符號線不在 hostPlane「{hostName}」上（點 {RfaToMm(p.X)},{RfaToMm(p.Y)},{RfaToMm(p.Z)} 偏離 {RfaToMm((p - ho).DotProduct(hn))} mm）");

            string subName = parameters["subcategory"]?.Value<string>();
            GraphicsStyle style = string.IsNullOrWhiteSpace(subName) ? null : RfaFindSubcategoryStyle(doc, subName);

            var results = new List<object>();
            SymbolicCurve sc;
            using (var t = new Transaction(doc, "MCP: rfa_create_symbolic_line"))
            {
                // Error（例如約束過度）時靜默復原，不跳 Revit 對話框
                var failures = RfaUseRollbackOnError(t);
                t.Start();

                SketchPlane sp = SketchPlane.Create(doc, hostRp.Id);
                sc = doc.FamilyCreate.NewSymbolicCurve(curve, sp) ?? throw new Exception("建立符號線失敗（Revit 回傳 null）");
                if (style != null) sc.Subcategory = style;
                doc.Regenerate();

                RfaConstrainCurve(doc, fm, sc, parameters["constraints"] as JArray, "符號線", results, warnings);

                if (t.Commit() != TransactionStatus.Committed)
                    throw new Exception($"Revit 拒絕了符號線或其約束（交易被復原，可能是過度約束）{RfaFailureText(failures)}");
            }

            Curve fin = sc.GeometryCurve;
            return new
            {
                id = sc.Id.GetIdValue(),
                kind = isArc ? "arc" : "line",
                hostPlane = hostName,
                subcategory = sc.Subcategory?.Name,
                startMm = RfaPt(fin.GetEndPoint(0)),
                endMm = RfaPt(fin.GetEndPoint(1)),
                lengthMm = RfaToMm(fin.Length),
                constraints = results,
                warnings,
            };
        }

        /// <summary>批次模式單次呼叫允許的線段上限（含 closed 補上的收尾段、含被略過的短段）</summary>
        private const int RfaSymbolicPolylineMaxSegments = 2000;

        /// <summary>
        /// rfa_create_symbolic_line 的批次模式（polylines）：多條折線一次畫完、同一個交易。
        /// 每對相鄰點成為一條直線符號線；closed 時補上最後一點回到第一點的收尾段。
        /// 長度小於 max(0.5 mm, Revit ShortCurveTolerance) 的線段略過並計數。
        /// 任何一段失敗即整批復原，錯誤訊息帶出 polyline / segment 索引（皆由 0 起算）。
        /// 批次模式不支援 constraints（家具平面輪廓這類線不需要跟參數走）。
        /// </summary>
        private object RfaCreateSymbolicPolylines(Document doc, JObject parameters, string hostName,
            ReferencePlane hostRp, XYZ hn, XYZ ho, double tol)
        {
            foreach (string k in new[] { "line", "arc", "constraints" })
                if (parameters[k] != null && parameters[k].Type != JTokenType.Null)
                    throw new Exception($"使用 polylines 批次模式時不可同時提供 {k}（批次模式只畫直線段、不加約束）");

            var polys = parameters["polylines"] as JArray;
            if (polys == null || polys.Count == 0)
                throw new Exception("polylines 需要非空陣列：[{ pointsMm: [[x,y,z], ...], closed?: boolean }, ...]");

            double minLen = Math.Max(RfaMm(0.5), doc.Application.ShortCurveTolerance);

            // 先完整驗證並組好所有線段，再開交易；驗證失敗不會留下任何東西
            var segs = new List<(int poly, int seg, Line line)>();
            var skippedList = new List<object>();
            var polyClosed = new bool[polys.Count];
            var polySkipped = new int[polys.Count];
            int totalSegments = 0;

            for (int i = 0; i < polys.Count; i++)
            {
                var po = polys[i] as JObject ?? throw new Exception($"polylines[{i}] 必須是物件 {{ pointsMm, closed? }}");
                var ptsArr = po["pointsMm"] as JArray;
                if (ptsArr == null || ptsArr.Count < 2)
                    throw new Exception($"polylines[{i}].pointsMm 至少需要 2 個點 [x, y, z]（mm）");
                bool closed = po["closed"]?.Value<bool>() ?? false;
                polyClosed[i] = closed;

                var pts = new List<XYZ>(ptsArr.Count);
                for (int j = 0; j < ptsArr.Count; j++)
                {
                    XYZ p = RfaReadXyzMm(ptsArr[j], $"polylines[{i}].pointsMm[{j}]");
                    double off = (p - ho).DotProduct(hn);
                    if (Math.Abs(off) > tol)
                        throw new Exception($"polylines[{i}].pointsMm[{j}] 不在 hostPlane「{hostName}」上（偏離 {RfaToMm(off)} mm）");
                    pts.Add(p);
                }

                int segCount = closed && pts.Count >= 3 ? pts.Count : pts.Count - 1;
                totalSegments += segCount;
                if (totalSegments > RfaSymbolicPolylineMaxSegments)
                    throw new Exception($"線段總數超過上限 {RfaSymbolicPolylineMaxSegments}（算到 polylines[{i}] 已達 {totalSegments} 段）。請拆成多次呼叫");

                for (int s = 0; s < segCount; s++)
                {
                    XYZ a = pts[s], b = pts[(s + 1) % pts.Count];
                    double len = a.DistanceTo(b);
                    if (len < minLen)
                    {
                        polySkipped[i]++;
                        if (skippedList.Count < 50)
                            skippedList.Add(new { polyline = i, segment = s, lengthMm = RfaToMm(len) });
                        continue;
                    }
                    segs.Add((i, s, Line.CreateBound(a, b)));
                }
            }

            if (segs.Count == 0)
                throw new Exception($"所有線段都短於 {RfaToMm(minLen)} mm 而被略過，沒有可建立的符號線");

            string subName = parameters["subcategory"]?.Value<string>();
            GraphicsStyle style = string.IsNullOrWhiteSpace(subName) ? null : RfaFindSubcategoryStyle(doc, subName);

            var created = new List<(int poly, SymbolicCurve sc)>(segs.Count);
            using (var t = new Transaction(doc, "MCP: rfa_create_symbolic_line (polylines)"))
            {
                var failures = RfaUseRollbackOnError(t);
                t.Start();

                SketchPlane sp = SketchPlane.Create(doc, hostRp.Id);
                foreach (var (poly, seg, line) in segs)
                {
                    SymbolicCurve sc;
                    try
                    {
                        sc = doc.FamilyCreate.NewSymbolicCurve(line, sp);
                        if (sc == null) throw new Exception("Revit 回傳 null");
                        if (style != null) sc.Subcategory = style;
                    }
                    catch (Exception ex)
                    {
                        t.RollBack();
                        throw new Exception($"polylines[{poly}] 第 {seg} 段建立失敗，整批已復原：{ex.Message}");
                    }
                    created.Add((poly, sc));
                }

                if (t.Commit() != TransactionStatus.Committed)
                    throw new Exception($"Revit 拒絕了這批符號線（整批交易被復原）{RfaFailureText(failures)}");
            }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var idsPerPoly = new List<IdType>[polys.Count];
            for (int i = 0; i < polys.Count; i++) idsPerPoly[i] = new List<IdType>();
            foreach (var (poly, sc) in created)
            {
                idsPerPoly[poly].Add(sc.Id.GetIdValue());
                Curve g = sc.GeometryCurve;
                foreach (XYZ p in new[] { g.GetEndPoint(0), g.GetEndPoint(1) })
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
                }
            }

            int skippedTotal = polySkipped.Sum();
            return new
            {
                kind = "polylines",
                hostPlane = hostName,
                subcategory = created[0].sc.Subcategory?.Name,
                polylineCount = polys.Count,
                totalCreated = created.Count,
                skipped = skippedTotal,
                skipThresholdMm = RfaToMm(minLen),
                skippedSegments = skippedList,
                skippedSegmentsTruncated = skippedTotal > skippedList.Count,
                polylines = Enumerable.Range(0, polys.Count).Select(i => new
                {
                    index = i,
                    closed = polyClosed[i],
                    created = idsPerPoly[i].Count,
                    skipped = polySkipped[i],
                    ids = idsPerPoly[i],
                }).ToList(),
                boundingBoxMm = new
                {
                    min = new[] { RfaToMm(minX), RfaToMm(minY), RfaToMm(minZ) },
                    max = new[] { RfaToMm(maxX), RfaToMm(maxY), RfaToMm(maxZ) },
                },
            };
        }

        #endregion

        #region rfa_save_as

        /// <summary>
        /// 驗證並正規化要寫入的 .rfa 路徑：必須是完整路徑；沒有副檔名時補 .rfa，其他副檔名拒絕；
        /// 資料夾必須已存在（不自動建立）。不檢查檔案是否已存在，由呼叫端依 overwrite 決定。
        /// rfa_save_as 與 rfa_new_family 共用。
        /// </summary>
        private static string RfaResolveRfaSavePath(string raw, string paramName)
        {
            string path = raw?.Trim();
            if (string.IsNullOrEmpty(path)) throw new Exception($"請提供 {paramName}（.rfa 的完整路徑）");
            if (!Path.IsPathRooted(path)) throw new Exception($"{paramName} 必須是完整路徑，收到: {path}");
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) path += ".rfa";
            else if (!ext.Equals(".rfa", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"副檔名必須是 .rfa，收到: {ext}");
            path = Path.GetFullPath(path);

            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                throw new Exception($"資料夾不存在: {dir}（工具不會自動建立資料夾）");
            return path;
        }

        private object RfaSaveAs(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();

            string path = RfaResolveRfaSavePath(parameters["filePath"]?.Value<string>(), "filePath");

            bool overwrite = parameters["overwrite"]?.Value<bool>() ?? false;
            bool existed = File.Exists(path);
            if (existed && !overwrite)
                throw new Exception($"檔案已存在: {path}。確定要覆蓋請傳 overwrite: true，或換一個檔名");
            if (doc.IsModifiable)
                throw new Exception("族群文件有尚未結束的交易，無法存檔");

            bool samePath = !string.IsNullOrEmpty(doc.PathName) &&
                            string.Equals(Path.GetFullPath(doc.PathName), path, StringComparison.OrdinalIgnoreCase);
            if (samePath)
                doc.Save();
            else
                doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = overwrite });

            var info = new FileInfo(path);
            return new
            {
                path = doc.PathName,
                title = doc.Title,
                sizeBytes = info.Exists ? info.Length : (long?)null,
                overwroteExisting = existed,
                savedInPlace = samePath,
                note = "作用中的族群文件現在指向這個檔案；之後在 Revit 按儲存會寫回這裡。",
            };
        }

        #endregion
    }
}
