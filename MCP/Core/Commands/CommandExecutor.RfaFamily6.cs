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
    /// RFA 族群編輯工具第三批形體：旋轉（rfa_create_revolution）；下一步會在此加入掃掠融合（swept blend）。
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
    }
}
