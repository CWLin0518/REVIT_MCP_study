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
    /// <summary>
    /// RFA 族群編輯工具第十批：rfa_create_dimension 的角度模式（angular = true）。
    ///
    /// 兩個參考（參考平面名稱或直線參考線 id，合計恰好兩個）之間標角度，可綁「角度」族群參數，
    /// 讓擺動／旋轉的幾何參數化。與線性模式同一個工具、同一個 case，由 RfaCreateDimension 分流進來。
    ///
    /// 扇區規則（決定量的是 θ 還是 180°−θ）：
    ///   • 參考線方向 = 起點 → 終點；
    ///   • 參考平面方向 = 在視圖平面上的跡線方向，正向取「自由端 → 泡泡端」的投影；
    ///     若兩端連線在視圖平面上沒有分量，改取視圖的上方向，再不行取右方向。
    ///   • 標的是「第一個參考的方向」與「第二個參考的方向」之間的夾角（0°–180°）；
    ///     supplementary = true 時改量第一方向與第二方向反向之間的夾角（相鄰扇區，180°−θ）。
    ///   • 參考順序：referencePlanes 依序在前，referenceLineIds 依序在後。
    /// </summary>
    public partial class CommandExecutor
    {
        #region rfa_create_dimension（角度）

        /// <summary>角度標註的一個參考：參考平面或直線參考線</summary>
        private sealed class RfaAngularRef
        {
            public string Kind;            // "referencePlane" | "referenceLine"
            public string Name;            // 參考平面名稱
            public IdType Id;              // 參考線 id
            public ReferencePlane Plane;
            public CurveElement Curve;
            public Reference Ref;
        }

        private const double RfaAngleTolRad = 0.01 * Math.PI / 180.0;

        /// <summary>
        /// 參考了 refIds 中「每一個」元素的帶標籤尺寸，回傳「#id 標籤」描述清單。
        /// 用來在角度自檢失敗時分辨原因：同一對參考上已有帶標籤的尺寸 → 過度約束。
        /// </summary>
        private static List<string> RfaLabelledDimensionsOn(Document doc, HashSet<IdType> refIds)
        {
            var found = new List<string>();
            foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(Dimension)))
            {
                var dim = el as Dimension;
                FamilyParameter fp = dim == null ? null : RfaSafe(() => dim.FamilyLabel);
                if (fp == null) continue;
                var ids = new HashSet<IdType>();
                ReferenceArray ra = RfaSafe(() => dim.References);
                if (ra == null) continue;
                foreach (Reference r in ra)
                {
                    ElementId eid = r == null ? null : RfaSafe(() => r.ElementId);
                    if (eid != null && eid != ElementId.InvalidElementId) ids.Add(eid.GetIdValue());
                }
                if (refIds.IsSubsetOf(ids))
                    found.Add($"#{dim.Id.GetIdValue()} 標籤「{RfaSafe(() => fp.Definition.Name)}」");
            }
            return found;
        }

        private static double RfaRadToDeg(double rad) => Math.Round(rad * 180.0 / Math.PI, 6);

        /// <summary>
        /// 取參考在視圖平面上的跡線：通過點與方向（方向已扣掉視線分量並單位化）。
        /// prefer 不為 null 時，方向正負號對齊 prefer（撓曲自檢時保持同一扇區）。
        /// </summary>
        private static void RfaAngularTrace(RfaAngularRef r, XYZ viewDir, View view, XYZ prefer, out XYZ point, out XYZ dir)
        {
            if (r.Kind == "referenceLine")
            {
                var ln = r.Curve.GeometryCurve as Line
                         ?? throw new Exception($"參考線 {r.Id} 不是直線（圓弧不能標角度）");
                point = ln.GetEndPoint(0);
                XYZ d = ln.GetEndPoint(1) - ln.GetEndPoint(0);
                d = d - viewDir * d.DotProduct(viewDir);
                if (d.IsZeroLength()) throw new Exception($"參考線 {r.Id} 在視圖中投影成一個點");
                dir = d.Normalize();
            }
            else
            {
                ReferencePlane rp = r.Plane;
                point = rp.GetPlane().Origin;
                XYZ d = viewDir.CrossProduct(rp.Normal.Normalize());
                if (d.IsZeroLength()) throw new Exception($"參考平面「{rp.Name}」在此視圖中不是一條線");
                d = d.Normalize();
                if (prefer == null)
                {
                    XYZ hint = rp.BubbleEnd - rp.FreeEnd;
                    double s = hint.DotProduct(d);
                    if (Math.Abs(s) < 1e-6 * Math.Max(1.0, hint.GetLength()))
                    {
                        s = view.UpDirection.DotProduct(d);
                        if (Math.Abs(s) < 1e-6) s = view.RightDirection.DotProduct(d);
                    }
                    if (s < 0) d = d.Negate();
                }
                dir = d;
            }
            if (prefer != null && dir.DotProduct(prefer) < 0) dir = dir.Negate();
        }

        /// <summary>
        /// 角度標註：兩個不平行的參考（參考平面 / 直線參考線）在共同視圖平面上的夾角，可綁角度參數。
        /// 由 RfaCreateDimension 在 angular = true 時呼叫。
        /// </summary>
        private object RfaCreateAngularDimension(Document doc, JObject parameters)
        {
            FamilyManager fm = doc.FamilyManager;
            var warnings = new List<string>();

            if (parameters["equalize"]?.Value<bool>() == true)
                throw new Exception("angular 模式不支援 equalize（EQ 只用於線性多段尺寸）");
            if (parameters["throughPointMm"] != null && parameters["throughPointMm"].Type != JTokenType.Null)
                throw new Exception("angular 模式不支援 throughPointMm；角度尺寸的位置請用 arcRadiusMm 控制");

            // ── 蒐集參考：參考平面在前、參考線在後 ──
            var refs = new List<RfaAngularRef>();
            var planeNames = (parameters["referencePlanes"] as JArray)?.Select(t => t.Value<string>()).ToList() ?? new List<string>();
            foreach (var n in planeNames)
            {
                var rp = RfaFindRefPlane(doc, n);
                refs.Add(new RfaAngularRef { Kind = "referencePlane", Name = n, Plane = rp, Ref = rp.GetReference() });
            }
            var lineIds = (parameters["referenceLineIds"] as JArray)?.Select(t => t.Value<IdType>()).ToList() ?? new List<IdType>();
            foreach (var idv in lineIds)
            {
                var ce = doc.GetElement(idv.ToElementId()) as CurveElement
                         ?? throw new Exception($"元素 {idv} 不是參考線／模型線");
                if (ce.GeometryCurve is Arc) throw new Exception($"參考線 {idv} 是圓弧；角度標註只能用直線參考線");
                if (!(ce.GeometryCurve is Line)) throw new Exception($"參考線 {idv} 不是直線");
                Reference r = ce.GeometryCurve.Reference ?? new Reference(ce);
                refs.Add(new RfaAngularRef { Kind = "referenceLine", Id = idv, Curve = ce, Ref = r });
            }
            if (refs.Count != 2)
                throw new Exception($"角度標註需要恰好兩個參考（referencePlanes + referenceLineIds 合計），目前是 {refs.Count} 個");

            // ── 需要的視線方向：同時垂直於兩個參考的「面內方向」 ──
            // 參考線：方向 d；參考平面：法向量 n。視線需 ⟂ d（線）且 ⟂ n（面）→ 取兩者的外積。
            XYZ Ax(RfaAngularRef r) => r.Kind == "referenceLine"
                ? ((Line)r.Curve.GeometryCurve).Direction.Normalize()
                : r.Plane.Normal.Normalize();
            // 面×面＝交線方向；線×線＝兩線所在平面的法向量；線×面法向量＝同時 ⟂ 兩者的方向（線若平行於面，外積的跡線會平行，下面另行檢查）
            XYZ need = Ax(refs[0]).CrossProduct(Ax(refs[1]));
            string viewName = parameters["viewName"]?.Value<string>();
            bool mixed = refs[0].Kind != refs[1].Kind;
            if (need.GetLength() < Math.Sin(RfaAngleTolRad))
            {
                if (!mixed) throw new Exception("兩個參考互相平行，沒有角度可標");
                // 參考線垂直於參考平面：任何包含該線的視圖都看得到 90°，方向不唯一，要使用者指定
                if (string.IsNullOrEmpty(viewName))
                    throw new Exception("參考線垂直於參考平面（90°），可標的視圖不唯一；請用 viewName 指定一個能把兩者都看成線的視圖");
                need = null;
            }
            else need = need.Normalize();
            string needStr = need == null ? null : $"({Math.Round(need.X, 4)}, {Math.Round(need.Y, 4)}, {Math.Round(need.Z, 4)})";

            View view;
            try { view = RfaPickConstraintView(doc, null, null, need, viewName); }
            catch (Exception ex)
            {
                throw new Exception($"{ex.Message}。角度要在真實大小下標註，需要視線方向為 ±{needStr} 的平面圖、立面或剖面");
            }
            XYZ viewDir = view.ViewDirection.Normalize();
            // 指定的視圖也要驗：視線需垂直於參考線方向、垂直於參考平面法向量（兩者都看成線）
            foreach (var r in refs)
                if (Math.Abs(viewDir.DotProduct(Ax(r))) > 1e-6)
                    throw new Exception($"視圖「{view.Name}」看不到{(r.Kind == "referencePlane" ? $"參考平面「{r.Name}」" : $"參考線 {r.Id}")}的真實角度"
                                        + (needStr != null ? $"；需要視線方向為 ±{needStr} 的視圖" : "；視線需同時垂直於參考線並落在參考平面內"));

            // ── 跡線與頂點 ──
            var pts = new XYZ[2];
            var dirs = new XYZ[2];
            for (int i = 0; i < 2; i++) RfaAngularTrace(refs[i], viewDir, view, null, out pts[i], out dirs[i]);

            // 尺寸弧所在深度：有參考線就取第一條參考線的深度，否則取視圖原點的深度
            var firstLine = refs.FirstOrDefault(r => r.Kind == "referenceLine");
            XYZ depthOrigin = firstLine != null ? ((Line)firstLine.Curve.GeometryCurve).GetEndPoint(0) : view.Origin;
            if (refs.Count(r => r.Kind == "referenceLine") == 2)
            {
                XYZ p2 = ((Line)refs[1].Curve.GeometryCurve).GetEndPoint(0);
                double dz = (p2 - depthOrigin).DotProduct(viewDir);
                if (Math.Abs(dz) > RfaMm(0.5))
                    warnings.Add($"兩條參考線不在同一深度（沿視線相差 {RfaToMm(Math.Abs(dz))} mm），角度以投影量測");
            }
            XYZ Flat(XYZ p) => p - viewDir * (p - depthOrigin).DotProduct(viewDir);
            XYZ p0 = Flat(pts[0]), p1 = Flat(pts[1]);
            double denom = dirs[0].CrossProduct(dirs[1]).DotProduct(viewDir);
            if (Math.Abs(denom) < Math.Sin(RfaAngleTolRad))
                throw new Exception("兩個參考在此視圖中平行，沒有交點");
            double tParam = (p1 - p0).CrossProduct(dirs[1]).DotProduct(viewDir) / denom;
            XYZ vertex = p0 + dirs[0] * tParam;

            // ── 扇區與預期角度 ──
            bool supplementary = parameters["supplementary"]?.Value<bool>() ?? false;
            XYZ a = dirs[0];
            XYZ b = supplementary ? dirs[1].Negate() : dirs[1];
            double theta = a.AngleTo(b);   // 0..π
            if (theta < RfaAngleTolRad || Math.PI - theta < RfaAngleTolRad)
                throw new Exception("兩個參考在此視圖中平行，沒有角度可標");

            double arcR = RfaMm(parameters["arcRadiusMm"]?.Value<double>() ?? 300);
            if (arcR <= RfaMm(1)) throw new Exception("arcRadiusMm 必須大於 1 mm");

            // ── 標籤：先檢查幾何已在參數值上 ──
            string label = parameters["label"]?.Value<string>();
            FamilyParameter fp = null;
            if (!string.IsNullOrEmpty(label))
            {
                fp = RfaFindParam(fm, label) ?? throw new Exception($"找不到族群參數「{label}」，請先用 rfa_add_parameter 建立角度參數（type: angle）");
                if (fp.Definition.GetDataType() != SpecTypeId.Angle)
                    throw new Exception($"「{label}」不是角度參數（angular 模式只能綁角度參數）");
                double want = fm.CurrentType?.AsDouble(fp) ?? double.NaN;
                if (double.IsNaN(want)) throw new Exception($"目前類型沒有「{label}」的值");
                if (Math.Abs(want - theta) > RfaAngleTolRad)
                {
                    string hint = Math.Abs(want - (Math.PI - theta)) <= RfaAngleTolRad
                        ? $"（參數值剛好是相鄰扇區的角度，請改用 supplementary = {(!supplementary).ToString().ToLowerInvariant()}）"
                        : "（請先把幾何轉到參數值，或先把參數設成目前角度）";
                    throw new Exception($"量到的角度是 {RfaRadToDeg(theta)}°，但參數「{label}」是 {RfaRadToDeg(want)}°{hint}");
                }
            }

            // ── 尺寸弧：圓心在頂點，法向量 = 視線方向，從起始射線逆時針掃 θ ──
            XYZ start = a.CrossProduct(b).DotProduct(viewDir) >= 0 ? a : b;
            XYZ yAxis = viewDir.CrossProduct(start).Normalize();
            Arc arc = Arc.Create(Plane.CreateByOriginAndBasis(vertex, start, yAxis), arcR, 0, theta);

            bool keepIfFlexFails = parameters["keepIfFlexFails"]?.Type == JTokenType.Boolean
                                   && parameters["keepIfFlexFails"].Value<bool>();

            // 交易群組：撓曲自檢失敗時，整個建立（含視圖範圍調整）一併復原
            using (var tg = new TransactionGroup(doc, "MCP: rfa_create_dimension（角度）"))
            {
                tg.Start();
                object result = RfaCreateAngularDimensionCore(doc, fm, refs, view, viewDir, dirs, vertex, arc, theta,
                    supplementary, label, fp, arcR, firstLine, warnings, out string flex);

                bool flexFailed = flex != null && flex.StartsWith("FAILED", StringComparison.Ordinal);
                if (flexFailed && !keepIfFlexFails)
                {
                    tg.RollBack();
                    // 同一對參考上已有帶標籤的尺寸 → 原因是過度約束，不是缺少旋轉約束
                    var refIds = new HashSet<IdType>(refs.Select(r => r.Kind == "referenceLine" ? r.Id : r.Plane.Id.GetIdValue()));
                    var clashing = RfaLabelledDimensionsOn(doc, refIds);
                    if (clashing.Count > 0)
                        throw new Exception(
                            $"已復原：帶標籤「{label}」的角度尺寸未通過撓曲自檢（{flex}）。原因是過度約束：同一對參考已被帶標籤的尺寸控制（{string.Join("、", clashing)}），" +
                            "兩個標籤會互相衝突。請改用既有的參數（或用公式讓「" + label + "」等於它），或先用 rfa_delete_elements 刪掉既有的尺寸再標註。");
                    throw new Exception(
                        $"已復原：帶標籤「{label}」的角度尺寸未通過撓曲自檢（{flex}）。兩個參考沒有被約束成可繞頂點旋轉（或其他約束擋住了旋轉），保留它會讓之後改參數時 Revit 跳出「Constraints are not satisfied」。" +
                        "可運作的做法：先用 rfa_create_reference_line 以 constraints 的 start 把兩條線的起點分別鎖到兩個在頂點相交的參考平面，" +
                        "再以 curve 約束把其中一條線鎖到一個參考平面，最後再標註並加 label。若確定要保留未通過的尺寸，可傳 keepIfFlexFails: true。");
                }
                if (flexFailed)
                    warnings.Add("撓曲自檢未通過但依 keepIfFlexFails 保留了帶標籤的角度尺寸；之後修改此參數時 Revit 可能跳出「Constraints are not satisfied」");
                tg.Assimilate();
                return result;
            }
        }

        /// <summary>建立角度尺寸（含標籤）並執行撓曲自檢；由 RfaCreateAngularDimension 在交易群組內呼叫</summary>
        private object RfaCreateAngularDimensionCore(Document doc, FamilyManager fm, List<RfaAngularRef> refs, View view, XYZ viewDir,
            XYZ[] dirs, XYZ vertex, Arc arc, double theta, bool supplementary, string label, FamilyParameter fp, double arcR,
            RfaAngularRef firstLine, List<string> warnings, out string flex)
        {
            Dimension dim;
            using (var t = new Transaction(doc, "MCP: rfa_create_dimension（角度）"))
            {
                // Error（例如過度約束）時靜默復原，不跳 Revit 對話框
                var failures = RfaUseRollbackOnError(t);
                t.Start();

                // 平面圖看不到較高的參考線就無法標註
                if (firstLine != null)
                {
                    double zMax = refs.Where(r => r.Kind == "referenceLine")
                        .SelectMany(r => new[] { r.Curve.GeometryCurve.GetEndPoint(0).Z, r.Curve.GeometryCurve.GetEndPoint(1).Z }).Max();
                    string note = RfaEnsurePlanShows(doc, view, zMax);
                    if (note != null) warnings.Add(note);
                }

                try
                {
                    dim = doc.FamilyCreate.NewAngularDimension(view, arc, refs[0].Ref, refs[1].Ref);
                }
                catch (Exception ex1)
                {
                    // 弧的方向要求文件沒寫清楚：反向（法向量 = −視線）再試一次
                    try
                    {
                        Arc rev = Arc.Create(arc.GetEndPoint(1), arc.GetEndPoint(0), arc.Evaluate(0.5, true));
                        dim = doc.FamilyCreate.NewAngularDimension(view, rev, refs[0].Ref, refs[1].Ref);
                        warnings.Add($"以視線方向為法向量的尺寸弧被拒絕（{ex1.Message}），改用反向弧建立");
                    }
                    catch (Exception ex2)
                    {
                        throw new Exception($"建立角度尺寸失敗（視圖「{view.Name}」）：{ex1.Message}；反向弧亦失敗：{ex2.Message}");
                    }
                }
                if (dim == null) throw new Exception("建立角度尺寸失敗（Revit 回傳 null）");

                if (fp != null)
                {
                    try { dim.FamilyLabel = fp; }
                    catch (Exception ex) { throw new Exception($"角度尺寸綁定參數「{label}」失敗：{ex.Message}"); }
                }

                if (t.Commit() != TransactionStatus.Committed)
                    throw new Exception($"Revit 拒絕了角度尺寸或其標籤（交易被復原，可能是過度約束）{RfaFailureText(failures)}");
            }

            double? readBack = dim.Value;
            double? angleDeg = readBack.HasValue ? RfaRadToDeg(readBack.Value) : (double?)null;
            if (!readBack.HasValue)
                warnings.Add("讀不到角度尺寸的值");
            else if (Math.Abs(readBack.Value - theta) > RfaAngleTolRad)
                warnings.Add($"尺寸讀回 {angleDeg}°，與事前計算的 {RfaRadToDeg(theta)}° 不同（Revit 可能標到了另一個扇區；可改 supplementary 重試）");

            // ── 撓曲自檢：角度 +5°，重算兩參考夾角，然後一定復原 ──
            flex = null;
            if (fp != null) flex = RfaFlexCheckAngle(doc, fp, refs, view, viewDir, dirs, supplementary);

            return new
            {
                id = dim.Id.GetIdValue(),
                kind = "angular",
                view = view.Name,
                references = refs.Select((r, i) => r.Kind == "referencePlane"
                    ? (object)new { kind = r.Kind, name = r.Name, directionInView = RfaVec(dirs[i]) }
                    : new { kind = r.Kind, id = r.Id, directionInView = RfaVec(dirs[i]) }).ToList(),
                vertexMm = RfaPt(vertex),
                angleDeg,
                computedAngleDeg = RfaRadToDeg(theta),
                arcRadiusMm = RfaToMm(arcR),
                label = string.IsNullOrEmpty(label) ? null : label,
                supplementary,
                flexCheck = flex,
                warnings,
            };
        }

        /// <summary>
        /// 暫時把角度參數 +5°（以 RfaRollbackOnErrorPreprocessor 提交，約束無法滿足時靜默復原、不跳對話框），
        /// 重量兩參考夾角，最後整個試驗一定復原（見 RfaRunFlexTrial）
        /// </summary>
        private static string RfaFlexCheckAngle(Document doc, FamilyParameter fp, List<RfaAngularRef> refs,
                                                View view, XYZ viewDir, XYZ[] dirs0, bool supplementary)
        {
            FamilyManager fm = doc.FamilyManager;
            if (!string.IsNullOrEmpty(fp.Formula)) return "skipped（角度參數由公式驅動）";
            if (fp.IsReporting) return "skipped（報告參數）";
            if (fm.CurrentType == null) return "skipped（沒有目前類型）";

            double v = (fm.CurrentType.AsDouble(fp) ?? 0) + 5.0 * Math.PI / 180.0;
            return RfaRunFlexTrial(doc, "MCP: 角度撓曲自檢",
                () => fm.Set(fp, v),
                () =>
                {
                    RfaAngularTrace(refs[0], viewDir, view, dirs0[0], out _, out XYZ d0);
                    RfaAngularTrace(refs[1], viewDir, view, dirs0[1], out _, out XYZ d1);
                    double got = d0.AngleTo(supplementary ? d1.Negate() : d1);
                    return Math.Abs(got - v) <= RfaAngleTolRad
                        ? "OK"
                        : $"FAILED：參數改為 {RfaRadToDeg(v)}° 後，兩參考夾角是 {RfaRadToDeg(got)}°（幾何沒有跟著參數轉動）";
                });
        }

        #endregion
    }
}
