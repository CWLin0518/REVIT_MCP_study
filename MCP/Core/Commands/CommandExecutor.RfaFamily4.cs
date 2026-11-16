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
                var fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
                t.SetFailureHandlingOptions(fho);
                t.Start();

                SketchPlane sp = SketchPlane.Create(doc, hostRp.Id);
                sc = doc.FamilyCreate.NewSymbolicCurve(curve, sp) ?? throw new Exception("建立符號線失敗（Revit 回傳 null）");
                if (style != null) sc.Subcategory = style;
                doc.Regenerate();

                RfaConstrainCurve(doc, fm, sc, parameters["constraints"] as JArray, "符號線", results, warnings);

                if (t.Commit() != TransactionStatus.Committed)
                    throw new Exception("Revit 拒絕了符號線或其約束（交易被復原，可能是過度約束）");
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

        #endregion

        #region rfa_save_as

        private object RfaSaveAs(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();

            string path = parameters["filePath"]?.Value<string>()?.Trim();
            if (string.IsNullOrEmpty(path)) throw new Exception("請提供 filePath（.rfa 的完整路徑）");
            if (!Path.IsPathRooted(path)) throw new Exception($"filePath 必須是完整路徑，收到: {path}");
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) path += ".rfa";
            else if (!ext.Equals(".rfa", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"副檔名必須是 .rfa，收到: {ext}");
            path = Path.GetFullPath(path);

            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                throw new Exception($"資料夾不存在: {dir}（工具不會自動建立資料夾）");

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
