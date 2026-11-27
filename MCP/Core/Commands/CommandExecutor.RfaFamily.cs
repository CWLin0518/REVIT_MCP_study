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
    /// RFA 族群編輯工具（rfa_*）：直接操作「目前作用中的族群文件」。
    /// 使用者先在 Revit 以樣板開好 .rfa（族群編輯器），這些指令再於其中建參考平面、擠出、參數與類型。
    /// 只用 Revit 2022 起就存在的 API（ForgeTypeId 版 AddParameter、SpecTypeId、GroupTypeId），
    /// 所以 R22–R27 共用同一份程式碼，不需要版本分支。
    /// 所有長度以 mm 輸入／輸出；角度以度；面積 m²；體積 m³。
    /// </summary>
    public partial class CommandExecutor
    {
        #region 共用

        private Document GetActiveFamilyDocument()
        {
            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                throw new Exception("Revit 目前沒有開啟任何文件。請先以族群樣板開啟或新建一個 .rfa。");
            if (!doc.IsFamilyDocument)
                throw new Exception($"目前作用中的文件「{doc.Title}」是專案檔，不是族群檔。rfa_* 工具只能在族群編輯器中使用，請先切換到要編輯的 .rfa 視窗，或用 rfa_new_family（從樣板新建）／rfa_open_family（開啟既有 .rfa）讓族群檔成為作用中文件。");
            return doc;
        }

        private static double RfaMm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        private static double RfaToMm(double ft) => Math.Round(UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters), 3);

        private static object RfaPt(XYZ p) => new[] { RfaToMm(p.X), RfaToMm(p.Y), RfaToMm(p.Z) };
        private static object RfaVec(XYZ v) => new[] { Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6) };

        private static XYZ RfaReadXyzMm(JToken token, string label)
        {
            var arr = token as JArray;
            if (arr == null || arr.Count != 3)
                throw new Exception($"{label} 需要 [x, y, z]（mm）");
            return new XYZ(RfaMm(arr[0].Value<double>()), RfaMm(arr[1].Value<double>()), RfaMm(arr[2].Value<double>()));
        }

        private static XYZ RfaReadVector(JToken token, string label)
        {
            var arr = token as JArray;
            if (arr == null || arr.Count != 3)
                throw new Exception($"{label} 需要 [x, y, z] 方向向量");
            var v = new XYZ(arr[0].Value<double>(), arr[1].Value<double>(), arr[2].Value<double>());
            if (v.IsZeroLength()) throw new Exception($"{label} 不可為零向量");
            return v.Normalize();
        }

        // 參數型別：工具輸入用的短名稱 ↔ Revit SpecTypeId
        private static readonly (string Key, ForgeTypeId Spec)[] RfaSpecMap =
        {
            ("length", SpecTypeId.Length),
            ("number", SpecTypeId.Number),
            ("integer", SpecTypeId.Int.Integer),
            ("yes_no", SpecTypeId.Boolean.YesNo),
            ("text", SpecTypeId.String.Text),
            ("angle", SpecTypeId.Angle),
            ("area", SpecTypeId.Area),
            ("volume", SpecTypeId.Volume),
            ("material", SpecTypeId.Reference.Material),
        };

        private static string RfaSpecName(ForgeTypeId spec)
        {
            if (spec == null) return null;
            foreach (var (key, s) in RfaSpecMap)
                if (s == spec) return key;
            return spec.TypeId;
        }

        private static ForgeTypeId RfaGroupFromName(string name)
        {
            switch ((name ?? "dimensions").Trim().ToLowerInvariant())
            {
                case "dimensions":
                case "geometry": return GroupTypeId.Geometry;
                case "constraints": return GroupTypeId.Constraints;
                case "materials": return GroupTypeId.Materials;
                case "identity_data": return GroupTypeId.IdentityData;
                case "data": return GroupTypeId.Data;
                case "text": return GroupTypeId.Text;
                case "graphics": return GroupTypeId.Graphics;
                case "construction": return GroupTypeId.Construction;
                case "visibility": return GroupTypeId.Visibility;
                // 「其他」群組在 ForgeTypeId 版 API 以空 TypeId 表示
                case "other": return new ForgeTypeId(string.Empty);
                default:
                    throw new Exception($"不支援的參數群組: {name}（可用: dimensions, constraints, materials, identity_data, data, text, graphics, construction, visibility, other）");
            }
        }

        private static FamilyParameter RfaFindParam(FamilyManager fm, string name)
        {
            foreach (FamilyParameter p in fm.Parameters)
                if (p.Definition.Name == name) return p;
            return null;
        }

        /// <summary>讀出某類型的參數值（換成 mm / 度 / m² / m³ / 材質名稱）</summary>
        private static object RfaReadValue(Document doc, FamilyType ft, FamilyParameter fp)
        {
            if (ft == null || !ft.HasValue(fp)) return null;
            ForgeTypeId spec = fp.Definition.GetDataType();
            switch (fp.StorageType)
            {
                case StorageType.Double:
                    double d = ft.AsDouble(fp) ?? 0;
                    if (spec == SpecTypeId.Length) return RfaToMm(d);
                    if (spec == SpecTypeId.Angle) return Math.Round(UnitUtils.ConvertFromInternalUnits(d, UnitTypeId.Degrees), 6);
                    if (spec == SpecTypeId.Area) return Math.Round(UnitUtils.ConvertFromInternalUnits(d, UnitTypeId.SquareMeters), 6);
                    if (spec == SpecTypeId.Volume) return Math.Round(UnitUtils.ConvertFromInternalUnits(d, UnitTypeId.CubicMeters), 6);
                    return d;
                case StorageType.Integer:
                    int i = ft.AsInteger(fp) ?? 0;
                    if (spec == SpecTypeId.Boolean.YesNo) return i != 0;
                    return i;
                case StorageType.String:
                    return ft.AsString(fp);
                case StorageType.ElementId:
                    ElementId id = ft.AsElementId(fp);
                    if (id == null || id == ElementId.InvalidElementId) return null;
                    Element e = doc.GetElement(id);
                    return e != null ? (object)e.Name : id.GetIdValue();
                default:
                    return ft.AsValueString(fp);
            }
        }

        /// <summary>依參數型別把 JSON 值寫進目前類型（mm / 度 / m² / m³ / bool / 材質名稱）</summary>
        private static void RfaSetValue(Document doc, FamilyManager fm, FamilyParameter fp, JToken value)
        {
            ForgeTypeId spec = fp.Definition.GetDataType();
            switch (fp.StorageType)
            {
                case StorageType.Double:
                    double d = value.Value<double>();
                    if (spec == SpecTypeId.Length) d = RfaMm(d);
                    else if (spec == SpecTypeId.Angle) d = UnitUtils.ConvertToInternalUnits(d, UnitTypeId.Degrees);
                    else if (spec == SpecTypeId.Area) d = UnitUtils.ConvertToInternalUnits(d, UnitTypeId.SquareMeters);
                    else if (spec == SpecTypeId.Volume) d = UnitUtils.ConvertToInternalUnits(d, UnitTypeId.CubicMeters);
                    fm.Set(fp, d);
                    break;
                case StorageType.Integer:
                    if (value.Type == JTokenType.Boolean) fm.Set(fp, value.Value<bool>() ? 1 : 0);
                    else fm.Set(fp, value.Value<int>());
                    break;
                case StorageType.String:
                    fm.Set(fp, value.Value<string>() ?? string.Empty);
                    break;
                case StorageType.ElementId:
                    ElementId id;
                    if (value.Type == JTokenType.Integer)
                    {
                        id = ((IdType)value.Value<long>()).ToElementId();
                    }
                    else
                    {
                        string matName = value.Value<string>();
                        var mat = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                            .FirstOrDefault(m => m.Name == matName);
                        if (mat == null)
                            throw new Exception($"族群檔內找不到材質「{matName}」（族群只能用已存在於族群檔中的材質）");
                        id = mat.Id;
                    }
                    fm.Set(fp, id);
                    break;
                default:
                    throw new Exception($"不支援的參數儲存型別: {fp.StorageType}");
            }
        }

        private static object RfaDescribeParam(Document doc, FamilyManager fm, FamilyParameter fp)
        {
            string group;
            try { group = fp.Definition.GetGroupTypeId()?.TypeId; } catch { group = null; }
            return new
            {
                name = fp.Definition.Name,
                id = fp.Id.GetIdValue(),
                type = RfaSpecName(fp.Definition.GetDataType()),
                group,
                isInstance = fp.IsInstance,
                isShared = fp.IsShared,
                formula = string.IsNullOrEmpty(fp.Formula) ? null : fp.Formula,
                currentValue = RfaReadValue(doc, fm.CurrentType, fp),
            };
        }

        /// <summary>形體的模型包圍盒（mm）；讀不到時回傳 null</summary>
        private static object RfaFormBoundingBoxMm(Element e)
        {
            try
            {
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null || bb.Min == null || bb.Max == null) return null;
                return new { min = RfaPt(bb.Min), max = RfaPt(bb.Max) };
            }
            catch
            {
                return null;
            }
        }

        private static double? RfaTryAngleDeg(Func<double> read)
        {
            try { return Math.Round(UnitUtils.ConvertFromInternalUnits(read(), UnitTypeId.Degrees), 6); }
            catch { return null; }
        }

        /// <summary>
        /// 交易失敗預處理器：Warning 記錄後刪除；有任何 Error 時記錄訊息並回傳 ProceedWithRollBack，
        /// 讓交易靜默復原（不跳 Revit 對話框），呼叫端以 Commit 狀態判斷是否被復原。
        /// （SilentFailuresPreprocessor 遇 Error 只記 log 並 Continue，Error 仍會交給 Revit UI 跳出對話框。）
        /// </summary>
        private sealed class RfaRollbackOnErrorPreprocessor : IFailuresPreprocessor
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool hasError = false;
                foreach (FailureMessageAccessor msg in accessor.GetFailureMessages())
                {
                    string text;
                    try { text = msg.GetDescriptionText(); } catch { text = "(無法讀取 Revit 失敗訊息)"; }
                    FailureSeverity sev;
                    try { sev = msg.GetSeverity(); } catch { sev = FailureSeverity.Error; }
                    if (sev == FailureSeverity.None) continue;
                    if (sev == FailureSeverity.Warning)
                    {
                        if (!Warnings.Contains(text)) Warnings.Add(text);
                        try { accessor.DeleteWarning(msg); } catch { }
                    }
                    else
                    {
                        if (!Errors.Contains(text)) Errors.Add(text);
                        hasError = true;
                    }
                }
                if (hasError)
                {
                    Logger.Error($"[RfaRollbackOnErrorPreprocessor] 交易「{accessor.GetTransactionName()}」有錯誤，已復原：{string.Join(" | ", Errors)}");
                    return FailureProcessingResult.ProceedWithRollBack;
                }
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>為交易套用 RfaRollbackOnErrorPreprocessor（交易尚未 Start 前呼叫），回傳該預處理器以讀取錯誤</summary>
        private static RfaRollbackOnErrorPreprocessor RfaUseRollbackOnError(Transaction t)
        {
            var failures = new RfaRollbackOnErrorPreprocessor();
            var fho = t.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(failures);
            fho.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(fho);
            return failures;
        }

        /// <summary>把預處理器記錄到的 Revit 錯誤訊息組成「：訊息」字串；沒有則回傳空字串</summary>
        private static string RfaFailureText(RfaRollbackOnErrorPreprocessor failures)
        {
            return failures != null && failures.Errors.Count > 0 ? "：" + string.Join("；", failures.Errors) : string.Empty;
        }

        /// <summary>
        /// 撓曲自檢共用流程：在一個「一定復原」的交易群組內，以 RfaRollbackOnErrorPreprocessor 提交試驗值
        /// （Revit 若判定約束無法滿足，交易靜默復原、不跳對話框，並視為撓曲失敗、附上 Revit 訊息）；
        /// 提交成功後呼叫 measure 量測（只讀），最後整個群組 RollBack，試驗值絕不保留。
        /// 回傳 measure 的結果，或以 "FAILED" 開頭的失敗說明。
        /// </summary>
        private static string RfaRunFlexTrial(Document doc, string name, Action apply, Func<string> measure)
        {
            using (var tg = new TransactionGroup(doc, name))
            {
                try
                {
                    tg.Start();
                    using (var t = new Transaction(doc, name))
                    {
                        var failures = RfaUseRollbackOnError(t);
                        t.Start();
                        try
                        {
                            apply();
                        }
                        catch (Exception ex)
                        {
                            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                            return $"FAILED：設定試驗值時發生錯誤：{ex.Message}";
                        }
                        TransactionStatus st = t.Commit();
                        if (st != TransactionStatus.Committed || failures.Errors.Count > 0)
                            return $"FAILED：Revit 無法套用試驗值，交易已復原（{st}）{RfaFailureText(failures)}";
                    }
                    return measure();
                }
                catch (Exception ex)
                {
                    return $"FAILED：{ex.Message}";
                }
                finally
                {
                    try { if (tg.HasStarted() && !tg.HasEnded()) tg.RollBack(); }
                    catch (Exception ex) { Logger.Error($"[RfaRunFlexTrial] 交易群組「{name}」復原失敗：{ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// 比對類型中參數的實際值與要求值（長度容差 0.01 mm、角度 0.001°）。
        /// actual 為讀回的值（與 RfaReadValue 相同單位）。
        /// </summary>
        private static bool RfaValueMatches(Document doc, FamilyType ft, FamilyParameter fp, JToken requested, out object actual)
        {
            actual = RfaReadValue(doc, ft, fp);
            if (requested == null || requested.Type == JTokenType.Null) return actual == null;
            if (actual == null) return false;
            try
            {
                ForgeTypeId spec = fp.Definition.GetDataType();
                switch (fp.StorageType)
                {
                    case StorageType.Double:
                    {
                        double want = requested.Value<double>();
                        double got = Convert.ToDouble(actual);
                        double tol = spec == SpecTypeId.Length ? 0.01
                                   : spec == SpecTypeId.Angle ? 0.001
                                   : 1e-5 * Math.Max(1.0, Math.Abs(want));
                        return Math.Abs(got - want) <= tol;
                    }
                    case StorageType.Integer:
                        if (actual is bool b)
                        {
                            bool wantB = requested.Type == JTokenType.Boolean ? requested.Value<bool>() : requested.Value<long>() != 0;
                            return b == wantB;
                        }
                        return Convert.ToInt64(actual) == (requested.Type == JTokenType.Boolean ? (requested.Value<bool>() ? 1 : 0) : requested.Value<long>());
                    case StorageType.String:
                        return string.Equals(Convert.ToString(actual), requested.Value<string>() ?? string.Empty, StringComparison.Ordinal);
                    case StorageType.ElementId:
                        if (requested.Type == JTokenType.Integer)
                        {
                            ElementId id = ft.AsElementId(fp);
                            return id != null && id.GetIdValue() == (IdType)requested.Value<long>();
                        }
                        return string.Equals(Convert.ToString(actual), requested.Value<string>(), StringComparison.Ordinal);
                    default:
                        return string.Equals(Convert.ToString(actual), requested.ToString(), StringComparison.Ordinal);
                }
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region rfa_get_document_info

        private object RfaGetDocumentInfo(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;
            Family fam = doc.OwnerFamily;

            var refPlanes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Select(rp =>
                {
                    Plane pl = rp.GetPlane();
                    return new
                    {
                        id = rp.Id.GetIdValue(),
                        name = rp.Name,
                        normal = RfaVec(rp.Normal),
                        origin = RfaPt(pl.Origin),
                        bubbleEnd = RfaPt(rp.BubbleEnd),
                        freeEnd = RfaPt(rp.FreeEnd),
                    };
                }).ToList();

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => new { id = l.Id.GetIdValue(), name = l.Name, elevation = RfaToMm(l.Elevation) }).ToList();

            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate)
                .Select(v =>
                {
                    object dir = null;
                    try { dir = RfaVec(v.ViewDirection); } catch { }
                    return new { id = v.Id.GetIdValue(), name = v.Name, viewType = v.ViewType.ToString(), viewDirection = dir };
                }).ToList();

            var parameters_ = fm.Parameters.Cast<FamilyParameter>()
                .OrderBy(p => p.Definition.Name)
                .Select(p => RfaDescribeParam(doc, fm, p)).ToList();

            var types = fm.Types.Cast<FamilyType>().Select(t => t.Name).ToList();

            var forms = new FilteredElementCollector(doc).OfClass(typeof(GenericForm)).Cast<GenericForm>()
                .Select(f =>
                {
                    var d = new Dictionary<string, object>
                    {
                        ["id"] = f.Id.GetIdValue(),
                        ["kind"] = f.GetType().Name,
                        ["isSolid"] = f.IsSolid,
                        ["name"] = f.Name,
                        ["subcategory"] = RfaFormSubcategoryName(doc, f),
                        ["visibility"] = RfaReadFormVisibility(f),
                        // 幾何讀回：撓曲參數後可比對形體是否跟著變（與 rfa_create_revolution 相同，用 get_BoundingBox(null)）
                        ["boundingBoxMm"] = RfaFormBoundingBoxMm(f),
                    };
                    if (f is Revolution rv)
                    {
                        d["startAngleDeg"] = RfaTryAngleDeg(() => rv.StartAngle);
                        d["endAngleDeg"] = RfaTryAngleDeg(() => rv.EndAngle);
                    }
                    return d;
                }).ToList();

            // 族群類別的子類別（rfa_set_form_visibility 可建立／指定）
            var subcategories = RfaFamilySubcategories(doc)
                .Select(c => new { id = c.Id.GetIdValue(), name = c.Name }).ToList();

            // 族群內可被形體／材質參數引用的材質（只能引用已存在於族群檔的材質）
            var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new { id = m.Id.GetIdValue(), name = m.Name }).ToList();

            // 已載入的巢狀族群（可編輯、非內建、不含族群檔自己）與各自的類型、實體數
            IdType ownerId = fam?.Id.GetIdValue() ?? (IdType)(-1);
            var instanceCounts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => fi.Symbol?.Family != null)
                .GroupBy(fi => fi.Symbol.Family.Id.GetIdValue())
                .ToDictionary(g => g.Key, g => g.Count());
            var nestedFamilies = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => f.Id.GetIdValue() != ownerId && f.IsEditable && !f.IsInPlace)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new
                {
                    id = f.Id.GetIdValue(),
                    name = f.Name,
                    category = f.FamilyCategory?.Name,
                    types = f.GetFamilySymbolIds().Select(id => doc.GetElement(id)?.Name).Where(n => n != null)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    instanceCount = instanceCounts.TryGetValue(f.Id.GetIdValue(), out int c) ? c : 0,
                }).ToList();

            // MEP 接頭（rfa_create_connector 建立的或樣板內建的）；各欄位讀不到時為 null（CommandExecutor.RfaFamily11.cs）
            var connectors = new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>()
                .Select(ce =>
                {
                    try { return (object)RfaDescribeConnector(ce); }
                    catch { return new { id = ce.Id.GetIdValue() }; }
                }).ToList();

            return new
            {
                title = doc.Title,
                path = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                category = fam?.FamilyCategory?.Name,
                familyPlacementType = fam?.FamilyPlacementType.ToString(),
                currentType = fm.CurrentType?.Name,
                types,
                parameters = parameters_,
                referencePlanes = refPlanes,
                levels,
                views,
                forms,
                subcategories,
                materials,
                nestedFamilies,
                connectors,
                units = "長度 mm、角度 度、面積 m²、體積 m³",
            };
        }

        #endregion

        #region rfa_create_reference_plane

        private object RfaCreateReferencePlane(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            var planes = parameters["planes"] as JArray;
            if (planes == null || planes.Count == 0)
                throw new Exception("請提供 planes 陣列（每個元素描述一個參考平面）");

            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate &&
                            (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan ||
                             v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Section))
                .ToList();

            var existingNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().Select(r => r.Name));

            var created = new List<object>();
            var skipped = new List<object>();

            using (var t = new Transaction(doc, "MCP: rfa_create_reference_plane"))
            {
                t.Start();
                foreach (JObject p in planes.OfType<JObject>())
                {
                    string name = p["name"]?.Value<string>();
                    if (!string.IsNullOrEmpty(name) && existingNames.Contains(name))
                    {
                        skipped.Add(new { name, reason = "已有同名參考平面" });
                        continue;
                    }

                    XYZ bubble, free, cut;
                    string axis = p["normal"]?.Value<string>()?.Trim().ToUpperInvariant();
                    if (!string.IsNullOrEmpty(axis))
                    {
                        double off = RfaMm(p["offsetMm"]?.Value<double>() ?? 0);
                        double ext = RfaMm(p["extentMm"]?.Value<double>() ?? 1000);
                        switch (axis)
                        {
                            case "X": // 平面 x = offset（垂直，平面圖可見）
                                bubble = new XYZ(off, ext, 0); free = new XYZ(off, -ext, 0); cut = XYZ.BasisZ; break;
                            case "Y": // 平面 y = offset（垂直，平面圖可見）
                                bubble = new XYZ(-ext, off, 0); free = new XYZ(ext, off, 0); cut = XYZ.BasisZ; break;
                            case "Z": // 平面 z = offset（水平，前/後立面可見）
                                bubble = new XYZ(-ext, 0, off); free = new XYZ(ext, 0, off); cut = XYZ.BasisY; break;
                            default:
                                throw new Exception($"normal 只接受 X / Y / Z，收到: {axis}");
                        }
                    }
                    else
                    {
                        if (p["bubbleEndMm"] == null || p["freeEndMm"] == null || p["cutVector"] == null)
                            throw new Exception("每個參考平面需提供 normal（+offsetMm），或 bubbleEndMm + freeEndMm + cutVector");
                        bubble = RfaReadXyzMm(p["bubbleEndMm"], "bubbleEndMm");
                        free = RfaReadXyzMm(p["freeEndMm"], "freeEndMm");
                        cut = RfaReadVector(p["cutVector"], "cutVector");
                    }

                    if (bubble.IsAlmostEqualTo(free))
                        throw new Exception($"參考平面「{name}」的兩端點重合");

                    // 參考平面要畫在「視線方向與 cutVector 平行」的視圖裡
                    View host = null;
                    string viewName = p["viewName"]?.Value<string>();
                    if (!string.IsNullOrEmpty(viewName))
                    {
                        host = views.FirstOrDefault(v => v.Name == viewName)
                               ?? throw new Exception($"找不到視圖「{viewName}」");
                    }
                    else
                    {
                        host = views.FirstOrDefault(v =>
                        {
                            try { return Math.Abs(v.ViewDirection.Normalize().DotProduct(cut)) > 0.999; }
                            catch { return false; }
                        });
                        if (host == null)
                            throw new Exception($"找不到可放置參考平面「{name}」的視圖（需要視線方向平行於 cutVector 的平面或立面視圖）");
                    }

                    ReferencePlane rp = doc.FamilyCreate.NewReferencePlane(bubble, free, cut, host);
                    if (!string.IsNullOrEmpty(name))
                    {
                        rp.Name = name;
                        existingNames.Add(name);
                    }

                    created.Add(new
                    {
                        id = rp.Id.GetIdValue(),
                        name = rp.Name,
                        normal = RfaVec(rp.Normal),
                        origin = RfaPt(rp.GetPlane().Origin),
                        hostView = host.Name,
                    });
                }
                t.Commit();
            }

            return new { createdCount = created.Count, created, skipped };
        }

        #endregion

        #region rfa_create_extrusion

        private struct RfaSketchFrame
        {
            public XYZ Origin, U, V, Normal;
            public SketchPlane Sketch;
            public string Description;
            public XYZ At(double uMm, double vMm) => Origin + U * RfaMm(uMm) + V * RfaMm(vMm);
        }

        private RfaSketchFrame RfaResolveSketchFrame(Document doc, JObject parameters)
        {
            string refPlaneName = parameters["referencePlane"]?.Value<string>();
            if (!string.IsNullOrEmpty(refPlaneName))
            {
                var rp = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .FirstOrDefault(r => r.Name == refPlaneName)
                    ?? throw new Exception($"找不到參考平面「{refPlaneName}」");
                Plane pl = rp.GetPlane();
                return new RfaSketchFrame
                {
                    Origin = pl.Origin, U = pl.XVec, V = pl.YVec, Normal = pl.Normal,
                    Sketch = SketchPlane.Create(doc, rp.Id),
                    Description = $"參考平面「{rp.Name}」",
                };
            }

            string plane = (parameters["plane"]?.Value<string>() ?? "XY").Trim().ToUpperInvariant();
            double off = RfaMm(parameters["planeOffsetMm"]?.Value<double>() ?? 0);
            XYZ origin, u, v;
            switch (plane)
            {
                case "XY": origin = new XYZ(0, 0, off); u = XYZ.BasisX; v = XYZ.BasisY; break;
                case "XZ": origin = new XYZ(0, off, 0); u = XYZ.BasisX; v = XYZ.BasisZ; break;
                case "YZ": origin = new XYZ(off, 0, 0); u = XYZ.BasisY; v = XYZ.BasisZ; break;
                default: throw new Exception($"plane 只接受 XY / XZ / YZ，收到: {plane}");
            }
            XYZ n = u.CrossProduct(v);
            Plane geo = Plane.CreateByOriginAndBasis(origin, u, v);
            return new RfaSketchFrame
            {
                Origin = origin, U = u, V = v, Normal = n,
                Sketch = SketchPlane.Create(doc, geo),
                Description = $"{plane} 平面（偏移 {RfaToMm(off)} mm，擠出方向 {n.X:0},{n.Y:0},{n.Z:0}）",
            };
        }

        private static CurveArray RfaBuildLoop(JObject loop, RfaSketchFrame f, int index)
        {
            var arr = new CurveArray();

            if (loop["circle"] is JObject c)
            {
                var ctr = c["center"] as JArray;
                double r = c["radius"]?.Value<double>() ?? 0;
                if (ctr == null || ctr.Count != 2 || r <= 0) throw new Exception($"輪廓 #{index} 的 circle 需要 center [u,v] 與 radius > 0");
                XYZ center = f.At(ctr[0].Value<double>(), ctr[1].Value<double>());
                Plane pl = Plane.CreateByOriginAndBasis(center, f.U, f.V);
                arr.Append(Arc.Create(pl, RfaMm(r), 0, Math.PI));
                arr.Append(Arc.Create(pl, RfaMm(r), Math.PI, 2 * Math.PI));
                return arr;
            }

            List<double[]> pts;
            if (loop["rectangle"] is JObject rect)
            {
                var ctr = rect["center"] as JArray;
                double cu = ctr != null && ctr.Count == 2 ? ctr[0].Value<double>() : 0;
                double cv = ctr != null && ctr.Count == 2 ? ctr[1].Value<double>() : 0;
                double w = rect["width"]?.Value<double>() ?? 0, h = rect["height"]?.Value<double>() ?? 0;
                if (w <= 0 || h <= 0) throw new Exception($"輪廓 #{index} 的 rectangle 需要 width、height > 0");
                pts = new List<double[]>
                {
                    new[] { cu - w / 2, cv - h / 2 }, new[] { cu + w / 2, cv - h / 2 },
                    new[] { cu + w / 2, cv + h / 2 }, new[] { cu - w / 2, cv + h / 2 },
                };
            }
            else if (loop["points"] is JArray pa)
            {
                pts = pa.OfType<JArray>().Select(a => new[] { a[0].Value<double>(), a[1].Value<double>() }).ToList();
            }
            else
            {
                throw new Exception($"輪廓 #{index} 需要 points、rectangle 或 circle 其中之一");
            }

            // 去掉重複的收尾點，自動閉合
            if (pts.Count > 1 && Math.Abs(pts[0][0] - pts[pts.Count - 1][0]) < 1e-6 && Math.Abs(pts[0][1] - pts[pts.Count - 1][1]) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) throw new Exception($"輪廓 #{index} 至少需要 3 個點");

            for (int i = 0; i < pts.Count; i++)
            {
                XYZ a = f.At(pts[i][0], pts[i][1]);
                XYZ b = f.At(pts[(i + 1) % pts.Count][0], pts[(i + 1) % pts.Count][1]);
                if (a.DistanceTo(b) < RfaMm(0.5))
                    throw new Exception($"輪廓 #{index} 第 {i} 段長度小於 0.5 mm，請合併重複點");
                arr.Append(Line.CreateBound(a, b));
            }
            return arr;
        }

        private object RfaCreateExtrusion(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;

            var profiles = parameters["profiles"] as JArray;
            if (profiles == null || profiles.Count == 0)
                throw new Exception("請提供 profiles（至少一個封閉輪廓；多個輪廓時，內部輪廓會成為開孔）");

            bool isSolid = parameters["isSolid"]?.Value<bool>() ?? true;
            double startMm = parameters["startOffsetMm"]?.Value<double>() ?? 0;
            double endMm = parameters["endOffsetMm"]?.Value<double>()
                           ?? throw new Exception("請提供 endOffsetMm（擠出終點，沿草圖平面法向量，mm）");
            if (Math.Abs(endMm - startMm) < 0.5) throw new Exception("擠出深度（endOffsetMm - startOffsetMm）太小");

            string materialName = parameters["material"]?.Value<string>();
            var associate = parameters["associate"] as JObject;

            var warnings = new List<string>();
            Extrusion ext;
            RfaSketchFrame frame;
            var circleReqs = new List<RfaCircleLabelRequest>();
            object result;

            using (var t = new Transaction(doc, "MCP: rfa_create_extrusion"))
            {
                // 警告（例如「光源在實體內」）靜默刪除並回報，錯誤靜默復原；不跳 Revit 對話框卡住 MCP
                var failures = RfaUseRollbackOnError(t);
                t.Start();
                frame = RfaResolveSketchFrame(doc, parameters);

                var loops = new CurveArrArray();
                int idx = 0;
                foreach (JObject loop in profiles.OfType<JObject>())
                {
                    RfaCollectCircleLabel(fm, loop, frame, $"profiles[{idx}]", circleReqs);
                    loops.Append(RfaBuildLoop(loop, frame, idx++));
                }

                try
                {
                    ext = doc.FamilyCreate.NewExtrusion(isSolid, loops, frame.Sketch, RfaMm(endMm));
                }
                catch (Exception ex)
                {
                    throw new Exception($"建立擠出失敗：{ex.Message}（常見原因：輪廓不封閉或自我交錯、族群樣板不支援擠出（例如概念量體／自適應族群））");
                }
                if (ext == null) throw new Exception("建立擠出失敗（Revit 回傳 null）");

                ext.StartOffset = RfaMm(startMm);
                ext.EndOffset = RfaMm(endMm);

                // 材質與參數綁定（例如擠出終點 ↔ 厚度），共用 RfaFamily2.cs 的輔助函式
                var assocResults = RfaApplyMaterialAndAssociations(doc, fm, ext, isSolid, materialName, associate,
                    new Dictionary<string, BuiltInParameter>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "extrusionEnd", BuiltInParameter.EXTRUSION_END_PARAM },
                        { "extrusionStart", BuiltInParameter.EXTRUSION_START_PARAM },
                    }, warnings);

                if (t.Commit() != TransactionStatus.Committed)
                    throw new Exception($"建立擠出的交易被 Revit 復原{RfaFailureText(failures)}");
                foreach (string w in failures.Warnings) warnings.Add($"Revit 警告：{w}");

                result = assocResults;
            }

            // 輪廓圓直徑標籤（circle.diameterParameter），獨立交易，失敗只寫 warnings
            var (diameterLabels, flexCheck) = RfaLabelProfileCircles(doc, new[] { ext.Sketch }, circleReqs,
                parameters["viewName"]?.Value<string>(), warnings);

            return new
            {
                id = ext.Id.GetIdValue(),
                isSolid,
                sketchPlane = frame.Description,
                startOffsetMm = RfaToMm(ext.StartOffset),
                endOffsetMm = RfaToMm(ext.EndOffset),
                profileCount = profiles.Count,
                associated = result,
                diameterLabels,
                flexCheck,
                warnings,
            };
        }

        #endregion

        #region rfa_add_parameter

        private object RfaAddParameter(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;

            var list = parameters["parameters"] as JArray;
            if (list == null || list.Count == 0)
                throw new Exception("請提供 parameters 陣列（每個元素含 name、type，可選 group、isInstance、formula、value；共用參數改傳 shared: true）");

            string sharedFile = parameters["sharedParameterFile"]?.Type == JTokenType.String
                ? parameters["sharedParameterFile"].Value<string>() : null;

            var created = new List<object>();
            var skipped = new List<object>();
            var errors = new List<object>();
            string sharedFileUsed = null;

            // 共用參數檔需涵蓋整個 Transaction（含 Commit）後才還原，見 RfaSharedParamSource 說明（CommandExecutor.RfaFamily9.cs）
            using (var sharedSource = new RfaSharedParamSource(doc.Application, sharedFile))
            using (var t = new Transaction(doc, "MCP: rfa_add_parameter"))
            {
                t.Start();
                foreach (JObject p in list.OfType<JObject>())
                {
                    string name = p["name"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(name)) { errors.Add(new { name, error = "缺少 name" }); continue; }

                    if (RfaFindParam(fm, name) != null)
                    {
                        skipped.Add(new { name, reason = "已有同名族群參數" });
                        continue;
                    }

                    bool isShared = p["shared"]?.Type == JTokenType.Boolean && p["shared"].Value<bool>();
                    string typeKey;
                    ForgeTypeId spec;
                    ExternalDefinition exDef = null;
                    if (isShared)
                    {
                        // 共用參數：型別由共用參數定義決定，type 欄位忽略
                        try
                        {
                            DefinitionFile defFile = sharedSource.Open();
                            sharedFileUsed = sharedSource.FilePath;
                            exDef = RfaFindSharedDefinition(defFile, name, p["sharedGuid"]?.Value<string>());
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new { name, error = ex.Message });
                            continue;
                        }
                        spec = exDef.GetDataType();
                        typeKey = RfaSpecName(spec);
                    }
                    else
                    {
                        typeKey = (p["type"]?.Value<string>() ?? "length").Trim().ToLowerInvariant();
                        spec = RfaSpecMap.Where(m => m.Key == typeKey).Select(m => m.Spec).FirstOrDefault();
                        if (spec == null)
                        {
                            errors.Add(new { name, error = $"不支援的 type「{typeKey}」（可用: {string.Join(", ", RfaSpecMap.Select(m => m.Key))}）" });
                            continue;
                        }
                    }

                    bool isInstance = p["isInstance"]?.Value<bool>() ?? false;
                    string groupName = p["group"]?.Value<string>()
                                       ?? (isShared ? RfaDefaultGroupForSpec(spec)
                                           : typeKey == "material" ? "materials" : typeKey == "text" ? "identity_data" : "dimensions");

                    FamilyParameter fp;
                    try
                    {
                        fp = isShared
                            ? fm.AddParameter(exDef, RfaGroupFromName(groupName), isInstance)
                            : fm.AddParameter(name, RfaGroupFromName(groupName), spec, isInstance);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new { name, error = $"新增失敗：{ex.Message}" });
                        continue;
                    }

                    string formula = p["formula"]?.Value<string>();
                    string note = null;
                    if (!string.IsNullOrEmpty(formula))
                    {
                        try { fm.SetFormula(fp, formula); }
                        catch (Exception ex) { note = $"公式設定失敗：{ex.Message}"; }
                    }
                    else if (p["value"] != null && p["value"].Type != JTokenType.Null)
                    {
                        if (fm.CurrentType == null)
                            note = "族群目前沒有任何類型，未寫入 value（可用 rfa_set_family_type 建立類型）";
                        else
                        {
                            try { RfaSetValue(doc, fm, fp, p["value"]); }
                            catch (Exception ex) { note = $"值設定失敗：{ex.Message}"; }
                        }
                    }

                    if (isShared)
                    {
                        created.Add(new
                        {
                            name,
                            type = typeKey,
                            group = groupName,
                            isInstance,
                            shared = true,
                            guid = exDef.GUID.ToString(),
                            definitionGroup = exDef.OwnerGroup?.Name,
                            formula = string.IsNullOrEmpty(fp.Formula) ? null : fp.Formula,
                            currentValue = RfaReadValue(doc, fm.CurrentType, fp),
                            note,
                        });
                        continue;
                    }

                    created.Add(new
                    {
                        name,
                        type = typeKey,
                        group = groupName,
                        isInstance,
                        formula = string.IsNullOrEmpty(fp.Formula) ? null : fp.Formula,
                        currentValue = RfaReadValue(doc, fm.CurrentType, fp),
                        note,
                    });
                }
                t.Commit();
            }

            if (sharedFileUsed != null)
                return new { createdCount = created.Count, created, skipped, errors, sharedParameterFile = sharedFileUsed };
            return new { createdCount = created.Count, created, skipped, errors };
        }

        #endregion

        #region rfa_set_family_type

        private object RfaSetFamilyType(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;

            string typeName = parameters["typeName"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(typeName)) throw new Exception("請提供 typeName");
            string baseType = parameters["baseType"]?.Value<string>();
            string renameTo = parameters["renameTo"]?.Value<string>();
            var values = parameters["values"] as JObject;
            var formulas = parameters["formulas"] as JObject;

            bool createdNew = false;
            var applied = new List<object>();
            var errors = new List<object>();
            // 交易內「已送出」的設定；Commit 後逐一讀回比對，確認真的生效才列入 applied
            var pendingValues = new List<KeyValuePair<string, JToken>>();
            var pendingFormulas = new List<KeyValuePair<string, string>>();
            var failures = new RfaRollbackOnErrorPreprocessor();
            TransactionStatus status;

            using (var t = new Transaction(doc, "MCP: rfa_set_family_type"))
            {
                // 失敗預處理器：Warning 靜默刪除；Error（例如「Constraints are not satisfied」）記錄後整筆復原，不跳對話框
                var fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(failures);
                fho.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(fho);
                t.Start();

                FamilyType target = fm.Types.Cast<FamilyType>().FirstOrDefault(ft => ft.Name == typeName);
                if (target == null)
                {
                    // 新類型會複製「目前類型」的值；指定 baseType 時先切到它
                    if (!string.IsNullOrEmpty(baseType))
                    {
                        FamilyType b = fm.Types.Cast<FamilyType>().FirstOrDefault(ft => ft.Name == baseType)
                                       ?? throw new Exception($"找不到 baseType「{baseType}」");
                        fm.CurrentType = b;
                    }
                    target = fm.NewType(typeName);
                    createdNew = true;
                }
                fm.CurrentType = target;

                if (!string.IsNullOrEmpty(renameTo) && renameTo != typeName)
                {
                    if (fm.Types.Cast<FamilyType>().Any(ft => ft.Name == renameTo))
                        throw new Exception($"已有名為「{renameTo}」的類型，無法改名");
                    fm.RenameCurrentType(renameTo);
                }

                if (formulas != null)
                {
                    foreach (var kv in formulas)
                    {
                        FamilyParameter fp = RfaFindParam(fm, kv.Key);
                        if (fp == null) { errors.Add(new { parameter = kv.Key, error = "找不到此族群參數" }); continue; }
                        try
                        {
                            string f = kv.Value?.Type == JTokenType.Null ? null : kv.Value?.Value<string>();
                            fm.SetFormula(fp, string.IsNullOrEmpty(f) ? null : f);
                            pendingFormulas.Add(new KeyValuePair<string, string>(kv.Key, f));
                        }
                        catch (Exception ex) { errors.Add(new { parameter = kv.Key, error = $"公式設定失敗：{ex.Message}" }); }
                    }
                }

                if (values != null)
                {
                    foreach (var kv in values)
                    {
                        FamilyParameter fp = RfaFindParam(fm, kv.Key);
                        if (fp == null) { errors.Add(new { parameter = kv.Key, error = "找不到此族群參數" }); continue; }
                        if (!string.IsNullOrEmpty(fp.Formula))
                        {
                            errors.Add(new { parameter = kv.Key, error = $"此參數由公式「{fp.Formula}」驅動，不能直接設值" });
                            continue;
                        }
                        try
                        {
                            RfaSetValue(doc, fm, fp, kv.Value);
                            pendingValues.Add(new KeyValuePair<string, JToken>(kv.Key, kv.Value));
                        }
                        catch (Exception ex) { errors.Add(new { parameter = kv.Key, error = ex.Message }); }
                    }
                }

                status = t.Commit();
            }

            bool rolledBack = status != TransactionStatus.Committed;
            string failureText = failures.Errors.Count > 0 ? string.Join(" | ", failures.Errors) : null;
            string warningText = failures.Warnings.Count > 0 ? string.Join(" | ", failures.Warnings) : null;

            if (rolledBack)
            {
                // 整筆交易被復原：沒有任何設定生效（含新建類型、改名）
                createdNew = false;
                string why = $"整筆交易已被 Revit 復原（狀態：{status}），沒有任何變更生效"
                             + (failureText != null ? $"；Revit 訊息：{failureText}" : "")
                             + "（常見原因：參數值使約束無法滿足，例如角度／長度超出幾何可達範圍）";
                foreach (var kv in pendingFormulas)
                    errors.Add(new { parameter = kv.Key, requestedFormula = kv.Value, error = why });
                foreach (var kv in pendingValues)
                {
                    FamilyParameter fp = RfaFindParam(fm, kv.Key);
                    object actual = fp != null ? RfaReadValue(doc, fm.CurrentType, fp) : null;
                    errors.Add(new { parameter = kv.Key, requested = kv.Value, actual, error = why, revitFailure = failureText });
                }
            }
            else
            {
                foreach (var kv in pendingFormulas)
                {
                    FamilyParameter fp = RfaFindParam(fm, kv.Key);
                    string got = fp == null ? null : (string.IsNullOrEmpty(fp.Formula) ? null : fp.Formula);
                    bool wantClear = string.IsNullOrEmpty(kv.Value);
                    if (fp != null && (wantClear ? got == null : got != null))
                        applied.Add(new { parameter = kv.Key, formula = got });
                    else
                        errors.Add(new { parameter = kv.Key, requestedFormula = kv.Value, actualFormula = got, error = "提交後讀回的公式與要求不符", revitFailure = warningText });
                }
                foreach (var kv in pendingValues)
                {
                    FamilyParameter fp = RfaFindParam(fm, kv.Key);
                    if (fp == null) { errors.Add(new { parameter = kv.Key, error = "提交後找不到此族群參數" }); continue; }
                    if (RfaValueMatches(doc, fm.CurrentType, fp, kv.Value, out object actual))
                        applied.Add(new { parameter = kv.Key, value = actual });
                    else
                        errors.Add(new
                        {
                            parameter = kv.Key,
                            requested = kv.Value,
                            actual,
                            error = "提交後讀回的值與要求不符（Revit 未採用此值；可能被公式、約束或其他參數覆寫）",
                            revitFailure = warningText,
                        });
                }
            }

            var finalValues = fm.Parameters.Cast<FamilyParameter>()
                .OrderBy(p => p.Definition.Name)
                .ToDictionary(p => p.Definition.Name, p => RfaReadValue(doc, fm.CurrentType, p));

            return new
            {
                type = fm.CurrentType?.Name,
                createdNew,
                rolledBack,
                allTypes = fm.Types.Cast<FamilyType>().Select(ft => ft.Name).ToList(),
                applied,
                errors,
                values = finalValues,
            };
        }

        #endregion
    }
}
