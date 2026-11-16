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
    /// RFA 族群編輯工具第三批形體：旋轉（rfa_create_revolution）與掃掠融合（rfa_create_swept_blend）。
    ///
    /// 掃掠融合：單一路徑曲線（直線／三點弧，或一條參考線）＋起點、終點各一個封閉輪廓。
    /// 輪廓畫在原點的 XY 平面（與 rfa_create_sweep 相同慣例），Revit 會把它們放到路徑兩端並垂直於路徑。
    /// 哪個輪廓參數落在路徑起點，比照 rfa_create_blend 的作法：建立後讀回、比對，若相反就刪除對調重建。
    ///
    /// 旋轉的輪廓與旋轉軸都畫在同一個草圖平面上，座標沿用擠出的 2D [u, v] mm 慣例。
    /// 輪廓不可跨越旋轉軸（可以貼齊軸線，這是做實心迴轉體的方式）；跨軸時 Revit 只回一句含糊的錯誤，
    /// 所以先在呼叫 Revit 之前自行檢查，並指出是哪個輪廓、跨過多少 mm。
    /// 共用 CommandExecutor.RfaFamily.cs 的草圖平面／輪廓輔助函式與 RfaFamily2.cs 的材質／參數綁定。
    /// </summary>
    public partial class CommandExecutor
    {
        #region rfa_create_revolution

        /// <summary>
        /// 輪廓曲線上各點到旋轉軸的有號距離（內部單位），正負代表在軸的哪一側。
        /// 直線取兩端點；圓弧取兩端點、中點，以及在法向量方向上的極值點（若落在弧上），所以圓的判斷是精確的。
        /// </summary>
        private static List<double> RfaSignedDistancesToAxis(CurveArray loop, XYZ axisOrigin, XYZ side)
        {
            var result = new List<double>();
            foreach (Curve c in loop)
            {
                var pts = new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) };
                if (c is Arc arc)
                {
                    pts.Add(arc.Evaluate(0.5, true));
                    foreach (XYZ cand in new[] { arc.Center + side * arc.Radius, arc.Center - side * arc.Radius })
                    {
                        IntersectionResult pr = arc.Project(cand);
                        if (pr != null && pr.XYZPoint.DistanceTo(cand) < 1e-6) pts.Add(cand);
                    }
                }
                else if (!(c is Line))
                {
                    for (int i = 0; i <= 8; i++) pts.Add(c.Evaluate(i / 8.0, true));
                }
                foreach (XYZ p in pts) result.Add((p - axisOrigin).DotProduct(side));
            }
            return result;
        }

        private object RfaCreateRevolution(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;

            var profiles = parameters["profiles"] as JArray;
            if (profiles == null || profiles.Count == 0)
                throw new Exception("請提供 profiles（至少一個封閉輪廓；多個輪廓時，內部輪廓會成為開孔）");

            var axisObj = parameters["axis"] as JObject
                          ?? throw new Exception("請提供 axis {\"start\": [u, v], \"end\": [u, v]}（旋轉軸，與輪廓同一套草圖 2D 座標，mm）");
            double[] ReadUv(string key)
            {
                var a = axisObj[key] as JArray;
                if (a == null || a.Count != 2) throw new Exception($"axis.{key} 需要 [u, v]（mm）");
                return new[] { a[0].Value<double>(), a[1].Value<double>() };
            }
            double[] axS = ReadUv("start"), axE = ReadUv("end");
            if (Math.Sqrt(Math.Pow(axE[0] - axS[0], 2) + Math.Pow(axE[1] - axS[1], 2)) < 0.5)
                throw new Exception("旋轉軸長度為零（axis.start 與 axis.end 重合），請給兩個相距至少 0.5 mm 的點");

            double startDeg = parameters["startAngleDeg"]?.Value<double>() ?? 0;
            double endDeg = parameters["endAngleDeg"]?.Value<double>() ?? 360;
            double sweepDeg = Math.Abs(endDeg - startDeg);
            if (sweepDeg < 0.01) throw new Exception("旋轉角度範圍（endAngleDeg - startAngleDeg）太小，至少 0.01°");
            if (sweepDeg > 360 + 1e-9) throw new Exception($"旋轉角度範圍不可超過 360°（目前 {sweepDeg}°）");
            double startRad = UnitUtils.ConvertToInternalUnits(startDeg, UnitTypeId.Degrees);
            double endRad = UnitUtils.ConvertToInternalUnits(endDeg, UnitTypeId.Degrees);

            bool isSolid = parameters["isSolid"]?.Value<bool>() ?? true;
            string materialName = parameters["material"]?.Value<string>();
            var associate = parameters["associate"] as JObject;

            var warnings = new List<string>();

            using (var t = new Transaction(doc, "MCP: rfa_create_revolution"))
            {
                t.Start();
                RfaSketchFrame frame = RfaResolveSketchFrame(doc, parameters);

                XYZ a3 = frame.At(axS[0], axS[1]);
                XYZ b3 = frame.At(axE[0], axE[1]);
                Line axisLine = Line.CreateBound(a3, b3);
                // 草圖平面內、垂直於軸的方向：用來判斷點在軸的哪一側
                XYZ side = frame.Normal.CrossProduct((b3 - a3).Normalize()).Normalize();

                var loops = new CurveArrArray();
                var loopList = new List<CurveArray>();
                int idx = 0;
                foreach (JObject loop in profiles.OfType<JObject>())
                {
                    CurveArray ca = RfaBuildLoop(loop, frame, idx++);
                    loops.Append(ca);
                    loopList.Add(ca);
                }

                // 預檢：所有輪廓必須在軸的同一側（貼齊軸線可以）
                double tol = RfaMm(0.01);
                int overallSign = 0;
                int overallSignLoop = -1;
                for (int i = 0; i < loopList.Count; i++)
                {
                    var d = RfaSignedDistancesToAxis(loopList[i], a3, side);
                    double max = d.Max(), min = d.Min();
                    if (max > tol && min < -tol)
                        throw new Exception(
                            $"輪廓 #{i} 跨越旋轉軸：一側最遠 {RfaToMm(max)} mm、另一側最遠 {RfaToMm(-min)} mm。" +
                            "輪廓必須完全位於軸的同一側（可以貼齊軸線），請移動輪廓或旋轉軸。");
                    int sign = max > tol ? 1 : min < -tol ? -1 : 0;
                    if (sign == 0) continue;
                    if (overallSign == 0) { overallSign = sign; overallSignLoop = i; }
                    else if (sign != overallSign)
                        throw new Exception(
                            $"輪廓 #{i} 與輪廓 #{overallSignLoop} 分別位於旋轉軸的兩側（輪廓 #{i} 離軸最遠 {RfaToMm(Math.Max(Math.Abs(max), Math.Abs(min)))} mm）。" +
                            "所有輪廓必須位於軸的同一側。");
                }
                if (overallSign == 0)
                    throw new Exception("所有輪廓都落在旋轉軸上（面積為零），無法旋轉");

                Revolution rev;
                try
                {
                    rev = doc.FamilyCreate.NewRevolution(isSolid, loops, frame.Sketch, axisLine, startRad, endRad);
                }
                catch (Exception ex)
                {
                    throw new Exception($"建立旋轉失敗：{ex.Message}（常見原因：輪廓不封閉或自我交錯、旋轉軸不在草圖平面上、族群樣板不支援旋轉（例如概念量體／自適應族群））");
                }
                if (rev == null) throw new Exception("建立旋轉失敗（Revit 回傳 null）");

                // 明確設定起訖角度，不依賴 NewRevolution 的角度參數是否被如實採用
                try { rev.StartAngle = startRad; }
                catch (Exception ex) { warnings.Add($"設定 StartAngle 失敗：{ex.Message}"); }
                try { rev.EndAngle = endRad; }
                catch (Exception ex) { warnings.Add($"設定 EndAngle 失敗：{ex.Message}"); }

                var assocResults = RfaApplyMaterialAndAssociations(doc, fm, rev, isSolid, materialName, associate,
                    new Dictionary<string, BuiltInParameter>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "startAngle", BuiltInParameter.REVOLUTION_START_ANGLE },
                        { "endAngle", BuiltInParameter.REVOLUTION_END_ANGLE },
                    }, warnings);

                // 自我檢查：重生後讀回實際包圍盒，讓呼叫端比對預期尺寸
                object bbox = null;
                try
                {
                    doc.Regenerate();
                    BoundingBoxXYZ bb = rev.get_BoundingBox(null);
                    if (bb != null) bbox = new { min = RfaPt(bb.Min), max = RfaPt(bb.Max) };
                }
                catch (Exception ex) { warnings.Add($"讀取包圍盒失敗：{ex.Message}"); }

                double readStart = Math.Round(UnitUtils.ConvertFromInternalUnits(rev.StartAngle, UnitTypeId.Degrees), 6);
                double readEnd = Math.Round(UnitUtils.ConvertFromInternalUnits(rev.EndAngle, UnitTypeId.Degrees), 6);

                if (t.Commit() != TransactionStatus.Committed) throw new Exception("建立旋轉的交易被復原");

                return new
                {
                    id = rev.Id.GetIdValue(),
                    isSolid,
                    sketchPlane = frame.Description,
                    axisStartMm = RfaPt(a3),
                    axisEndMm = RfaPt(b3),
                    startAngleDeg = readStart,
                    endAngleDeg = readEnd,
                    profileCount = profiles.Count,
                    boundingBoxMm = bbox,
                    associated = assocResults,
                    warnings,
                };
            }
        }

        #endregion

        #region rfa_create_swept_blend

        /// <summary>讀回輪廓的取樣點（各曲線兩端點＋中點），模型座標</summary>
        private static List<XYZ> RfaProfileSamplePoints(CurveArrArray profile)
        {
            var pts = new List<XYZ>();
            if (profile == null) return pts;
            foreach (CurveArray loop in profile)
                foreach (Curve c in loop)
                {
                    pts.Add(c.GetEndPoint(0));
                    pts.Add(c.Evaluate(0.5, true));
                    pts.Add(c.GetEndPoint(1));
                }
            return pts;
        }

        /// <summary>
        /// 讀回輪廓相對於某個路徑端點的距離證據：
        /// centroid = 取樣點平均到該點的距離；plane = 該點到輪廓平面的距離（優先用草圖平面，否則由曲線擬合）。
        /// 兩者相加作為「離這個端點多近」的分數，越小越近。
        /// </summary>
        private static (double score, double centroidDist, double planeDist) RfaProfileDistance(
            List<XYZ> pts, Plane sketchPlane, XYZ target)
        {
            if (pts.Count == 0) return (double.NaN, double.NaN, double.NaN);
            XYZ sum = XYZ.Zero;
            foreach (XYZ p in pts) sum += p;
            XYZ centroid = sum / pts.Count;
            double cd = centroid.DistanceTo(target);

            double pd = double.NaN;
            if (sketchPlane != null)
            {
                pd = Math.Abs((target - sketchPlane.Origin).DotProduct(sketchPlane.Normal.Normalize()));
            }
            else
            {
                XYZ n = null;
                for (int i = 1; i < pts.Count && n == null; i++)
                    for (int j = i + 1; j < pts.Count && n == null; j++)
                    {
                        XYZ c = (pts[i] - pts[0]).CrossProduct(pts[j] - pts[0]);
                        if (c.GetLength() > 1e-6) n = c.Normalize();
                    }
                if (n != null) pd = Math.Abs((target - pts[0]).DotProduct(n));
            }
            return (cd + (double.IsNaN(pd) ? 0 : pd), cd, pd);
        }

        private static Plane RfaSketchPlaneOf(Sketch sk)
        {
            try { return sk?.SketchPlane?.GetPlane(); }
            catch { return null; }
        }

        private object RfaCreateSweptBlend(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;

            // ── 路徑：path（單一 line / arc）或 pathReferenceLineId（單一參考線），二擇一 ──
            JToken pathTok = parameters["path"];
            JToken refTok = parameters["pathReferenceLineId"];
            if (parameters["pathReferenceLineIds"] != null || parameters["pathSegments"] != null || parameters["pathPoints"] != null)
                throw new Exception("rfa_create_swept_blend 的路徑只能是單一曲線：請用 path（一段 line 或 arc）或 pathReferenceLineId（一條參考線）。" +
                                    "多段路徑請拆成數個掃掠融合，每段一個。");
            if (pathTok is JArray)
                throw new Exception("path 只能是單一曲線物件 {\"line\": {...}} 或 {\"arc\": {...}}，不能是陣列；掃掠融合的路徑只有一段，多段路徑請拆成數個掃掠融合。");
            if (refTok is JArray)
                throw new Exception("pathReferenceLineId 只能是一條參考線的元素 id（數字），不能是陣列；多段路徑請拆成數個掃掠融合。");
            if ((pathTok == null || pathTok.Type == JTokenType.Null) == (refTok == null || refTok.Type == JTokenType.Null))
                throw new Exception("請提供 path（{\"line\": {start, end}} 或 {\"arc\": {start, mid, end}}，mm）或 pathReferenceLineId（參考線元素 id），且只能二擇一");

            Curve pathCurve;
            Reference pathRef = null;
            string pathKind;
            if (pathTok != null && pathTok.Type != JTokenType.Null)
            {
                var segObj = pathTok as JObject ?? throw new Exception("path 需要物件 {\"line\": {...}} 或 {\"arc\": {...}}");
                if (segObj["line"] != null && segObj["arc"] != null)
                    throw new Exception("path 只能有 line 或 arc 其中之一；掃掠融合的路徑只有一段，多段路徑請拆成數個掃掠融合。");
                pathKind = segObj["line"] != null ? "line" : segObj["arc"] != null ? "arc" : null;
                if (pathKind == null) throw new Exception("path 需要 line {start, end} 或 arc {start, mid, end}");
                try
                {
                    pathCurve = RfaReadPathSegment(segObj, 0);
                }
                catch (Exception ex)
                {
                    throw new Exception($"路徑無效：{ex.Message}（常見原因：起訖點重合（長度為零）、弧的三點共線）");
                }
                if (pathCurve.Length < RfaMm(0.5)) throw new Exception("路徑長度小於 0.5 mm");
            }
            else
            {
                IdType idv = refTok.Value<IdType>();
                var ce = doc.GetElement(idv.ToElementId()) as CurveElement
                         ?? throw new Exception($"元素 {idv} 不是參考線／模型線");
                pathCurve = ce.GeometryCurve ?? throw new Exception($"參考線 {idv} 沒有幾何曲線");
                pathRef = pathCurve.Reference ?? throw new Exception($"參考線 {idv} 取不到參考");
                pathKind = "referenceLine";
            }
            XYZ pathStart = pathCurve.GetEndPoint(0), pathEnd = pathCurve.GetEndPoint(1);

            // ── 輪廓 ──
            var startLoop = parameters["startProfile"] as JObject ?? throw new Exception("請提供 startProfile（路徑起點的封閉輪廓）");
            var endLoop = parameters["endProfile"] as JObject ?? throw new Exception("請提供 endProfile（路徑終點的封閉輪廓）");
            // 輪廓畫在原點的 XY 平面，Revit 會搬到路徑端點並垂直於路徑（與 rfa_create_sweep 相同）
            var xyFrame = new RfaSketchFrame { Origin = XYZ.Zero, U = XYZ.BasisX, V = XYZ.BasisY, Normal = XYZ.BasisZ };
            CurveArray startCurves = RfaBuildLoop(startLoop, xyFrame, 0);
            CurveArray endCurves = RfaBuildLoop(endLoop, xyFrame, 1);
            string wantStart = RfaLoopSignature(startCurves), wantEnd = RfaLoopSignature(endCurves);

            bool isSolid = parameters["isSolid"]?.Value<bool>() ?? true;
            var warnings = new List<string>();

            using (var t = new Transaction(doc, "MCP: rfa_create_swept_blend"))
            {
                t.Start();

                // 每次建立都用新的 SweepProfile（重建時不重用可能已被前一個形體吃掉的物件）
                SweepProfile MakeProfile(JObject loop, int idx)
                {
                    var arr = new CurveArrArray();
                    arr.Append(RfaBuildLoop(loop, xyFrame, idx));
                    return _uiApp.Application.Create.NewCurveLoopsProfile(arr);
                }

                // 曲線路徑的草圖平面：API 文件說可為 null，但掃掠在 Revit 2027 實測 null 會丟例外，所以一律自建。
                SketchPlane pathPlane = null;
                SketchPlane GetPathPlane()
                {
                    if (pathPlane != null && pathPlane.IsValidObject) return pathPlane;
                    XYZ planeNormal, planeOrigin;
                    if (pathCurve is Arc arc)
                    {
                        planeNormal = arc.Normal;
                        planeOrigin = arc.Center;
                    }
                    else
                    {
                        XYZ dir = (pathEnd - pathStart).Normalize();
                        XYZ helper = Math.Abs(dir.DotProduct(XYZ.BasisZ)) > 0.999 ? XYZ.BasisX : XYZ.BasisZ;
                        planeNormal = dir.CrossProduct(helper).Normalize();
                        planeOrigin = pathStart;
                    }
                    pathPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(planeNormal, planeOrigin));
                    return pathPlane;
                }

                // NewSweptBlend(isSolid, path, [pathPlane,] bottomProfile, topProfile)：文件說「由 bottom 沿路徑掃到 top」，
                // 預設把 startProfile 當 bottom（第一個輪廓參數）。實際落點建立後再讀回驗證。
                SweptBlend Create(JObject firstLoop, int firstIdx, JObject secondLoop, int secondIdx)
                {
                    SweepProfile p1 = MakeProfile(firstLoop, firstIdx);
                    SweepProfile p2 = MakeProfile(secondLoop, secondIdx);
                    try
                    {
                        return pathRef != null
                            ? doc.FamilyCreate.NewSweptBlend(isSolid, pathRef, p1, p2)
                            : doc.FamilyCreate.NewSweptBlend(isSolid, pathCurve, GetPathPlane(), p1, p2);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"建立掃掠融合失敗：{ex.Message}（常見原因：輪廓相對於弧形路徑半徑太大而自我交錯、路徑長度為零或不在單一平面上、輪廓不封閉或自我交錯、族群樣板不支援掃掠融合）");
                    }
                }

                // 判斷讀回的 Bottom/Top 輪廓哪個在路徑起點；回傳 true = Bottom 在起點、false = Top 在起點、null = 無法判斷
                object evidence = null;
                bool? BottomAtStart(SweptBlend sb)
                {
                    var bPts = RfaProfileSamplePoints(sb.BottomProfile);
                    var tPts = RfaProfileSamplePoints(sb.TopProfile);
                    Plane bPl = RfaSketchPlaneOf(sb.BottomSketch), tPl = RfaSketchPlaneOf(sb.TopSketch);
                    var bS = RfaProfileDistance(bPts, bPl, pathStart);
                    var bE = RfaProfileDistance(bPts, bPl, pathEnd);
                    var tS = RfaProfileDistance(tPts, tPl, pathStart);
                    var tE = RfaProfileDistance(tPts, tPl, pathEnd);

                    double? Mm(double v) => double.IsNaN(v) ? (double?)null : RfaToMm(v);
                    object Side(List<XYZ> pts, Plane pl, (double score, double centroidDist, double planeDist) s, (double score, double centroidDist, double planeDist) e)
                    {
                        XYZ centroid = null;
                        if (pts.Count > 0) { XYZ sum = XYZ.Zero; foreach (XYZ p in pts) sum += p; centroid = sum / pts.Count; }
                        return new
                        {
                            centroidMm = centroid == null ? null : RfaPt(centroid),
                            sketchPlaneOriginMm = pl == null ? null : RfaPt(pl.Origin),
                            sketchPlaneNormal = pl == null ? null : RfaVec(pl.Normal),
                            toPathStart = new { centroidDistMm = Mm(s.centroidDist), planeDistMm = Mm(s.planeDist), scoreMm = Mm(s.score) },
                            toPathEnd = new { centroidDistMm = Mm(e.centroidDist), planeDistMm = Mm(e.planeDist), scoreMm = Mm(e.score) },
                        };
                    }

                    // 正值：Bottom 靠近終點、Top 靠近起點；負值：Bottom 在起點
                    double combined = double.NaN;
                    if (!double.IsNaN(bS.score) && !double.IsNaN(bE.score) && !double.IsNaN(tS.score) && !double.IsNaN(tE.score))
                        combined = (bS.score - bE.score) + (tE.score - tS.score);
                    else if (!double.IsNaN(bS.score) && !double.IsNaN(bE.score))
                        combined = bS.score - bE.score;
                    else if (!double.IsNaN(tS.score) && !double.IsNaN(tE.score))
                        combined = tE.score - tS.score;

                    bool? result = double.IsNaN(combined) || Math.Abs(combined) < RfaMm(1) ? (bool?)null : combined < 0;
                    evidence = new
                    {
                        method = "讀回 BottomProfile/TopProfile 曲線（模型座標）的形心距離＋到 BottomSketch/TopSketch 草圖平面的距離，比對路徑起訖點",
                        bottomAtPathStart = result,
                        combinedScoreMm = Mm(combined),
                        bottom = Side(bPts, bPl, bS, bE),
                        top = Side(tPts, tPl, tS, tE),
                    };
                    return result;
                }

                SweptBlend sb0 = Create(startLoop, 0, endLoop, 1);
                if (sb0 == null) throw new Exception("建立掃掠融合失敗（Revit 回傳 null）");
                doc.Regenerate();

                bool? bottomAtStart = BottomAtStart(sb0);
                bool swapped = false;
                if (wantStart != wantEnd)
                {
                    if (bottomAtStart == null)
                    {
                        warnings.Add("無法從讀回的輪廓位置判斷哪個輪廓在路徑起點，未做起訖自動校正；請看 startProfileCurves/endProfileCurves 與 placementEvidence 自行確認");
                    }
                    else
                    {
                        CurveArrArray atStart = bottomAtStart.Value ? sb0.BottomProfile : sb0.TopProfile;
                        string gotStart = RfaLoopSignature(atStart);
                        if (gotStart == wantEnd && gotStart != wantStart)
                        {
                            doc.Delete(sb0.Id);
                            sb0 = Create(endLoop, 1, startLoop, 0);
                            if (sb0 == null) throw new Exception("對調輪廓重建掃掠融合失敗（Revit 回傳 null）");
                            doc.Regenerate();
                            bottomAtStart = BottomAtStart(sb0);
                            swapped = true;
                            warnings.Add("此 Revit 版本的 NewSweptBlend 把起點輪廓放到了路徑終點，已自動對調輪廓參數重建");
                        }
                        else if (gotStart != wantStart)
                        {
                            warnings.Add($"路徑起點的輪廓簽章（{gotStart}）與要求的 startProfile（{wantStart}）、endProfile（{wantEnd}）都不相符，未自動校正");
                        }
                    }
                }
                SweptBlend sb = sb0;

                var assoc = RfaApplyMaterialAndAssociations(doc, fm, sb, isSolid,
                    parameters["material"]?.Value<string>(), parameters["associate"] as JObject, null, warnings);

                // 自我檢查：起訖兩端實際的輪廓組成（Bottom/Top 依判斷結果對應到路徑起訖；無法判斷時假設 Bottom 在起點）
                bool bottomIsStart = bottomAtStart ?? true;
                string startDesc = RfaDescribeProfile(bottomIsStart ? sb.BottomProfile : sb.TopProfile);
                string endDesc = RfaDescribeProfile(bottomIsStart ? sb.TopProfile : sb.BottomProfile);
                if (wantStart != wantEnd && bottomAtStart != null)
                {
                    string finalStart = RfaLoopSignature(bottomIsStart ? sb.BottomProfile : sb.TopProfile);
                    if (finalStart != wantStart)
                        warnings.Add($"最終路徑起點的輪廓簽章（{finalStart}）仍與 startProfile（{wantStart}）不同，請檢查");
                }

                object bbox = null;
                try
                {
                    doc.Regenerate();
                    BoundingBoxXYZ bb = sb.get_BoundingBox(null);
                    if (bb != null) bbox = new { min = RfaPt(bb.Min), max = RfaPt(bb.Max) };
                    else warnings.Add("讀不到包圍盒（Revit 回傳 null）");
                }
                catch (Exception ex) { warnings.Add($"讀取包圍盒失敗：{ex.Message}"); }

                if (t.Commit() != TransactionStatus.Committed) throw new Exception("建立掃掠融合的交易被復原");

                return new
                {
                    id = sb.Id.GetIdValue(),
                    isSolid,
                    pathKind,
                    pathReferenceLineId = pathRef != null ? refTok.Value<IdType>() : (IdType?)null,
                    pathStartMm = RfaPt(pathStart),
                    pathEndMm = RfaPt(pathEnd),
                    pathLengthMm = RfaToMm(pathCurve.Length),
                    startProfileCurves = startDesc,
                    endProfileCurves = endDesc,
                    profilesSwapped = swapped,
                    // 原始證據：讓實機測試確認 Bottom/Top 與路徑起訖的對應
                    placementEvidence = evidence,
                    boundingBoxMm = bbox,
                    associated = assoc,
                    warnings,
                };
            }
        }

        #endregion
    }
}
