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
    /// RFA 族群編輯工具第九批：族群參數維護。
    /// - 共用參數查找 helper：rfa_add_parameter（shared: true）與 rfa_modify_parameter（replaceWithShared）共用，
    ///   只「讀取」共用參數檔中既有的定義，絕不建立或修改共用參數檔內容。
    /// - rfa_modify_parameter：更名、切換實例／類型、以共用參數取代（非破壞性）。
    /// - rfa_delete_parameter：刪除族群參數（破壞性；預設 dryRun，仍被使用的參數需 force 才刪）。
    /// </summary>
    public partial class CommandExecutor
    {
        #region 共用參數查找

        /// <summary>找不到共用參數定義時，錯誤訊息最多列出的定義名稱數</summary>
        private const int RfaSharedDefListLimit = 30;

        /// <summary>
        /// 共用參數檔的開啟範圍。第一次呼叫 Open() 時才真正開檔；
        /// 若指定的檔案與 Revit 目前設定不同，會暫時切換 Application.SharedParametersFilename，Dispose 時還原。
        /// FamilyManager.AddParameter / ReplaceParameter(ExternalDefinition, ...) 需要 Revit 當下仍開著
        /// 取得該定義的共用參數檔（見 GM_RfaFamilyInjection.cs 2026-08-31 根因修正），
        /// 所以呼叫端必須讓本物件涵蓋整個 Transaction（含 Commit）之後才 Dispose。
        /// 本類別只呼叫 OpenSharedParameterFile 讀取，不會寫入共用參數檔。
        /// </summary>
        private sealed class RfaSharedParamSource : IDisposable
        {
            private readonly Autodesk.Revit.ApplicationServices.Application _app;
            private readonly string _requestedPath;
            private string _originalPath;
            private bool _switched;
            private bool _opened;
            private DefinitionFile _file;
            private string _openError;

            /// <summary>實際讀取的共用參數檔路徑（開檔後才有值）</summary>
            public string FilePath { get; private set; }

            public RfaSharedParamSource(Autodesk.Revit.ApplicationServices.Application app, string requestedPath)
            {
                _app = app;
                _requestedPath = string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath.Trim();
            }

            public DefinitionFile Open()
            {
                if (_opened)
                {
                    if (_file == null) throw new Exception(_openError ?? "共用參數檔開啟失敗");
                    return _file;
                }
                _opened = true;
                try
                {
                    _originalPath = _app.SharedParametersFilename ?? string.Empty;
                    string target;
                    if (_requestedPath != null)
                    {
                        target = _requestedPath;
                        if (!Path.IsPathRooted(target))
                            throw new Exception($"sharedParameterFile 必須是絕對路徑，收到: {target}");
                        if (!File.Exists(target))
                            throw new Exception($"找不到共用參數檔: {target}");
                        if (!RfaSamePath(target, _originalPath))
                        {
                            _app.SharedParametersFilename = target;
                            _switched = true;
                        }
                    }
                    else
                    {
                        target = _originalPath;
                        if (string.IsNullOrWhiteSpace(target))
                            throw new Exception("Revit 目前沒有設定共用參數檔，請以 sharedParameterFile 指定 .txt 的絕對路徑");
                        if (!File.Exists(target))
                            throw new Exception($"Revit 目前設定的共用參數檔不存在: {target}（可用 sharedParameterFile 指定其他檔案）");
                    }

                    FilePath = target;
                    _file = _app.OpenSharedParameterFile();
                    if (_file == null)
                        throw new Exception($"無法開啟共用參數檔: {target}（檔案格式可能不正確）");
                    return _file;
                }
                catch (Exception ex)
                {
                    _openError = ex.Message;
                    throw;
                }
            }

            public void Dispose()
            {
                if (!_switched) return;
                _switched = false;
                try { _app.SharedParametersFilename = _originalPath ?? string.Empty; }
                catch { /* 還原失敗不可蓋掉主流程的結果或例外 */ }
            }
        }

        /// <summary>
        /// 在共用參數檔中依名稱找 ExternalDefinition；guidText 有值時用來驗證／區分同名定義。
        /// 找不到或有歧義時丟出例外，訊息列出搜尋過的群組與可用的定義名稱。
        /// </summary>
        private static ExternalDefinition RfaFindSharedDefinition(DefinitionFile file, string name, string guidText)
        {
            Guid? guid = null;
            if (!string.IsNullOrWhiteSpace(guidText))
            {
                if (!Guid.TryParse(guidText.Trim(), out Guid g))
                    throw new Exception($"sharedGuid 不是有效的 GUID: {guidText}");
                guid = g;
            }

            var groups = new List<string>();
            var all = new List<ExternalDefinition>();
            foreach (DefinitionGroup grp in file.Groups)
            {
                groups.Add(grp.Name);
                foreach (Definition d in grp.Definitions)
                    if (d is ExternalDefinition ed) all.Add(ed);
            }

            var byName = all.Where(d => d.Name == name).ToList();
            if (byName.Count == 0)
            {
                var available = all.Select(d => d.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                string list = string.Join(", ", available.Take(RfaSharedDefListLimit));
                if (available.Count > RfaSharedDefListLimit) list += $" …（共 {available.Count} 個）";
                throw new Exception(
                    $"共用參數檔中找不到名為「{name}」的定義（本工具不會在共用參數檔中建立定義）。" +
                    $"搜尋過的群組: {(groups.Count == 0 ? "（無）" : string.Join(", ", groups))}；" +
                    $"可用的定義: {(available.Count == 0 ? "（無）" : list)}");
            }

            if (guid.HasValue)
            {
                var match = byName.FirstOrDefault(d => d.GUID == guid.Value);
                if (match == null)
                    throw new Exception(
                        $"共用參數「{name}」的 GUID 與 sharedGuid 不符：檔案中為 {string.Join(", ", byName.Select(d => d.GUID.ToString()).Distinct())}，指定的是 {guid.Value}");
                return match;
            }

            var distinctGuids = byName.Select(d => d.GUID).Distinct().ToList();
            if (distinctGuids.Count > 1)
                throw new Exception(
                    $"共用參數檔中有多個名為「{name}」的定義，請以 sharedGuid 指定：" +
                    string.Join("; ", byName.Select(d => $"{d.GUID}（群組 {d.OwnerGroup?.Name}）")));
            return byName[0];
        }

        /// <summary>共用參數未指定 group 時的預設群組（與一般參數相同的規則）</summary>
        private static string RfaDefaultGroupForSpec(ForgeTypeId spec)
        {
            if (spec == SpecTypeId.Reference.Material) return "materials";
            if (spec == SpecTypeId.String.Text) return "identity_data";
            return "dimensions";
        }

        private static string RfaParamGuid(FamilyParameter fp)
        {
            try { return fp.IsShared ? fp.GUID.ToString() : null; }
            catch { return null; }
        }

        private static bool RfaIsBuiltInParam(FamilyParameter fp)
        {
            if (fp.Definition is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID)
                return true;
            return fp.Id.GetIdValue() < 0;
        }

        #endregion

        #region 公式引用比對

        private static bool RfaIsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// 找出公式中引用了哪些參數名稱（allNames 中的哪些）。
        /// 規則：先把雙引號字串常值遮掉；名稱由長到短比對，「[名稱]」或前後不是字母／數字／底線的完整名稱才算命中，
        /// 已被較長名稱占用的位置不再算給較短名稱（避免「Overall Width」被當成引用「Width」）。
        /// </summary>
        private static HashSet<string> RfaFormulaReferences(string formula, IEnumerable<string> allNames)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(formula)) return result;

            char[] chars = formula.ToCharArray();
            bool inString = false;
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '"') { inString = !inString; chars[i] = ' '; }
                else if (inString) chars[i] = ' ';
            }
            string f = new string(chars);
            var claimed = new bool[f.Length];

            bool Free(int start, int len)
            {
                for (int k = start; k < start + len; k++) if (claimed[k]) return false;
                return true;
            }
            void Claim(int start, int len)
            {
                for (int k = start; k < start + len; k++) claimed[k] = true;
            }

            foreach (string name in allNames.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal)
                         .OrderByDescending(n => n.Length))
            {
                bool hit = false;

                string bracketed = "[" + name + "]";
                int idx = 0;
                while (idx < f.Length && (idx = f.IndexOf(bracketed, idx, StringComparison.Ordinal)) >= 0)
                {
                    if (Free(idx, bracketed.Length)) { Claim(idx, bracketed.Length); hit = true; }
                    idx++;
                }

                idx = 0;
                while (idx < f.Length && (idx = f.IndexOf(name, idx, StringComparison.Ordinal)) >= 0)
                {
                    int end = idx + name.Length;
                    bool leftOk = idx == 0 || !RfaIsNameChar(f[idx - 1]) || !RfaIsNameChar(name[0]);
                    bool rightOk = end >= f.Length || !RfaIsNameChar(f[end]) || !RfaIsNameChar(name[name.Length - 1]);
                    if (leftOk && rightOk && Free(idx, name.Length)) { Claim(idx, name.Length); hit = true; }
                    idx++;
                }

                if (hit) result.Add(name);
            }
            return result;
        }

        /// <summary>其他族群參數中，公式引用了 targetName 的參數（不含自己）</summary>
        private static List<FamilyParameter> RfaParamsReferencing(FamilyManager fm, string targetName)
        {
            var allNames = fm.Parameters.Cast<FamilyParameter>().Select(p => p.Definition.Name).ToList();
            var list = new List<FamilyParameter>();
            foreach (FamilyParameter p in fm.Parameters)
            {
                if (p.Definition.Name == targetName) continue;
                string formula = p.Formula;
                if (string.IsNullOrEmpty(formula)) continue;
                if (RfaFormulaReferences(formula, allNames).Contains(targetName)) list.Add(p);
            }
            return list.OrderBy(p => p.Definition.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        #endregion

        #region rfa_modify_parameter

        /// <summary>
        /// 修改既有族群參數：renameTo（更名）、isInstance（切換實例／類型）、replaceWithShared（以共用參數取代）。
        /// 每筆在各自的子交易中執行，單筆失敗只回滾該筆。共用參數檔只讀不寫。
        /// </summary>
        private object RfaModifyParameter(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;

            var list = parameters["parameters"] as JArray;
            if (list == null || list.Count == 0)
                throw new Exception("請提供 parameters 陣列（每個元素含 name，以及 renameTo、isInstance、replaceWithShared 其中至少一項）");

            string sharedFile = parameters["sharedParameterFile"]?.Type == JTokenType.String
                ? parameters["sharedParameterFile"].Value<string>() : null;

            var modified = new List<object>();
            var unchanged = new List<object>();
            var errors = new List<object>();
            string sharedFileUsed = null;

            using (var shared = new RfaSharedParamSource(doc.Application, sharedFile))
            {
                using (var t = new Transaction(doc, "MCP: rfa_modify_parameter"))
                {
                    t.Start();

                    foreach (JToken token in list)
                    {
                        var p = token as JObject;
                        if (p == null) { errors.Add(new { name = (string)null, error = "每個元素必須是物件" }); continue; }

                        string name = p["name"]?.Type == JTokenType.String ? p["name"].Value<string>() : null;
                        if (string.IsNullOrWhiteSpace(name)) { errors.Add(new { name, error = "缺少 name" }); continue; }

                        // ── 驗證輸入（不碰 Revit） ──
                        string renameTo = null;
                        var renTok = p["renameTo"];
                        if (renTok != null && renTok.Type != JTokenType.Null)
                        {
                            renameTo = renTok.Type == JTokenType.String ? renTok.Value<string>()?.Trim() : null;
                            if (string.IsNullOrEmpty(renameTo)) { errors.Add(new { name, error = "renameTo 必須是非空字串" }); continue; }
                        }

                        bool? wantInstance = null;
                        var instTok = p["isInstance"];
                        if (instTok != null && instTok.Type != JTokenType.Null)
                        {
                            if (instTok.Type != JTokenType.Boolean) { errors.Add(new { name, error = "isInstance 必須是 true / false" }); continue; }
                            wantInstance = instTok.Value<bool>();
                        }

                        JObject replace = null;
                        var repTok = p["replaceWithShared"];
                        if (repTok != null && repTok.Type != JTokenType.Null)
                        {
                            replace = repTok as JObject;
                            if (replace == null) { errors.Add(new { name, error = "replaceWithShared 必須是物件 { name?, sharedGuid? }" }); continue; }
                        }

                        if (renameTo == null && wantInstance == null && replace == null)
                        {
                            errors.Add(new { name, error = "未指定任何修改（renameTo、isInstance、replaceWithShared 至少一項）" });
                            continue;
                        }
                        if (renameTo != null && replace != null)
                        {
                            errors.Add(new { name, error = "renameTo 與 replaceWithShared 不可同時使用（共用參數的名稱由共用參數定義決定，且共用參數不能更名）" });
                            continue;
                        }

                        FamilyParameter fp = RfaFindParam(fm, name);
                        if (fp == null) { errors.Add(new { name, error = $"找不到族群參數「{name}」" }); continue; }

                        var changes = new List<object>();
                        var notes = new List<string>();
                        string currentName = name;
                        List<string> formulasNowReferencing = null;

                        using (var st = new SubTransaction(doc))
                        {
                            st.Start();
                            try
                            {
                                // ── 更名 ──
                                if (renameTo != null)
                                {
                                    if (renameTo == name)
                                    {
                                        notes.Add("renameTo 與目前名稱相同，未更名");
                                    }
                                    else
                                    {
                                        if (RfaFindParam(fm, renameTo) != null)
                                            throw new Exception($"已有名為「{renameTo}」的族群參數，無法更名");
                                        try { fm.RenameParameter(fp, renameTo); }
                                        catch (Exception ex) { throw new Exception($"更名失敗（內建參數與共用參數不能更名）：{ex.Message}"); }
                                        currentName = renameTo;
                                        fp = RfaFindParam(fm, currentName) ?? fp;
                                        formulasNowReferencing = RfaParamsReferencing(fm, currentName)
                                            .Select(x => x.Definition.Name).ToList();
                                        changes.Add(new { action = "rename", from = name, to = currentName });
                                    }
                                }

                                // ── 以共用參數取代（保留群組；實例／類型依 isInstance，未指定則維持現狀） ──
                                if (replace != null)
                                {
                                    string defName = replace["name"]?.Type == JTokenType.String ? replace["name"].Value<string>()?.Trim() : null;
                                    if (string.IsNullOrEmpty(defName)) defName = currentName;
                                    string guidText = replace["sharedGuid"]?.Type == JTokenType.String ? replace["sharedGuid"].Value<string>() : null;

                                    DefinitionFile defFile = shared.Open();
                                    sharedFileUsed = shared.FilePath;
                                    ExternalDefinition exDef = RfaFindSharedDefinition(defFile, defName, guidText);

                                    if (fp.IsShared && RfaParamGuid(fp) == exDef.GUID.ToString())
                                    {
                                        notes.Add($"已是 GUID {exDef.GUID} 的共用參數，未取代");
                                    }
                                    else
                                    {
                                        if (exDef.Name != currentName && RfaFindParam(fm, exDef.Name) != null)
                                            throw new Exception($"已有名為「{exDef.Name}」的族群參數，無法以同名共用參數取代「{currentName}」");

                                        ForgeTypeId group;
                                        try { group = fp.Definition.GetGroupTypeId() ?? new ForgeTypeId(string.Empty); }
                                        catch { group = new ForgeTypeId(string.Empty); }
                                        bool targetInstance = wantInstance ?? fp.IsInstance;
                                        string previousType = RfaSpecName(fp.Definition.GetDataType());

                                        FamilyParameter replaced;
                                        try { replaced = fm.ReplaceParameter(fp, exDef, group, targetInstance); }
                                        catch (Exception ex) { throw new Exception($"以共用參數取代失敗：{ex.Message}"); }
                                        fp = replaced ?? RfaFindParam(fm, exDef.Name) ?? throw new Exception("取代後找不到新的共用參數");
                                        currentName = fp.Definition.Name;

                                        changes.Add(new
                                        {
                                            action = "replaceWithShared",
                                            definition = exDef.Name,
                                            guid = exDef.GUID.ToString(),
                                            definitionGroup = exDef.OwnerGroup?.Name,
                                            previousType,
                                            type = RfaSpecName(exDef.GetDataType()),
                                            isInstance = fp.IsInstance,
                                        });
                                        formulasNowReferencing = RfaParamsReferencing(fm, currentName)
                                            .Select(x => x.Definition.Name).ToList();
                                        wantInstance = null; // 已在 ReplaceParameter 一併處理
                                    }
                                }

                                // ── 實例／類型切換 ──
                                if (wantInstance.HasValue)
                                {
                                    if (fp.IsInstance == wantInstance.Value)
                                    {
                                        notes.Add($"已是{(wantInstance.Value ? "實例" : "類型")}參數，未變更");
                                    }
                                    else
                                    {
                                        try
                                        {
                                            if (wantInstance.Value) fm.MakeInstance(fp);
                                            else fm.MakeType(fp);
                                        }
                                        catch (Exception ex)
                                        {
                                            throw new Exception($"切換為{(wantInstance.Value ? "實例" : "類型")}參數失敗：{ex.Message}");
                                        }
                                        fp = RfaFindParam(fm, currentName) ?? fp;
                                        changes.Add(new { action = wantInstance.Value ? "makeInstance" : "makeType" });
                                    }
                                }

                                st.Commit();

                                if (changes.Count == 0)
                                {
                                    unchanged.Add(new { name = currentName, reason = string.Join("；", notes) });
                                }
                                else
                                {
                                    modified.Add(new
                                    {
                                        name = currentName,
                                        previousName = name,
                                        changes,
                                        notes = notes.Count == 0 ? null : notes,
                                        formulasNowReferencing,
                                        guid = RfaParamGuid(fp),
                                        parameter = RfaDescribeParam(doc, fm, fp),
                                    });
                                }
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
                        throw new Exception($"rfa_modify_parameter 交易未成功提交（狀態：{status}），所有變更已復原");
                }
            }

            return new
            {
                modifiedCount = modified.Count,
                modified,
                unchanged,
                errors,
                sharedParameterFile = sharedFileUsed,
            };
        }

        #endregion

        #region rfa_delete_parameter

        /// <summary>單一族群參數的使用情況</summary>
        private sealed class RfaParamUsage
        {
            public string Name;
            public IdType Id;
            public bool IsBuiltIn;
            public bool IsShared;
            public string Guid;
            public bool IsInstance;
            public string Type;
            public string OwnFormula;
            public List<object> ReferencedByFormulas = new List<object>();
            public List<object> AssociatedElementParameters = new List<object>();
            public List<object> LabelledDimensions = new List<object>();

            public bool InUse => ReferencedByFormulas.Count > 0 || AssociatedElementParameters.Count > 0 || LabelledDimensions.Count > 0;

            public Dictionary<string, object> ToReport()
            {
                return new Dictionary<string, object>
                {
                    ["name"] = Name,
                    ["id"] = Id,
                    ["isBuiltIn"] = IsBuiltIn,
                    ["isShared"] = IsShared,
                    ["guid"] = Guid,
                    ["isInstance"] = IsInstance,
                    ["type"] = Type,
                    ["hasFormula"] = !string.IsNullOrEmpty(OwnFormula),
                    ["formula"] = string.IsNullOrEmpty(OwnFormula) ? null : OwnFormula,
                    ["referencedByFormulas"] = ReferencedByFormulas,
                    ["associatedElementParameters"] = AssociatedElementParameters,
                    ["labelledDimensions"] = LabelledDimensions,
                    ["inUse"] = InUse,
                };
            }
        }

        /// <summary>族群文件中所有尺寸標註的標籤參數 id → 標註清單</summary>
        private static Dictionary<IdType, List<object>> RfaDimensionLabels(Document doc)
        {
            var map = new Dictionary<IdType, List<object>>();
            foreach (Dimension dim in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                FamilyParameter label;
                try { label = dim.FamilyLabel; }
                catch { continue; }
                if (label == null) continue;

                IdType key = label.Id.GetIdValue();
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<object>();
                ElementId viewId = dim.OwnerViewId;
                View view = viewId != null && viewId != ElementId.InvalidElementId ? doc.GetElement(viewId) as View : null;
                list.Add(new
                {
                    dimensionId = dim.Id.GetIdValue(),
                    viewId = view?.Id.GetIdValue(),
                    viewName = view?.Name,
                });
            }
            return map;
        }

        private static RfaParamUsage RfaBuildUsage(FamilyManager fm, FamilyParameter fp, Dictionary<IdType, List<object>> dimLabels)
        {
            var u = new RfaParamUsage
            {
                Name = fp.Definition.Name,
                Id = fp.Id.GetIdValue(),
                IsBuiltIn = RfaIsBuiltInParam(fp),
                IsShared = fp.IsShared,
                Guid = RfaParamGuid(fp),
                IsInstance = fp.IsInstance,
                Type = RfaSpecName(fp.Definition.GetDataType()),
                OwnFormula = fp.Formula,
            };

            foreach (FamilyParameter other in RfaParamsReferencing(fm, u.Name))
                u.ReferencedByFormulas.Add(new { parameter = other.Definition.Name, formula = other.Formula });

            ParameterSet assoc = null;
            try { assoc = fp.AssociatedParameters; } catch { }
            if (assoc != null)
            {
                foreach (Parameter ep in assoc)
                {
                    Element owner = ep.Element;
                    u.AssociatedElementParameters.Add(new
                    {
                        elementId = owner?.Id.GetIdValue(),
                        elementKind = owner?.GetType().Name,
                        category = owner?.Category?.Name,
                        elementName = owner?.Name,
                        parameter = ep.Definition?.Name,
                    });
                }
            }

            if (dimLabels.TryGetValue(u.Id, out var dims))
                u.LabelledDimensions.AddRange(dims);

            return u;
        }

        /// <summary>
        /// 刪除族群參數。預設 dryRun 只回報使用情況；實際刪除時，仍被公式、元素參數關聯或尺寸標籤使用的參數
        /// 需 force: true 才刪。同一次要刪的多個參數會反覆檢查：某參數只被另一個也要刪的參數引用時，
        /// 後者刪除後前者即可刪除。內建參數不可刪除。
        /// </summary>
        private object RfaDeleteParameter(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;

            var namesArr = parameters["names"] as JArray;
            if (namesArr == null || namesArr.Count == 0)
                throw new Exception("請提供 names 陣列（要刪除的族群參數名稱）");

            bool dryRun = parameters["dryRun"]?.Type == JTokenType.Boolean ? parameters["dryRun"].Value<bool>() : true;
            bool force = parameters["force"]?.Type == JTokenType.Boolean && parameters["force"].Value<bool>();

            var names = new List<string>();
            var errors = new List<object>();
            foreach (JToken tok in namesArr)
            {
                string n = tok.Type == JTokenType.String ? tok.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(n)) { errors.Add(new { name = n, error = "名稱必須是非空字串" }); continue; }
                if (!names.Contains(n)) names.Add(n);
            }

            var notFound = new List<string>();
            var reports = new List<Dictionary<string, object>>();
            var refused = new List<object>();
            var deleted = new List<string>();
            var sideEffects = new List<object>();
            var candidates = new List<string>();

            var dimLabels = RfaDimensionLabels(doc);
            foreach (string name in names)
            {
                FamilyParameter fp = RfaFindParam(fm, name);
                if (fp == null) { notFound.Add(name); continue; }

                RfaParamUsage u = RfaBuildUsage(fm, fp, dimLabels);
                var report = u.ToReport();
                string reason = null;
                bool wouldDelete;
                if (u.IsBuiltIn)
                {
                    wouldDelete = false;
                    reason = "內建參數不可刪除";
                }
                else if (u.InUse && !force)
                {
                    // 若引用它的只有同批也要刪的參數，實際執行時仍可能刪得掉（見下方多輪檢查）
                    wouldDelete = false;
                    reason = "仍被使用（公式引用／元素參數關聯／尺寸標籤），需 force: true 才刪除";
                }
                else
                {
                    wouldDelete = true;
                    if (u.InUse) reason = "仍被使用，但 force: true：刪除時 Revit 會一併移除引用它的公式、解除關聯與尺寸標籤";
                }
                report["wouldDelete"] = wouldDelete;
                report["reason"] = reason;
                reports.Add(report);

                if (u.IsBuiltIn)
                {
                    if (!dryRun) refused.Add(new { name, reason, usage = report });
                    continue;
                }
                candidates.Add(name);
            }

            if (dryRun)
            {
                return new
                {
                    dryRun = true,
                    force,
                    deleted,
                    refused,
                    notFound,
                    errors,
                    reports,
                    wouldDeleteCount = reports.Count(r => (bool)r["wouldDelete"]),
                };
            }

            if (candidates.Count > 0)
            {
                using (var t = new Transaction(doc, "MCP: rfa_delete_parameter"))
                {
                    t.Start();

                    var pending = new List<string>(candidates);
                    var latestUsage = new Dictionary<string, Dictionary<string, object>>();
                    bool progress = true;
                    while (progress && pending.Count > 0)
                    {
                        progress = false;
                        var labels = RfaDimensionLabels(doc);
                        foreach (string name in pending.ToList())
                        {
                            FamilyParameter fp = RfaFindParam(fm, name);
                            if (fp == null)
                            {
                                errors.Add(new { name, error = "刪除其他參數後找不到此參數" });
                                pending.Remove(name);
                                continue;
                            }

                            RfaParamUsage u = RfaBuildUsage(fm, fp, labels);
                            latestUsage[name] = u.ToReport();
                            if (u.InUse && !force) continue;

                            var formulasBefore = fm.Parameters.Cast<FamilyParameter>()
                                .Where(x => x.Definition.Name != name)
                                .ToDictionary(x => x.Definition.Name, x => x.Formula);

                            using (var st = new SubTransaction(doc))
                            {
                                st.Start();
                                try
                                {
                                    try { fm.RemoveParameter(fp); }
                                    catch (Exception ex) { throw new Exception($"刪除失敗：{ex.Message}"); }
                                    st.Commit();

                                    deleted.Add(name);
                                    progress = true;
                                    // 刪除後其他參數狀態可能改變，重新收集標籤
                                    labels = RfaDimensionLabels(doc);

                                    var cleared = new List<string>();
                                    foreach (FamilyParameter x in fm.Parameters)
                                    {
                                        if (formulasBefore.TryGetValue(x.Definition.Name, out string before)
                                            && !string.IsNullOrEmpty(before) && before != x.Formula)
                                            cleared.Add(x.Definition.Name);
                                    }
                                    if (u.InUse || cleared.Count > 0)
                                    {
                                        sideEffects.Add(new
                                        {
                                            name,
                                            formulasChanged = cleared,
                                            disassociatedElementParameters = u.AssociatedElementParameters.Count,
                                            unlabelledDimensions = u.LabelledDimensions.Count,
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                                    errors.Add(new { name, error = ex.Message });
                                }
                            }
                            pending.Remove(name);
                        }
                    }

                    foreach (string name in pending)
                    {
                        latestUsage.TryGetValue(name, out var usage);
                        refused.Add(new
                        {
                            name,
                            reason = "仍被使用（公式引用／元素參數關聯／尺寸標籤），未刪除；確認後可傳 force: true 強制刪除",
                            usage,
                        });
                    }

                    TransactionStatus status = t.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new Exception($"rfa_delete_parameter 交易未成功提交（狀態：{status}），所有變更已復原");
                }
            }

            return new
            {
                dryRun = false,
                force,
                deletedCount = deleted.Count,
                deleted,
                refused,
                notFound,
                errors,
                sideEffects,
                reports,
            };
        }

        #endregion
    }
}
