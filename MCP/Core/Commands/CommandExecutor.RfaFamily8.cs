using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// RFA 族群編輯工具第八批：巢狀族群（rfa_place_nested_family）。
    ///
    /// 在作用中的族群文件裡「載入 → 放置 → 綁定參數」一次完成：
    ///   1. familyPath：以 Document.LoadFamily(path, IFamilyLoadOptions, out Family) 載入另一個 .rfa；
    ///      familyName：使用族群檔中已載入的族群。
    ///   2. 依 Family.FamilyPlacementType 選擇 FamilyItemFactory.NewFamilyInstance 的多載放置實體
    ///      （只支援不需要主體／面的放置方式，其他類型逐筆回報錯誤，不猜測）。
    ///   3. 以 FamilyManager.AssociateElementParameterToFamilyParameter 把巢狀族群的實體／類型參數綁到主族群參數。
    ///
    /// 已知限制：放置的實體是固定座標，本工具不會把它們鎖到參考平面；主族群變形時它們不會跟著移動，
    /// 除非透過綁定的參數驅動巢狀族群本身。
    /// </summary>
    public partial class CommandExecutor
    {
        #region rfa_place_nested_family

        /// <summary>
        /// 巢狀族群的載入選項：族群已存在時，是否從檔案重新載入由 reload 決定；
        /// 是否覆蓋既有類型參數值由 overwriteParameterValues 決定。共用巢狀族群一律採用檔案內的版本。
        /// </summary>
        private class RfaNestedLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _reload;
            private readonly bool _overwrite;
            public RfaNestedLoadOptions(bool reload, bool overwrite) { _reload = reload; _overwrite = overwrite; }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwrite;
                return _reload;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwrite;
                return _reload;
            }
        }

        /// <summary>族群檔中可作為巢狀族群的 Family：可編輯、非內建族群、不是族群檔自己（OwnerFamily）</summary>
        private static List<Family> RfaNestedFamilies(Document doc)
        {
            IdType ownerId = doc.OwnerFamily?.Id.GetIdValue() ?? (IdType)(-1);
            return new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => f.Id.GetIdValue() != ownerId && f.IsEditable && !f.IsInPlace)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>族群的類型（FamilySymbol），依名稱排序</summary>
        private static List<FamilySymbol> RfaFamilySymbols(Document doc, Family fam)
        {
            return fam.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .Where(s => s != null)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>族群「主體」設定（FAMILY_HOSTING_BEHAVIOR）；讀不到時回傳 null</summary>
        private static Parameter RfaHostingParam(Family fam)
        {
            try { return fam.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR); } catch { return null; }
        }

        /// <summary>參數名稱清單：不含唯讀與無儲存型別的參數，去重後排序</summary>
        private static List<string> RfaEditableParamNames(Element e)
        {
            if (e == null) return null;
            var names = new List<string>();
            foreach (Parameter p in e.Parameters)
            {
                if (p?.Definition == null || p.IsReadOnly || p.StorageType == StorageType.None) continue;
                names.Add(p.Definition.Name);
            }
            return names.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 在作用中的族群文件中載入（或取用已載入的）巢狀族群、放置實體，並把巢狀族群參數綁到主族群參數。
        /// 參數：familyPath | familyName（擇一）、reload、overwriteParameterValues、typeName、
        /// instances [{ locationMm, rotationDeg }]、associate { 巢狀參數: 主族群參數 }、applyToExisting、viewName（2D 族群用）。
        /// </summary>
        private object RfaPlaceNestedFamily(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;

            // ---- 解析來源 ----
            string familyPath = parameters["familyPath"]?.Value<string>()?.Trim();
            string familyName = parameters["familyName"]?.Value<string>()?.Trim();
            bool hasPath = !string.IsNullOrEmpty(familyPath);
            bool hasName = !string.IsNullOrEmpty(familyName);
            if (hasPath == hasName)
                throw new Exception("familyPath 與 familyName 必須擇一提供（不可都給、也不可都不給）");

            bool reload = parameters["reload"]?.Value<bool>() ?? false;
            bool overwrite = parameters["overwriteParameterValues"]?.Value<bool>() ?? false;
            string typeName = parameters["typeName"]?.Value<string>()?.Trim();
            var instancesIn = parameters["instances"] as JArray;
            var associate = parameters["associate"] as JObject;
            bool applyToExisting = parameters["applyToExisting"]?.Value<bool>() ?? false;
            string viewName = parameters["viewName"]?.Value<string>()?.Trim();

            if (hasPath)
            {
                if (!Path.IsPathRooted(familyPath))
                    throw new Exception($"familyPath 必須是完整路徑，收到: {familyPath}");
                if (!Path.GetExtension(familyPath).Equals(".rfa", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"familyPath 副檔名必須是 .rfa，收到: {Path.GetExtension(familyPath)}");
                familyPath = Path.GetFullPath(familyPath);
                if (!File.Exists(familyPath))
                    throw new Exception($"檔案不存在: {familyPath}");
                if (RfaSamePath(doc.PathName, familyPath))
                    throw new Exception($"familyPath 就是作用中的族群檔本身（{familyPath}），不能把族群巢狀載入到自己裡面。");
            }

            if (doc.IsModifiable)
                throw new Exception($"族群文件「{doc.Title}」有尚未結束的交易，無法執行。請先在 Revit 中完成或取消目前的編輯。");

            var errors = new List<object>();
            var warnings = new List<string>();
            var placed = new List<object>();
            var associated = new List<object>();

            Family fam = null;
            FamilySymbol symbol = null;
            bool wasAlreadyLoaded = false;
            bool reloaded = false;
            List<string> availableTypes = null;
            object nestedParameters = null;

            using (var t = new Transaction(doc, "MCP: rfa_place_nested_family"))
            {
                t.Start();

                // ---- 1. 取得族群 ----
                if (hasPath)
                {
                    string expectedName = Path.GetFileNameWithoutExtension(familyPath);
                    Family existing = RfaNestedFamilies(doc).FirstOrDefault(f => string.Equals(f.Name, expectedName, StringComparison.OrdinalIgnoreCase));
                    wasAlreadyLoaded = existing != null;

                    bool ok;
                    Family loadedFam;
                    try
                    {
                        ok = doc.LoadFamily(familyPath, new RfaNestedLoadOptions(reload, overwrite), out loadedFam);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"載入族群 {familyPath} 失敗（{ex.GetType().Name}: {ex.Message}）。常見原因：檔案版本比目前 Revit 新、檔案損毀，或該族群類別不能巢狀載入到「{doc.OwnerFamily?.FamilyCategory?.Name}」族群中。");
                    }

                    if (ok && loadedFam != null)
                    {
                        fam = loadedFam;
                        reloaded = wasAlreadyLoaded && reload;
                    }
                    else
                    {
                        // 同名族群已存在且未要求重新載入（或 Revit 判定無需載入）：改用已載入的族群
                        fam = existing ?? RfaNestedFamilies(doc).FirstOrDefault(f => string.Equals(f.Name, expectedName, StringComparison.OrdinalIgnoreCase));
                        if (fam == null)
                            throw new Exception($"LoadFamily 回傳 {ok}，但族群檔中找不到名為「{expectedName}」的族群。檔案可能不是有效的可載入族群。");
                        wasAlreadyLoaded = true;
                        if (reload)
                            warnings.Add($"要求 reload，但 Revit 沒有從檔案重新載入「{fam.Name}」（LoadFamily 回傳 false，通常表示檔案與已載入的版本相同）；沿用已載入的族群。");
                    }
                }
                else
                {
                    var nested = RfaNestedFamilies(doc);
                    fam = nested.FirstOrDefault(f => f.Name == familyName)
                          ?? nested.FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
                    if (fam == null)
                    {
                        string list = nested.Count == 0 ? "（族群檔中目前沒有任何已載入的可編輯族群）" : string.Join(", ", nested.Select(f => $"「{f.Name}」"));
                        throw new Exception($"族群檔中找不到已載入的族群「{familyName}」。已載入的族群: {list}。要從檔案載入請改用 familyPath。");
                    }
                    wasAlreadyLoaded = true;
                }

                // ---- 2. 取得類型 ----
                var symbols = RfaFamilySymbols(doc, fam);
                availableTypes = symbols.Select(s => s.Name).ToList();
                if (symbols.Count == 0)
                    throw new Exception($"族群「{fam.Name}」沒有任何類型，無法放置。");
                if (string.IsNullOrEmpty(typeName))
                {
                    symbol = symbols[0];
                }
                else
                {
                    symbol = symbols.FirstOrDefault(s => s.Name == typeName)
                             ?? symbols.FirstOrDefault(s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase));
                    if (symbol == null)
                        throw new Exception($"族群「{fam.Name}」沒有類型「{typeName}」。可用類型: {string.Join(", ", availableTypes)}");
                }
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    doc.Regenerate();
                }

                // ---- 3. 放置 ----
                FamilyPlacementType placement = fam.FamilyPlacementType;
                Parameter hostParam = RfaHostingParam(fam);
                int? hostingValue = null;
                string hostingText = null;
                if (hostParam != null && hostParam.HasValue)
                {
                    try { hostingValue = hostParam.AsInteger(); } catch { }
                    try { hostingText = hostParam.AsValueString(); } catch { }
                }

                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList();

                View placementView = null;
                string viewProblem = null;
                if (placement == FamilyPlacementType.ViewBased && instancesIn != null && instancesIn.Count > 0)
                {
                    var candidates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate).ToList();
                    if (!string.IsNullOrEmpty(viewName))
                    {
                        placementView = candidates.FirstOrDefault(v => v.Name == viewName);
                        if (placementView == null)
                            viewProblem = $"找不到視圖「{viewName}」。可用視圖: {string.Join(", ", candidates.Select(v => v.Name))}";
                    }
                    else
                    {
                        View active = _uiApp.ActiveUIDocument?.ActiveView;
                        if (active != null && active is ViewPlan && !active.IsTemplate && active.Document.Equals(doc))
                            placementView = active;
                        else
                            placementView = candidates.Where(v => v.ViewType == ViewType.FloorPlan)
                                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                                ?? candidates.Where(v => v is ViewPlan).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                        if (placementView == null)
                            viewProblem = "族群檔中找不到可放置 2D 族群的平面視圖，請以 viewName 指定一個 2D 視圖。";
                    }
                }

                string unsupported = null;
                switch (placement)
                {
                    case FamilyPlacementType.OneLevelBased:
                    case FamilyPlacementType.ViewBased:
                        break;
                    case FamilyPlacementType.WorkPlaneBased:
                        // 面式族群（Face-based）的 FamilyPlacementType 也是 WorkPlaneBased；以「主體」參數區分
                        if (hostingValue.HasValue && hostingValue.Value != 0)
                            unsupported = $"族群「{fam.Name}」是以面為主體的族群（FamilyPlacementType = WorkPlaneBased，主體 = {hostingText ?? hostingValue.ToString()}），需要指定主體面，本工具目前尚不支援此放置方式。";
                        break;
                    case FamilyPlacementType.OneLevelBasedHosted:
                        unsupported = $"族群「{fam.Name}」的放置方式是 OneLevelBasedHosted（需要牆／樓板／天花板等主體{(hostingText != null ? $"，主體 = {hostingText}" : "")}），本工具目前尚不支援此放置方式。";
                        break;
                    default:
                        // CurveBased / CurveBasedDetail / CurveDrivenStructural 需要線；TwoLevelsBased 需要兩個樓層；Adaptive 需要自適應點
                        unsupported = $"族群「{fam.Name}」的放置方式是 {placement}，本工具目前尚不支援此放置方式（只支援 OneLevelBased、WorkPlaneBased（非面式）與 ViewBased 2D 族群的單點放置）。";
                        break;
                }

                var newIds = new HashSet<IdType>();
                var newInstances = new List<FamilyInstance>();
                if (instancesIn != null)
                {
                    for (int i = 0; i < instancesIn.Count; i++)
                    {
                        var item = instancesIn[i] as JObject;
                        if (item == null) { errors.Add(new { index = i, error = "每個 instances 元素必須是物件 { locationMm, rotationDeg }" }); continue; }
                        if (unsupported != null) { errors.Add(new { index = i, error = unsupported }); continue; }
                        if (viewProblem != null) { errors.Add(new { index = i, error = viewProblem }); continue; }

                        using (var st = new SubTransaction(doc))
                        {
                            st.Start();
                            try
                            {
                                XYZ loc = RfaReadXyzMm(item["locationMm"], $"instances[{i}].locationMm");
                                double rotDeg = item["rotationDeg"]?.Value<double>() ?? 0;

                                FamilyInstance inst;
                                string method;
                                string levelName = null;
                                switch (placement)
                                {
                                    case FamilyPlacementType.OneLevelBased:
                                    {
                                        if (levels.Count == 0)
                                            throw new Exception($"族群「{fam.Name}」是 OneLevelBased，但作用中的族群檔沒有任何樓層可作為放置基準。");
                                        // 取高程 ≤ Z 的最高樓層；Z 低於所有樓層時取最低樓層（高差成為偏移）
                                        Level lvl = levels.LastOrDefault(l => l.Elevation <= loc.Z + 1e-9) ?? levels[0];
                                        levelName = lvl.Name;
                                        inst = doc.FamilyCreate.NewFamilyInstance(loc, symbol, (Element)lvl, StructuralType.NonStructural);
                                        method = "NewFamilyInstance(XYZ, FamilySymbol, Element level, StructuralType)";
                                        break;
                                    }
                                    case FamilyPlacementType.WorkPlaneBased:
                                    {
                                        try
                                        {
                                            inst = doc.FamilyCreate.NewFamilyInstance(loc, symbol, StructuralType.NonStructural);
                                            method = "NewFamilyInstance(XYZ, FamilySymbol, StructuralType)";
                                        }
                                        catch (Exception first)
                                        {
                                            // 退而求其次：放在高程等於 Z 的樓層平面上（工作平面 = 樓層）
                                            Level lvl = levels.FirstOrDefault(l => Math.Abs(l.Elevation - loc.Z) < RfaMm(0.5));
                                            if (lvl == null)
                                                throw new Exception($"無主體放置失敗（{first.Message}），且沒有高程等於 Z = {RfaToMm(loc.Z)} mm 的樓層可作為工作平面。");
                                            inst = doc.FamilyCreate.NewFamilyInstance(lvl.GetPlaneReference(), loc, XYZ.BasisX, symbol);
                                            levelName = lvl.Name;
                                            method = "NewFamilyInstance(Reference levelPlane, XYZ, XYZ refDir, FamilySymbol)";
                                            warnings.Add($"instances[{i}]：無主體放置失敗（{first.Message}），改放在樓層「{lvl.Name}」的平面上。");
                                        }
                                        break;
                                    }
                                    default: // ViewBased
                                        inst = doc.FamilyCreate.NewFamilyInstance(loc, symbol, placementView);
                                        method = $"NewFamilyInstance(XYZ, FamilySymbol, View「{placementView.Name}」)";
                                        break;
                                }
                                if (inst == null) throw new Exception("Revit 回傳 null");

                                if (Math.Abs(rotDeg) > 1e-9)
                                {
                                    doc.Regenerate();
                                    XYZ axisPt = (inst.Location as LocationPoint)?.Point ?? loc;
                                    Line axis = Line.CreateBound(axisPt, axisPt + XYZ.BasisZ);
                                    ElementTransformUtils.RotateElement(doc, inst.Id, axis, rotDeg * Math.PI / 180.0);
                                }

                                st.Commit();
                                doc.Regenerate();

                                var lp = inst.Location as LocationPoint;
                                double? rotBack = null;
                                try { if (lp != null) rotBack = Math.Round(lp.Rotation * 180.0 / Math.PI, 6); } catch { }
                                newIds.Add(inst.Id.GetIdValue());
                                newInstances.Add(inst);
                                placed.Add(new
                                {
                                    index = i,
                                    id = inst.Id.GetIdValue(),
                                    locationMm = lp != null ? RfaPt(lp.Point) : null,
                                    rotationDeg = rotBack ?? rotDeg,
                                    level = levelName,
                                    method,
                                });
                            }
                            catch (Exception ex)
                            {
                                if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                                errors.Add(new { index = i, error = ex.Message });
                            }
                        }
                    }
                }

                // ---- 4. 參數綁定 ----
                var targets = new List<FamilyInstance>(newInstances);
                List<FamilyInstance> existingInstances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol != null && fi.Symbol.Id == symbol.Id && !newIds.Contains(fi.Id.GetIdValue()))
                    .ToList();
                if (applyToExisting) targets.AddRange(existingInstances);

                if (associate != null && associate.Count > 0)
                {
                    if (targets.Count == 0)
                        warnings.Add(applyToExisting
                            ? "associate：沒有新放置的實體，族群檔中也沒有此類型的既有實體；實體參數未綁定（類型參數仍會綁定）。"
                            : "associate：沒有新放置的實體（既有實體需傳 applyToExisting: true）；實體參數未綁定（類型參數仍會綁定）。");

                    foreach (var kv in associate)
                    {
                        string nestedName = kv.Key;
                        string hostName = kv.Value?.Type == JTokenType.String ? kv.Value.Value<string>() : null;
                        if (string.IsNullOrWhiteSpace(hostName))
                        {
                            warnings.Add($"associate「{nestedName}」：主族群參數名稱必須是非空字串");
                            continue;
                        }
                        FamilyParameter fp = RfaFindParam(fm, hostName);
                        if (fp == null)
                        {
                            warnings.Add($"associate 巢狀「{nestedName}」→ 主族群「{hostName}」：主族群沒有參數「{hostName}」，請先用 rfa_add_parameter 建立");
                            continue;
                        }

                        bool isVisibleKey = string.Equals(nestedName, "visible", StringComparison.OrdinalIgnoreCase);

                        // 判斷是實體參數還是類型參數：先看實體，再看類型
                        Element probe = targets.FirstOrDefault() ?? existingInstances.FirstOrDefault();
                        Parameter probeInst = probe == null ? null
                            : isVisibleKey ? probe.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM) : probe.LookupParameter(nestedName);
                        Parameter typeParam = isVisibleKey ? null : symbol.LookupParameter(nestedName);

                        if (probeInst != null)
                        {
                            foreach (FamilyInstance fi in targets)
                            {
                                Parameter ep = isVisibleKey ? fi.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM) : fi.LookupParameter(nestedName);
                                RfaAssociateNested(fm, ep, fp, nestedName, hostName, "instance", fi.Id.GetIdValue(), associated, warnings);
                            }
                        }
                        else if (typeParam != null)
                        {
                            if (fp.IsInstance)
                            {
                                warnings.Add($"associate 巢狀類型參數「{nestedName}」→ 主族群「{hostName}」：巢狀族群的類型參數只能綁到主族群的類型參數，但「{hostName}」是實體參數");
                                continue;
                            }
                            RfaAssociateNested(fm, typeParam, fp, nestedName, hostName, "type", symbol.Id.GetIdValue(), associated, warnings);
                        }
                        else if (probe == null && !isVisibleKey)
                        {
                            warnings.Add($"associate 巢狀「{nestedName}」→ 主族群「{hostName}」：巢狀族群類型上沒有此參數，且沒有可檢查的實體（可能是實體參數，請放置實體或傳 applyToExisting: true）");
                        }
                        else
                        {
                            warnings.Add($"associate 巢狀「{nestedName}」→ 主族群「{hostName}」：巢狀族群「{fam.Name}」的實體與類型上都找不到參數「{nestedName}」（參數名稱區分大小寫；可用名稱見 nestedParameters）");
                        }
                    }
                }

                // ---- 5. 巢狀族群可綁定的參數名稱 ----
                Element sample = (Element)newInstances.FirstOrDefault() ?? existingInstances.FirstOrDefault();
                nestedParameters = new
                {
                    instance = RfaEditableParamNames(sample),
                    type = RfaEditableParamNames(symbol),
                    note = sample == null ? "族群檔中沒有此類型的實體，無法列出實體參數；放置一個實體後再查詢。" : null,
                };

                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                    throw new Exception($"rfa_place_nested_family 交易未成功提交（狀態：{status}），所有變更已復原");

                return new
                {
                    family = new
                    {
                        name = fam.Name,
                        id = fam.Id.GetIdValue(),
                        category = fam.FamilyCategory?.Name,
                        placementType = placement.ToString(),
                        hostingBehavior = hostingText,
                        wasAlreadyLoaded,
                        reloaded,
                    },
                    type = new { name = symbol.Name, id = symbol.Id.GetIdValue() },
                    availableTypes,
                    placementView = placementView?.Name,
                    placed,
                    associated,
                    nestedParameters,
                    errors,
                    warnings,
                    note = "放置的實體是固定座標，沒有鎖到參考平面；主族群變形時不會跟著移動，除非透過綁定的參數驅動巢狀族群。",
                };
            }
        }

        /// <summary>綁定單一巢狀族群參數到主族群參數；失敗寫進 warnings（註明兩邊名稱），成功寫進 associated</summary>
        private static void RfaAssociateNested(FamilyManager fm, Parameter ep, FamilyParameter fp, string nestedName, string hostName,
            string scope, IdType elementId, List<object> associated, List<string> warnings)
        {
            string tag = $"associate 巢狀「{nestedName}」→ 主族群「{hostName}」（{scope}, id {elementId}）";
            if (ep == null) { warnings.Add($"{tag}：元素上找不到此參數"); return; }

            ForgeTypeId a = null, b = null;
            try { a = ep.Definition.GetDataType(); } catch { }
            try { b = fp.Definition.GetDataType(); } catch { }
            if (a != null && b != null && !string.IsNullOrEmpty(a.TypeId) && !string.IsNullOrEmpty(b.TypeId) && a != b)
            {
                warnings.Add($"{tag}：型別不同（巢狀 {RfaSpecName(a)}、主族群 {RfaSpecName(b)}），無法綁定");
                return;
            }

            bool can;
            try { can = fm.CanElementParameterBeAssociated(ep); }
            catch (Exception ex) { warnings.Add($"{tag}：無法檢查是否可綁定（{ex.Message}）"); return; }
            if (!can) { warnings.Add($"{tag}：此參數不能綁定族群參數（例如唯讀、由公式驅動或為系統參數）"); return; }

            try
            {
                fm.AssociateElementParameterToFamilyParameter(ep, fp);
                associated.Add(new { nestedParameter = nestedName, hostParameter = hostName, scope, elementId });
            }
            catch (Exception ex)
            {
                warnings.Add($"{tag}：綁定失敗：{ex.Message}");
            }
        }

        #endregion
    }
}
