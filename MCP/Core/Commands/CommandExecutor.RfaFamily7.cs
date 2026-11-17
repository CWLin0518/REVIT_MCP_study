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
    /// - rfa_create_material：族群裡的形體與材質型族群參數只能引用「已存在於族群檔中」的材質，
    ///   這支工具讓呼叫端先在族群檔內建立（或複製）材質，再交給 rfa_create_extrusion 等工具引用。
    /// - rfa_set_form_visibility：建立／指定族群類別的子類別（含線色、線寬、材質），
    ///   並設定形體的「族群元素可見性設定」（詳細程度與視圖方向）。
    ///   rfa_get_document_info 的 forms 清單也透過這裡的共用 helper 讀出子類別與可見性。
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

        #region rfa_set_form_visibility

        // 工具輸入鍵 ↔ FamilyElementVisibility 屬性
        private static readonly string[] RfaVisibilityKeys =
            { "coarse", "medium", "fine", "planRcp", "frontBack", "leftRight", "whenCutInPlanRcp" };

        private static bool RfaGetVisFlag(FamilyElementVisibility v, string key)
        {
            switch (key)
            {
                case "coarse": return v.IsShownInCoarse;
                case "medium": return v.IsShownInMedium;
                case "fine": return v.IsShownInFine;
                case "planRcp": return v.IsShownInTopBottom;
                case "frontBack": return v.IsShownInFrontBack;
                case "leftRight": return v.IsShownInLeftRight;
                case "whenCutInPlanRcp": return v.IsShownInPlanRCPCut;
                default: throw new Exception($"未知的可見性鍵: {key}");
            }
        }

        private static void RfaSetVisFlag(FamilyElementVisibility v, string key, bool value)
        {
            switch (key)
            {
                case "coarse": v.IsShownInCoarse = value; break;
                case "medium": v.IsShownInMedium = value; break;
                case "fine": v.IsShownInFine = value; break;
                case "planRcp": v.IsShownInTopBottom = value; break;
                case "frontBack": v.IsShownInFrontBack = value; break;
                case "leftRight": v.IsShownInLeftRight = value; break;
                case "whenCutInPlanRcp": v.IsShownInPlanRCPCut = value; break;
                default: throw new Exception($"未知的可見性鍵: {key}");
            }
        }

        /// <summary>
        /// 讀出形體的族群元素可見性設定（七個布林）；形體沒有可見性設定或讀取失敗時回傳 null。
        /// rfa_get_document_info 與 rfa_set_form_visibility 共用。
        /// </summary>
        private static Dictionary<string, bool> RfaReadFormVisibility(GenericForm form)
        {
            try
            {
                FamilyElementVisibility v = form.GetVisibility();
                if (v == null) return null;
                var d = new Dictionary<string, bool>();
                foreach (string k in RfaVisibilityKeys) d[k] = RfaGetVisFlag(v, k);
                return d;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>形體所屬子類別名稱；位於族群類別本身（未指定子類別）時回傳 null</summary>
        private static string RfaFormSubcategoryName(Document doc, GenericForm form)
        {
            try
            {
                Category sub = form.Subcategory;
                if (sub == null) return null;
                Category famCat = doc.OwnerFamily?.FamilyCategory;
                if (famCat != null && sub.Id == famCat.Id) return null;
                return sub.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 清除形體的子類別（回到族群類別本身）。依序嘗試，每種方法在各自的巢狀子交易中執行，
        /// 第一個「成功且讀回為未指定子類別」者保留並回傳其名稱；全部失敗時丟出列出所有嘗試結果的例外。
        /// ① FAMILY_ELEM_SUBCATEGORY 參數設為 InvalidElementId；② Subcategory = null；③ Subcategory = 族群類別。
        /// </summary>
        private static string RfaClearFormSubcategory(Document doc, GenericForm form, Category famCat)
        {
            if (RfaFormSubcategoryName(doc, form) == null) return "alreadyClear";

            var attempts = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("FAMILY_ELEM_SUBCATEGORY=InvalidElementId", () =>
                {
                    Parameter p = form.get_Parameter(BuiltInParameter.FAMILY_ELEM_SUBCATEGORY);
                    if (p == null) throw new Exception("形體沒有 FAMILY_ELEM_SUBCATEGORY 參數");
                    if (p.IsReadOnly) throw new Exception("FAMILY_ELEM_SUBCATEGORY 參數為唯讀");
                    if (!p.Set(ElementId.InvalidElementId)) throw new Exception("Parameter.Set 回傳 false");
                }),
                new KeyValuePair<string, Action>("Subcategory=null", () => { form.Subcategory = null; }),
                new KeyValuePair<string, Action>("Subcategory=familyCategory", () => { form.Subcategory = famCat; }),
            };

            var tried = new List<string>();
            foreach (var a in attempts)
            {
                using (var st = new SubTransaction(doc))
                {
                    st.Start();
                    try
                    {
                        a.Value();
                        string after = RfaFormSubcategoryName(doc, form);
                        if (after == null)
                        {
                            st.Commit();
                            return a.Key;
                        }
                        tried.Add($"{a.Key}：未報錯，但讀回仍為「{after}」");
                    }
                    catch (Exception ex)
                    {
                        tried.Add($"{a.Key}：{ex.Message}");
                    }
                    if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                }
            }
            throw new Exception($"清除子類別失敗，已嘗試：{string.Join("；", tried)}");
        }

        /// <summary>族群類別的子類別（依名稱排序）</summary>
        private static List<Category> RfaFamilySubcategories(Document doc)
        {
            Category famCat = doc.OwnerFamily?.FamilyCategory;
            if (famCat == null) return new List<Category>();
            return famCat.SubCategories.Cast<Category>()
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static object RfaDescribeSubcategoryStyle(Category c, bool modifiedExisting)
        {
            int[] color = null;
            try
            {
                Color lc = c.LineColor;
                if (lc != null && lc.IsValid) color = new int[] { lc.Red, lc.Green, lc.Blue };
            }
            catch { }
            int? proj = null, cut = null;
            try { proj = c.GetLineWeight(GraphicsStyleType.Projection); } catch { }
            try { cut = c.GetLineWeight(GraphicsStyleType.Cut); } catch { }
            string mat = null;
            try { mat = c.Material?.Name; } catch { }
            return new
            {
                lineColor = color,
                projectionLineWeight = proj,
                cutLineWeight = cut,
                material = mat,
                modifiedExisting,
            };
        }

        private static bool RfaIsWholeNumber(JToken t) =>
            t.Type == JTokenType.Integer || (t.Type == JTokenType.Float && Math.Abs(t.Value<double>() % 1) < 1e-9);

        private static int? RfaReadLineWeight(JObject style, string key)
        {
            var tok = style[key];
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (!RfaIsWholeNumber(tok)) throw new Exception($"subcategoryStyle.{key} 需要 1–16 的整數");
            long v = (long)Math.Round(tok.Value<double>());
            if (v < 1 || v > 16) throw new Exception($"subcategoryStyle.{key} 必須介於 1–16，收到: {v}");
            return (int)v;
        }

        /// <summary>
        /// 指定形體的子類別（不存在時可建立，並可設定線色／線寬／材質），
        /// 以及族群元素可見性設定（詳細程度：粗略／中等／精細；視圖：平面/天花、前/後、左/右、平面/天花被剖切時）。
        /// 未給的可見性欄位保留形體目前的值。每個元素在各自的子交易中執行，單筆失敗只回滾該筆。
        /// </summary>
        private object RfaSetFormVisibility(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            Category famCat = doc.OwnerFamily?.FamilyCategory ?? throw new Exception("讀不到族群類別");

            // ── 讀取並驗證輸入（不碰 Revit） ──
            var idsTok = parameters["elementIds"] as JArray;
            if (idsTok == null)
                throw new Exception("請提供 elementIds 陣列（族群檔中形體的元素 ID）");
            var ids = new List<long>();
            foreach (var t in idsTok)
            {
                if (!RfaIsWholeNumber(t)) throw new Exception($"elementIds 只接受整數，收到: {t}");
                ids.Add((long)Math.Round(t.Value<double>()));
            }

            var subTok = parameters["subcategory"];
            bool hasSub = subTok != null && subTok.Type != JTokenType.Null;
            if (hasSub && subTok.Type != JTokenType.String) throw new Exception("subcategory 需要字串（空字串 = 清除子類別，回到族群類別本身）");
            string subName = hasSub ? (subTok.Value<string>() ?? string.Empty).Trim() : null;
            bool clearSub = hasSub && subName.Length == 0;

            var createTok = parameters["createSubcategory"];
            bool createSub = createTok == null || createTok.Type == JTokenType.Null || createTok.Value<bool>();

            var styleTok = parameters["subcategoryStyle"];
            JObject style = null;
            if (styleTok != null && styleTok.Type != JTokenType.Null)
            {
                style = styleTok as JObject ?? throw new Exception("subcategoryStyle 需要物件");
                var unknown = style.Properties().Select(p => p.Name)
                    .Where(n => n != "lineColor" && n != "projectionLineWeight" && n != "cutLineWeight" && n != "material").ToList();
                if (unknown.Count > 0)
                    throw new Exception($"subcategoryStyle 不支援的欄位: {string.Join(", ", unknown)}（可用: lineColor, projectionLineWeight, cutLineWeight, material）");
            }

            var visTok = parameters["visibility"];
            var visChanges = new Dictionary<string, bool>();
            if (visTok != null && visTok.Type != JTokenType.Null)
            {
                var visObj = visTok as JObject ?? throw new Exception("visibility 需要物件");
                foreach (var prop in visObj.Properties())
                {
                    if (!RfaVisibilityKeys.Contains(prop.Name))
                        throw new Exception($"visibility 不支援的欄位: {prop.Name}（可用: {string.Join(", ", RfaVisibilityKeys)}）");
                    if (prop.Value.Type != JTokenType.Boolean)
                        throw new Exception($"visibility.{prop.Name} 需要布林值");
                    visChanges[prop.Name] = prop.Value.Value<bool>();
                }
            }

            if (!hasSub && style == null && visChanges.Count == 0)
                throw new Exception("請至少提供 subcategory、visibility、subcategoryStyle 其中之一");
            if (style != null && (!hasSub || clearSub))
                throw new Exception("subcategoryStyle 需要搭配非空的 subcategory 名稱（樣式是套在子類別上）");

            Color lineColor = null;
            int? projLw = null, cutLw = null;
            Material styleMat = null;
            if (style != null)
            {
                var colorTok = style["lineColor"];
                if (colorTok != null && colorTok.Type != JTokenType.Null)
                {
                    var arr = colorTok as JArray;
                    if (arr == null || arr.Count != 3 || arr.Any(x => !RfaIsWholeNumber(x)))
                        throw new Exception("subcategoryStyle.lineColor 需要 [r, g, b] 三個 0–255 的整數");
                    var rgb = arr.Select(x => (long)Math.Round(x.Value<double>())).ToArray();
                    if (rgb.Any(v => v < 0 || v > 255))
                        throw new Exception($"subcategoryStyle.lineColor 數值超出 0–255：[{string.Join(", ", rgb)}]");
                    lineColor = new Color((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);
                }
                projLw = RfaReadLineWeight(style, "projectionLineWeight");
                cutLw = RfaReadLineWeight(style, "cutLineWeight");

                var matTok = style["material"];
                if (matTok != null && matTok.Type != JTokenType.Null)
                {
                    if (matTok.Type != JTokenType.String) throw new Exception("subcategoryStyle.material 需要材質名稱字串");
                    string matName = matTok.Value<string>().Trim();
                    var all = RfaAllMaterials(doc);
                    styleMat = all.FirstOrDefault(m => m.Name == matName)
                               ?? all.FirstOrDefault(m => string.Equals(m.Name, matName, StringComparison.OrdinalIgnoreCase));
                    if (styleMat == null)
                        throw new Exception($"族群檔內找不到材質「{matName}」。請先用 rfa_create_material 建立。可用: {string.Join(", ", all.Select(m => m.Name))}");
                }
            }

            // 既有子類別查找（不分大小寫，與 RfaFindSubcategoryStyle 一致）
            Category targetSub = null;
            bool subExisted = false;
            if (hasSub && !clearSub)
            {
                targetSub = RfaFamilySubcategories(doc)
                    .FirstOrDefault(c => string.Equals(c.Name, subName, StringComparison.OrdinalIgnoreCase));
                subExisted = targetSub != null;
                if (!subExisted && !createSub)
                    throw new Exception($"族群類別「{famCat.Name}」沒有子類別「{subName}」（createSubcategory 為 false，不會自動建立）。可用: {string.Join(", ", RfaFamilySubcategories(doc).Select(c => c.Name))}");
            }

            var updated = new List<object>();
            var errors = new List<object>();
            var warnings = new List<string>();
            object subResult = null;
            int clearedCount = 0;

            using (var t = new Transaction(doc, "MCP: rfa_set_form_visibility"))
            {
                t.Start();

                // ── 子類別：建立一次並套用樣式 ──
                if (hasSub && !clearSub)
                {
                    if (targetSub == null)
                    {
                        try { targetSub = doc.Settings.Categories.NewSubcategory(famCat, subName); }
                        catch (Exception ex) { throw new Exception($"在族群類別「{famCat.Name}」下建立子類別「{subName}」失敗：{ex.Message}"); }
                        if (targetSub == null) throw new Exception($"建立子類別「{subName}」失敗（Revit 回傳 null）");
                    }

                    if (style != null)
                    {
                        if (subExisted)
                            warnings.Add($"subcategoryStyle 修改了既有子類別「{targetSub.Name}」，所有使用該子類別的元素都會跟著改變");
                        if (lineColor != null)
                        {
                            try { targetSub.LineColor = lineColor; }
                            catch (Exception ex) { warnings.Add($"設定線色失敗：{ex.Message}"); }
                        }
                        if (projLw.HasValue)
                        {
                            try { targetSub.SetLineWeight(projLw.Value, GraphicsStyleType.Projection); }
                            catch (Exception ex) { warnings.Add($"設定投影線寬失敗：{ex.Message}"); }
                        }
                        if (cutLw.HasValue)
                        {
                            try { targetSub.SetLineWeight(cutLw.Value, GraphicsStyleType.Cut); }
                            catch (Exception ex) { warnings.Add($"設定剖切線寬失敗（類別「{famCat.Name}」可能不可剖切）：{ex.Message}"); }
                        }
                        if (styleMat != null)
                        {
                            try { targetSub.Material = styleMat; }
                            catch (Exception ex) { warnings.Add($"設定子類別材質失敗：{ex.Message}"); }
                        }
                    }
                }

                // ── 逐一處理形體（每筆一個子交易） ──
                foreach (long rawId in ids)
                {
                    GenericForm form = doc.GetElement(((IdType)rawId).ToElementId()) as GenericForm;
                    if (form == null)
                    {
                        Element e = doc.GetElement(((IdType)rawId).ToElementId());
                        errors.Add(new { id = rawId, error = e == null ? "找不到元素" : $"元素不是形體（GenericForm），而是 {e.GetType().Name}" });
                        continue;
                    }

                    using (var st = new SubTransaction(doc))
                    {
                        st.Start();
                        try
                        {
                            string clearMethod = null;
                            if (clearSub)
                            {
                                clearMethod = RfaClearFormSubcategory(doc, form, famCat);
                            }
                            else if (hasSub)
                            {
                                try { form.Subcategory = targetSub; }
                                catch (Exception ex) { throw new Exception($"指定子類別失敗：{ex.Message}"); }
                            }

                            if (visChanges.Count > 0)
                            {
                                FamilyElementVisibility vis;
                                try { vis = form.GetVisibility(); }
                                catch (Exception ex) { throw new Exception($"讀取可見性設定失敗：{ex.Message}"); }
                                if (vis == null) throw new Exception("此形體沒有可見性設定（GetVisibility 回傳 null）");
                                foreach (var kv in visChanges) RfaSetVisFlag(vis, kv.Key, kv.Value);
                                try { form.SetVisibility(vis); }
                                catch (Exception ex) { throw new Exception($"設定可見性失敗：{ex.Message}"); }
                            }

                            st.Commit();

                            var readBack = RfaReadFormVisibility(form);
                            if (readBack != null)
                            {
                                foreach (var kv in visChanges)
                                    if (readBack.TryGetValue(kv.Key, out bool actual) && actual != kv.Value)
                                        warnings.Add($"id {rawId}：{kv.Key} 要求 {kv.Value.ToString().ToLowerInvariant()}，Revit 讀回 {actual.ToString().ToLowerInvariant()}（此類別可能不支援該設定）");
                            }

                            string actualSub = RfaFormSubcategoryName(doc, form);
                            if (hasSub && !clearSub && !string.Equals(actualSub, targetSub.Name, StringComparison.OrdinalIgnoreCase))
                                warnings.Add($"id {rawId}：子類別讀回為「{actualSub ?? "(族群類別本身)"}」，與要求的「{targetSub.Name}」不同");
                            if (clearSub && actualSub != null)
                                warnings.Add($"id {rawId}：清除子類別後讀回仍為「{actualSub}」");

                            if (clearMethod == "alreadyClear")
                                warnings.Add($"id {rawId}：形體原本就沒有指定子類別，未變更");
                            else if (clearMethod != null && actualSub == null)
                                clearedCount++;

                            updated.Add(new
                            {
                                id = rawId,
                                kind = form.GetType().Name,
                                isSolid = form.IsSolid,
                                subcategory = actualSub,
                                visibility = readBack,
                                clearMethod,
                            });
                        }
                        catch (Exception ex)
                        {
                            if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                            errors.Add(new { id = rawId, error = ex.Message });
                        }
                    }
                }

                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                    throw new Exception($"rfa_set_form_visibility 交易未成功提交（狀態：{status}），所有變更已復原");
            }

            if (hasSub && !clearSub && targetSub != null)
            {
                subResult = new
                {
                    name = targetSub.Name,
                    id = targetSub.Id.GetIdValue(),
                    created = !subExisted,
                    styleApplied = style != null ? RfaDescribeSubcategoryStyle(targetSub, subExisted) : null,
                };
            }

            return new
            {
                subcategory = subResult,
                subcategoryCleared = clearSub && clearedCount > 0,
                updated,
                errors,
                availableSubcategories = RfaFamilySubcategories(doc).Select(c => c.Name).ToList(),
                warnings,
            };
        }

        #endregion
    }
}
