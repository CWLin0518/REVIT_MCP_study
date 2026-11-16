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
    /// RFA 族群編輯工具第七批：族群文件的外觀相關工具。
    /// 目前為材質（rfa_create_material）；之後的子類別與形體可見性工具也會放在這個檔案。
    ///
    /// 族群裡的形體與材質型族群參數只能引用「已存在於族群檔中」的材質，
    /// 這支工具讓呼叫端先在族群檔內建立（或複製）材質，再交給 rfa_create_extrusion 等工具引用。
    /// </summary>
    public partial class CommandExecutor
    {
        #region rfa_create_material

        /// <summary>讀出材質的著色顏色；顏色無效時回傳 null</summary>
        private static int[] RfaMaterialColor(Material m)
        {
            Color c = m.Color;
            if (c == null || !c.IsValid) return null;
            return new int[] { c.Red, c.Green, c.Blue };
        }

        /// <summary>族群文件中所有材質（依名稱排序）</summary>
        private static List<Material> RfaAllMaterials(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static object RfaDescribeMaterial(Material m, string duplicatedFrom)
        {
            return new
            {
                name = m.Name,
                id = m.Id.GetIdValue(),
                color = RfaMaterialColor(m),
                transparency = m.Transparency,
                materialClass = string.IsNullOrEmpty(m.MaterialClass) ? null : m.MaterialClass,
                duplicatedFrom,
            };
        }

        /// <summary>
        /// 在作用中的族群文件建立材質。每筆可指定顏色、透明度、材質類別，或以既有材質複製。
        /// 同名材質預設略過；updateExisting=true 時改為把顏色／透明度／類別套到既有材質。
        /// 每筆在各自的子交易中執行，單筆失敗只回滾該筆，不影響其他筆。
        /// </summary>
        private object RfaCreateMaterial(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();

            var list = parameters["materials"] as JArray;
            if (list == null || list.Count == 0)
                throw new Exception("請提供 materials 陣列（每個元素含 name，可選 color、transparency、materialClass、duplicateFrom、updateExisting）");

            var created = new List<object>();
            var updated = new List<object>();
            var skipped = new List<object>();
            var errors = new List<object>();

            using (var t = new Transaction(doc, "MCP: rfa_create_material"))
            {
                t.Start();

                foreach (JToken token in list)
                {
                    var p = token as JObject;
                    if (p == null) { errors.Add(new { name = (string)null, error = "每個元素必須是物件" }); continue; }

                    string name = p["name"]?.Type == JTokenType.String ? p["name"].Value<string>()?.Trim() : null;
                    if (string.IsNullOrEmpty(name)) { errors.Add(new { name, error = "缺少 name" }); continue; }

                    // ── 驗證（不碰 Revit） ──
                    Color color = null;
                    var colorTok = p["color"];
                    if (colorTok != null && colorTok.Type != JTokenType.Null)
                    {
                        var arr = colorTok as JArray;
                        if (arr == null || arr.Count != 3 || arr.Any(x => x.Type != JTokenType.Integer && !(x.Type == JTokenType.Float && Math.Abs(x.Value<double>() % 1) < 1e-9)))
                        {
                            errors.Add(new { name, error = "color 需要 [r, g, b] 三個 0–255 的整數" });
                            continue;
                        }
                        var rgb = arr.Select(x => (long)Math.Round(x.Value<double>())).ToArray();
                        if (rgb.Any(v => v < 0 || v > 255))
                        {
                            errors.Add(new { name, error = $"color 數值超出 0–255：[{string.Join(", ", rgb)}]" });
                            continue;
                        }
                        color = new Color((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);
                    }

                    int? transparency = null;
                    var trTok = p["transparency"];
                    if (trTok != null && trTok.Type != JTokenType.Null)
                    {
                        if (trTok.Type != JTokenType.Integer && !(trTok.Type == JTokenType.Float && Math.Abs(trTok.Value<double>() % 1) < 1e-9))
                        {
                            errors.Add(new { name, error = "transparency 需要 0–100 的整數" });
                            continue;
                        }
                        long tv = (long)Math.Round(trTok.Value<double>());
                        if (tv < 0 || tv > 100)
                        {
                            errors.Add(new { name, error = $"transparency 必須介於 0–100，收到: {tv}" });
                            continue;
                        }
                        transparency = (int)tv;
                    }

                    string materialClass = p["materialClass"]?.Type == JTokenType.String ? p["materialClass"].Value<string>() : null;
                    string duplicateFrom = p["duplicateFrom"]?.Type == JTokenType.String ? p["duplicateFrom"].Value<string>()?.Trim() : null;
                    if (string.IsNullOrEmpty(duplicateFrom)) duplicateFrom = null;
                    bool updateExisting = p["updateExisting"]?.Type == JTokenType.Boolean && p["updateExisting"].Value<bool>();

                    // 同名比對：Revit 材質名稱不分大小寫視為重複，IsNameUnique 為準，名稱查找用來拿到既有元素
                    Material existing = RfaAllMaterials(doc)
                        .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                    bool nameTaken = existing != null || !Material.IsNameUnique(doc, name);

                    if (nameTaken && !updateExisting)
                    {
                        skipped.Add(new { name = existing?.Name ?? name, reason = "已有同名材質（如需修改請傳 updateExisting: true）" });
                        continue;
                    }
                    if (nameTaken && existing == null)
                    {
                        errors.Add(new { name, error = "Revit 判定名稱不唯一，但找不到對應的材質元素" });
                        continue;
                    }

                    Material source = null;
                    if (!nameTaken && duplicateFrom != null)
                    {
                        source = RfaAllMaterials(doc).FirstOrDefault(m => m.Name == duplicateFrom)
                                 ?? RfaAllMaterials(doc).FirstOrDefault(m => string.Equals(m.Name, duplicateFrom, StringComparison.OrdinalIgnoreCase));
                        if (source == null)
                        {
                            errors.Add(new { name, error = $"duplicateFrom 指定的材質「{duplicateFrom}」不存在於族群檔中" });
                            continue;
                        }
                    }

                    // ── 寫入（每筆一個子交易，失敗只回滾該筆） ──
                    using (var st = new SubTransaction(doc))
                    {
                        st.Start();
                        try
                        {
                            Material mat;
                            if (nameTaken)
                            {
                                mat = existing;
                            }
                            else if (source != null)
                            {
                                try { mat = source.Duplicate(name); }
                                catch (Exception ex) { throw new Exception($"複製材質「{source.Name}」失敗：{ex.Message}"); }
                                if (mat == null) throw new Exception($"複製材質「{source.Name}」失敗（Revit 回傳 null）");
                            }
                            else
                            {
                                ElementId newId;
                                try { newId = Material.Create(doc, name); }
                                catch (Exception ex) { throw new Exception($"建立材質失敗：{ex.Message}"); }
                                mat = doc.GetElement(newId) as Material
                                      ?? throw new Exception("建立材質失敗（讀不到新材質元素）");
                            }

                            if (color != null) mat.Color = color;
                            if (transparency.HasValue) mat.Transparency = transparency.Value;
                            if (materialClass != null) mat.MaterialClass = materialClass;

                            st.Commit();

                            if (nameTaken)
                                updated.Add(RfaDescribeMaterial(mat, null));
                            else
                                created.Add(RfaDescribeMaterial(mat, source?.Name));
                        }
                        catch (Exception ex)
                        {
                            if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                            errors.Add(new { name, error = ex.Message });
                        }
                    }
                }

                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                    throw new Exception($"rfa_create_material 交易未成功提交（狀態：{status}），所有變更已復原");
            }

            var materialsInDocument = RfaAllMaterials(doc).Select(m => m.Name).ToList();

            return new
            {
                createdCount = created.Count,
                created,
                updated,
                skipped,
                errors,
                materialsInDocument,
            };
        }

        #endregion
    }
}
