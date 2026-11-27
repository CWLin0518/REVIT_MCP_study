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
    /// RFA 族群編輯工具第十四批：圓形尺寸參數化。
    ///
    /// 1. 形體輪廓圓的直徑標籤（rfa_create_extrusion / rfa_create_blend 的 circle.diameterParameter）：
    ///    與 rfa_create_sweep 的 diameterParameter 同一條路徑——形體建立後，在草圖模式外對草圖圓弧
    ///    建 NewDiameterDimension 並掛 FamilyLabel，再做一次一定復原的撓曲自檢（半徑跟著參數、圓心不動）。
    ///    標籤失敗時形體保留、直徑固定，原因寫進 warnings。
    /// 2. 圓弧半徑標籤（rfa_create_reference_line / rfa_create_symbolic_line 的 radiusParameter）共用 RfaLabelArcRadius。
    /// </summary>
    public partial class CommandExecutor
    {
        #region 輪廓圓直徑標籤

        private class RfaCircleLabelRequest
        {
            public string Param;
            public XYZ Center;
            public XYZ Normal;
            public double Radius;
            public string Where;
        }

        /// <summary>
        /// 讀取輪廓迴圈的 circle.diameterParameter，驗證參數存在、是長度、現值等於圓的直徑，
        /// 加入待標籤清單。沒有 diameterParameter 時不做事。必須在形體建立前呼叫，錯誤時整個工具拒絕。
        /// </summary>
        private static void RfaCollectCircleLabel(FamilyManager fm, JObject loop, RfaSketchFrame f, string where,
                                                  List<RfaCircleLabelRequest> list)
        {
            if (!(loop?["circle"] is JObject c)) return;
            string dp = c["diameterParameter"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(dp)) return;

            FamilyParameter fp = RfaFindParam(fm, dp) ?? throw new Exception($"{where} 的 diameterParameter 找不到族群參數「{dp}」");
            if (fp.Definition.GetDataType() != SpecTypeId.Length) throw new Exception($"{where} 的 diameterParameter「{dp}」不是長度參數");

            var ctr = c["center"] as JArray;
            double r = c["radius"]?.Value<double>() ?? 0;
            double want = fm.CurrentType?.AsDouble(fp) ?? -1;
            if (Math.Abs(want - RfaMm(2 * r)) > RfaMm(0.5))
                throw new Exception($"{where} 的圓直徑是 {2 * r} mm，但參數「{dp}」目前是 {RfaToMm(want)} mm；請先讓兩者相等再標籤");

            list.Add(new RfaCircleLabelRequest
            {
                Param = dp,
                Center = f.At(ctr[0].Value<double>(), ctr[1].Value<double>()),
                Normal = f.Normal.Normalize(),
                Radius = RfaMm(r),
                Where = where,
            });
        }

        /// <summary>草圖圓弧圓心與要求的圓心比對時，忽略沿法向的差距（融合頂部草圖的實際高度由 Revit 決定）</summary>
        private static bool RfaArcMatches(Arc a, RfaCircleLabelRequest req, double tol)
        {
            if (Math.Abs(a.Radius - req.Radius) > tol) return false;
            if (Math.Abs(Math.Abs(a.Normal.Normalize().DotProduct(req.Normal)) - 1) > 1e-6) return false;
            XYZ d = a.Center - req.Center;
            XYZ inPlane = d - req.Normal * d.DotProduct(req.Normal);
            return inPlane.GetLength() < tol;
        }

        /// <summary>
        /// 形體交易提交後呼叫：在 sketches 中找出每個要求對應的圓弧，建直徑尺寸並掛參數（獨立交易，失敗靜默復原），
        /// 再對每個非公式參數做 +10 mm 撓曲自檢。回傳 (labels, flexChecks)；失敗只寫 warnings，不丟例外。
        /// </summary>
        private static (List<object> labels, List<object> flex) RfaLabelProfileCircles(
            Document doc, IEnumerable<Sketch> sketches, List<RfaCircleLabelRequest> reqs, string viewName, List<string> warnings)
        {
            var labels = new List<object>();
            var flex = new List<object>();
            if (reqs == null || reqs.Count == 0) return (labels, flex);

            FamilyManager fm = doc.FamilyManager;
            double tol = RfaMm(0.5);
            var arcs = sketches.Where(s => s != null)
                .SelectMany(s => s.GetAllElements()).Select(id => doc.GetElement(id)).OfType<CurveElement>()
                .Where(ce => ce.GeometryCurve is Arc).ToList();

            // 每個參數 → 它驅動的圓弧元素與原始圓心（撓曲自檢用）
            var tracked = new List<(CurveElement el, RfaCircleLabelRequest req)>();
            try
            {
                using (var t = new Transaction(doc, "MCP: 輪廓圓直徑標籤"))
                {
                    var failures = RfaUseRollbackOnError(t);
                    t.Start();
                    foreach (var req in reqs)
                    {
                        var matched = arcs.Where(ce => RfaArcMatches((Arc)ce.GeometryCurve, req, tol)).ToList();
                        if (matched.Count == 0) throw new Exception($"{req.Where}：形體草圖裡找不到半徑 {RfaToMm(req.Radius)} mm 的圓弧");
                        FamilyParameter fp = RfaFindParam(fm, req.Param);
                        foreach (var arcEl in matched)
                        {
                            var arc = (Arc)arcEl.GeometryCurve;
                            View view = RfaPickConstraintView(doc, null, null, arc.Normal, viewName);
                            string note = RfaEnsurePlanShows(doc, view, arc.Center.Z);
                            if (note != null && !warnings.Contains(note)) warnings.Add(note);
                            XYZ at = arc.Center + (arc.XDirection + arc.YDirection).Normalize() * arc.Radius * 1.5;
                            Dimension dd = doc.FamilyCreate.NewDiameterDimension(view, arc.Reference ?? new Reference(arcEl), at);
                            dd.FamilyLabel = fp;
                            labels.Add(new { where = req.Where, id = dd.Id.GetIdValue(), arcId = arcEl.Id.GetIdValue(), view = view.Name, label = req.Param });
                            tracked.Add((arcEl, req));
                        }
                    }
                    if (t.Commit() != TransactionStatus.Committed) throw new Exception($"交易被復原{RfaFailureText(failures)}");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"輪廓圓直徑未綁參數：{ex.Message}（形體已建立，直徑固定）");
                labels.Clear();
                return (labels, flex);
            }

            foreach (var grp in tracked.GroupBy(x => x.req.Param))
            {
                FamilyParameter fp = RfaFindParam(fm, grp.Key);
                string result;
                if (!string.IsNullOrEmpty(fp.Formula)) result = "skipped（參數由公式驅動；請改驅動它的參數做撓曲測試）";
                else if (fm.CurrentType == null) result = "skipped（沒有目前類型）";
                else
                {
                    double v = (fm.CurrentType.AsDouble(fp) ?? 0) + RfaMm(10);
                    var items = grp.ToList();
                    result = RfaRunFlexTrial(doc, "MCP: 輪廓圓直徑撓曲自檢",
                        () => fm.Set(fp, v),
                        () =>
                        {
                            var bad = new List<string>();
                            foreach (var (el, req) in items)
                            {
                                var a = el.GeometryCurve as Arc;
                                if (a == null) { bad.Add($"元素 {el.Id.GetIdValue()} 不再是圓弧"); continue; }
                                if (Math.Abs(a.Radius - v / 2) > RfaMm(0.1))
                                    bad.Add($"{req.Where} 直徑 {RfaToMm(a.Radius * 2)} ≠ {RfaToMm(v)}");
                                XYZ d = a.Center - req.Center;
                                double drift = (d - req.Normal * d.DotProduct(req.Normal)).GetLength();
                                if (drift > RfaMm(0.5))
                                    bad.Add($"{req.Where} 圓心偏移 {RfaToMm(drift)} mm");
                            }
                            return bad.Count == 0 ? "OK" : "FAILED：" + string.Join("；", bad);
                        });
                }
                flex.Add(new { parameter = grp.Key, result });
                if (result.StartsWith("FAILED"))
                    warnings.Add($"直徑參數「{grp.Key}」撓曲自檢失敗：{result}。標籤已保留，請在 Revit 確認或用 rfa_delete_elements 刪除該尺寸");
            }
            return (labels, flex);
        }

        #endregion

        #region 圓弧半徑標籤（參考線／符號線共用）

        /// <summary>在交易內呼叫：圓弧現值半徑必須等於參數現值，建半徑尺寸並掛參數</summary>
        private static void RfaLabelArcRadius(Document doc, FamilyManager fm, CurveElement mc, string radParam,
                                              string viewName, List<object> results, List<string> warnings)
        {
            if (string.IsNullOrEmpty(radParam)) return;
            if (!(mc.GeometryCurve is Arc arc)) throw new Exception("radiusParameter 只適用於圓弧");
            FamilyParameter fp = RfaFindParam(fm, radParam) ?? throw new Exception($"找不到族群參數「{radParam}」");
            double want = fm.CurrentType?.AsDouble(fp) ?? -1;
            if (Math.Abs(want - arc.Radius) > RfaMm(0.5))
                throw new Exception($"圓弧半徑是 {RfaToMm(arc.Radius)} mm，但參數「{radParam}」是 {RfaToMm(want)} mm");
            View view = RfaPickConstraintView(doc, null, null, arc.Normal, viewName);
            string note = RfaEnsurePlanShows(doc, view, Math.Max(arc.GetEndPoint(0).Z, arc.Center.Z));
            if (note != null && !warnings.Contains(note)) warnings.Add(note);
            XYZ midDir = (arc.Evaluate(0.5, true) - arc.Center).Normalize();
            Dimension rd = doc.FamilyCreate.NewRadialDimension(view, arc.Reference, arc.Center + midDir * arc.Radius * 0.6);
            rd.FamilyLabel = fp;
            results.Add(new { target = "radius", kind = "radius", label = radParam, id = rd.Id.GetIdValue(), view = view.Name });
        }

        #endregion
    }
}
