using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// RFA 族群編輯工具第五批：開新族群（rfa_new_family）、開啟族群（rfa_open_family）
    /// 與載入至專案（rfa_load_into_project）。
    ///
    /// rfa_load_into_project 以 Document.LoadFamily(Document, IFamilyLoadOptions) 把作用中族群載入同一工作階段中
    /// 已開啟的專案；預設不切換作用中文件，族群編輯器仍是作用中文件，可繼續修改後再載入。
    ///
    /// 其他 rfa_* 工具都作用在 _uiApp.ActiveUIDocument，所以這兩支的目標是讓族群文件「成為作用中文件」。
    /// NewFamilyDocument 回傳的是只在記憶體中的文件，無法直接在 UI 啟用；因此流程是
    /// 先 SaveAs 到 savePath、Close(false)，再用 OpenAndActivateDocument 從磁碟開啟並啟用。
    ///
    /// OpenAndActivateDocument 對呼叫情境有限制（不可有進行中的交易、部分事件處理器內不可呼叫）。
    /// 本專案的命令在 IExternalEventHandler.Execute 內執行（見 ExternalEventManager / Application.OnCommandReceived），
    /// 一般可以呼叫；但仍以 try/catch 包住，失敗時回報實際例外訊息、檔案是否已寫入磁碟，並請使用者手動開啟。
    /// </summary>
    public partial class CommandExecutor
    {
        /// <summary>族群樣板清單最多列出的筆數（找不到樣板時的提示用）</summary>
        private const int RfaTemplateListLimit = 30;

        /// <summary>兩個路徑是否指向同一個檔案（正規化後不分大小寫比對）</summary>
        private static bool RfaSamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>在 Revit 目前開啟的文件中找路徑相同者；沒有則回傳 null</summary>
        private Document RfaFindOpenDocument(string path)
        {
            foreach (Document d in _uiApp.Application.Documents)
            {
                string p = null;
                try { p = d.PathName; } catch { }
                if (RfaSamePath(p, path)) return d;
            }
            return null;
        }

        /// <summary>若作用中文件有尚未結束的交易，不能切換文件</summary>
        private void RfaEnsureNoOpenTransaction()
        {
            Document active = _uiApp.ActiveUIDocument?.Document;
            if (active != null && active.IsModifiable)
                throw new Exception($"作用中的文件「{active.Title}」有尚未結束的交易，無法開啟或切換文件。請先在 Revit 中完成或取消目前的編輯。");
        }

        /// <summary>族群文件摘要（開啟／新建後回傳用）</summary>
        private static Dictionary<string, object> RfaFamilySummary(Document doc)
        {
            Family fam = doc.OwnerFamily;
            FamilyManager fm = doc.FamilyManager;
            var types = fm.Types.Cast<FamilyType>().Select(t => t.Name).ToList();
            var refPlanes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Select(rp => rp.Name).ToList();
            return new Dictionary<string, object>
            {
                ["path"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                ["title"] = doc.Title,
                ["category"] = fam?.FamilyCategory?.Name,
                ["familyPlacementType"] = fam?.FamilyPlacementType.ToString(),
                ["currentType"] = fm.CurrentType?.Name,
                ["typeCount"] = types.Count,
                ["types"] = types,
                ["referencePlanes"] = refPlanes,
            };
        }

        #region rfa_new_family

        /// <summary>列出 FamilyTemplatePath 底下所有 .rft（遞迴；無權限的子資料夾略過）</summary>
        private static List<string> RfaListTemplates(string root)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;

            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try { result.AddRange(Directory.GetFiles(dir, "*.rft")); } catch { }
                try { foreach (string sub in Directory.GetDirectories(dir)) stack.Push(sub); } catch { }
            }
            return result.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>找不到樣板時的說明：搜尋的資料夾、資料夾狀態、可用樣板名稱（最多 30 個）</summary>
        private static string RfaDescribeAvailableTemplates(string root, List<string> all, string wanted = null)
        {
            if (string.IsNullOrWhiteSpace(root))
                return "Revit 的族群樣板預設路徑（FamilyTemplatePath）未設定，無法以 templateName 搜尋；請改傳 templatePath（.rft 完整路徑），或在 Revit 選項 > 檔案位置 設定族群樣板資料夾。";
            if (!Directory.Exists(root))
                return $"搜尋的族群樣板資料夾不存在: {root}。請改傳 templatePath（.rft 完整路徑），或在 Revit 選項 > 檔案位置 修正族群樣板資料夾。";
            if (all.Count == 0)
                return $"搜尋的族群樣板資料夾 {root}（含子資料夾）裡沒有任何 .rft。請改傳 templatePath（.rft 完整路徑）。";

            var names = all.Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(wanted))
            {
                string shownAll = string.Join(", ", names.Take(RfaTemplateListLimit));
                string moreAll = names.Count > RfaTemplateListLimit ? $" …（共 {names.Count} 個，只列前 {RfaTemplateListLimit} 個）" : $"（共 {names.Count} 個）";
                return $"搜尋的資料夾: {root}（含子資料夾）。可用樣板: {shownAll}{moreAll}";
            }

            List<string> ranked = RfaRankTemplateNames(names, wanted.Trim());
            if (ranked.Count == 0)
                return $"搜尋的資料夾: {root}（含子資料夾），共 {names.Count} 個樣板，沒有任何名稱與「{wanted.Trim()}」相近。請確認樣板名稱（可在 Revit 的新建族群對話框查看），或改傳 templatePath。";
            return $"搜尋的資料夾: {root}（含子資料夾），共 {names.Count} 個樣板。與「{wanted.Trim()}」最接近的 {ranked.Count} 個: {string.Join(", ", ranked)}";
        }

        /// <summary>
        /// 樣板名稱排序（最多 RfaTemplateListLimit 個）：先列含有要求名稱任一空白分隔詞（不分大小寫）的樣板，
        /// 符合詞數多者在前；不足時再以「與要求名稱共同前綴最長」者補足（共同前綴為 0 的不列）。
        /// </summary>
        private static List<string> RfaRankTemplateNames(List<string> names, string wanted)
        {
            var tokens = wanted.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = names
                .Select(n => new { n, hits = tokens.Count(tk => n.IndexOf(tk, StringComparison.OrdinalIgnoreCase) >= 0) })
                .Where(x => x.hits > 0)
                .OrderByDescending(x => x.hits)
                .ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.n)
                .Take(RfaTemplateListLimit)
                .ToList();

            if (result.Count < RfaTemplateListLimit)
            {
                int CommonPrefix(string a)
                {
                    int i = 0, max = Math.Min(a.Length, wanted.Length);
                    while (i < max && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(wanted[i])) i++;
                    return i;
                }
                var chosen = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
                result.AddRange(names
                    .Where(n => !chosen.Contains(n))
                    .Select(n => new { n, len = CommonPrefix(n) })
                    .Where(x => x.len > 0)
                    .OrderByDescending(x => x.len)
                    .ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.n)
                    .Take(RfaTemplateListLimit - result.Count));
            }
            return result;
        }

        private object RfaNewFamily(JObject parameters)
        {
            Autodesk.Revit.ApplicationServices.Application app = _uiApp.Application;

            // ---- 解析樣板 ----
            string templatePath = parameters["templatePath"]?.Value<string>()?.Trim();
            string templateName = parameters["templateName"]?.Value<string>()?.Trim();
            bool hasPath = !string.IsNullOrEmpty(templatePath);
            bool hasName = !string.IsNullOrEmpty(templateName);
            if (hasPath == hasName)
                throw new Exception("templatePath 與 templateName 必須擇一提供（不可都給、也不可都不給）");

            string templateRoot = null;
            try { templateRoot = app.FamilyTemplatePath; } catch { }

            string template;
            if (hasPath)
            {
                if (!Path.IsPathRooted(templatePath))
                    throw new Exception($"templatePath 必須是完整路徑，收到: {templatePath}");
                if (!Path.GetExtension(templatePath).Equals(".rft", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"templatePath 副檔名必須是 .rft，收到: {Path.GetExtension(templatePath)}");
                template = Path.GetFullPath(templatePath);
                if (!File.Exists(template))
                    throw new Exception($"找不到族群樣板: {template}。{RfaDescribeAvailableTemplates(templateRoot, RfaListTemplates(templateRoot), Path.GetFileNameWithoutExtension(template))}");
            }
            else
            {
                string wanted = templateName.EndsWith(".rft", StringComparison.OrdinalIgnoreCase)
                    ? templateName.Substring(0, templateName.Length - 4).Trim()
                    : templateName;
                List<string> all = RfaListTemplates(templateRoot);
                var matches = all.Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                    throw new Exception($"找不到名為「{wanted}」的族群樣板。{RfaDescribeAvailableTemplates(templateRoot, all, wanted)}");
                if (matches.Count > 1)
                    throw new Exception($"有 {matches.Count} 個樣板都叫「{wanted}」，請改用 templatePath 指定其中一個: {string.Join(" | ", matches)}");
                template = matches[0];
            }

            // ---- 驗證存檔路徑 ----
            string savePath = RfaResolveRfaSavePath(parameters["savePath"]?.Value<string>(), "savePath");
            bool overwrite = parameters["overwrite"]?.Value<bool>() ?? false;

            Document openAtTarget = RfaFindOpenDocument(savePath);
            if (openAtTarget != null)
                throw new Exception($"savePath 是 Revit 目前已開啟的文件「{openAtTarget.Title}」({savePath})，不能覆蓋開啟中的檔案。請換一個檔名，或先在 Revit 中關閉該文件。");

            bool existed = File.Exists(savePath);
            if (existed && !overwrite)
                throw new Exception($"檔案已存在: {savePath}。確定要覆蓋請傳 overwrite: true，或換一個檔名");

            RfaEnsureNoOpenTransaction();

            // ---- 建立 → 存檔 → 關閉 ----
            Document famDoc = app.NewFamilyDocument(template)
                              ?? throw new Exception($"Revit 無法以樣板建立族群文件（NewFamilyDocument 回傳 null）: {template}");
            try
            {
                famDoc.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = overwrite });
            }
            catch (Exception ex)
            {
                try { famDoc.Close(false); } catch { }
                throw new Exception($"族群已在記憶體中建立，但存檔到 {savePath} 失敗：{ex.Message}。檔案{(File.Exists(savePath) ? (existed ? "（原本就存在的舊檔）仍在磁碟上" : "可能已部分寫入，請檢查") : "未寫入磁碟")}。");
            }

            string closeWarning = null;
            try
            {
                famDoc.Close(false);
            }
            catch (Exception ex)
            {
                // 已存檔但關不掉：OpenAndActivateDocument 遇到同路徑已開啟的文件時會改為啟用它，仍可繼續
                closeWarning = $"存檔後關閉記憶體中的族群文件失敗（{ex.Message}），改為直接啟用。";
            }

            // ---- 從磁碟開啟並啟用 ----
            UIDocument uidoc;
            try
            {
                uidoc = _uiApp.OpenAndActivateDocument(savePath);
            }
            catch (Exception ex)
            {
                bool onDisk = File.Exists(savePath);
                throw new Exception(
                    $"新族群已建立{(onDisk ? $"並存到 {savePath}（檔案在磁碟上）" : $"，但磁碟上找不到 {savePath}")}，" +
                    $"但無法在 Revit 中開啟並設為作用中文件：OpenAndActivateDocument 失敗（{ex.GetType().Name}: {ex.Message}）。" +
                    (onDisk ? $"請在 Revit 中手動開啟 {savePath}，再繼續使用其他 rfa_* 工具。" : "請重新執行 rfa_new_family。") +
                    (closeWarning != null ? " " + closeWarning : ""));
            }

            Document doc = uidoc?.Document;
            if (doc == null)
                throw new Exception($"新族群已存到 {savePath}，但 OpenAndActivateDocument 沒有回傳文件。請在 Revit 中手動開啟該檔案。");
            if (!doc.IsFamilyDocument)
                throw new Exception($"開啟 {savePath} 後得到的文件「{doc.Title}」不是族群檔，這不應發生。請在 Revit 中檢查該檔案。");

            var summary = RfaFamilySummary(doc);
            summary["template"] = template;
            summary["overwroteExisting"] = existed;
            if (closeWarning != null) summary["warning"] = closeWarning;
            summary["note"] = "新族群已存檔並成為 Revit 的作用中文件，可以直接接著使用其他 rfa_* 工具。";
            return summary;
        }

        #endregion

        #region rfa_open_family

        private object RfaOpenFamily(JObject parameters)
        {
            string path = parameters["filePath"]?.Value<string>()?.Trim();
            if (string.IsNullOrEmpty(path)) throw new Exception("請提供 filePath（.rfa 的完整路徑）");
            if (!Path.IsPathRooted(path)) throw new Exception($"filePath 必須是完整路徑，收到: {path}");
            if (!Path.GetExtension(path).Equals(".rfa", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"副檔名必須是 .rfa，收到: {Path.GetExtension(path)}");
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new Exception($"檔案不存在: {path}");

            bool wasAlreadyOpen = RfaFindOpenDocument(path) != null;

            RfaEnsureNoOpenTransaction();

            UIDocument uidoc;
            try
            {
                uidoc = _uiApp.OpenAndActivateDocument(path);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"無法在 Revit 中{(wasAlreadyOpen ? "切換到已開啟的" : "開啟")}族群 {path}：OpenAndActivateDocument 失敗（{ex.GetType().Name}: {ex.Message}）。" +
                    $"請在 Revit 中手動開啟該檔案，再繼續使用其他 rfa_* 工具。");
            }

            Document doc = uidoc?.Document;
            if (doc == null)
                throw new Exception($"OpenAndActivateDocument 沒有回傳文件: {path}。請在 Revit 中手動開啟該檔案。");
            if (!doc.IsFamilyDocument)
                throw new Exception($"開啟的文件「{doc.Title}」不是族群檔（{path}）。");

            var summary = RfaFamilySummary(doc);
            summary["wasAlreadyOpen"] = wasAlreadyOpen;
            summary["note"] = wasAlreadyOpen
                ? "族群原本就已開啟，已切換為 Revit 的作用中文件，可以直接接著使用其他 rfa_* 工具。"
                : "族群已開啟並成為 Revit 的作用中文件，可以直接接著使用其他 rfa_* 工具。";
            return summary;
        }

        #endregion

        #region rfa_load_into_project

        /// <summary>
        /// rfa_load_into_project 的載入選項：族群已存在於專案時一律重新載入，
        /// 是否覆蓋既有類型參數值由呼叫端的 overwriteParameterValues 決定。
        /// 共用巢狀族群一律採用族群檔內的版本（FamilySource.Family）。
        /// </summary>
        private class RfaLoadIntoProjectOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;
            public RfaLoadIntoProjectOptions(bool overwrite) { _overwrite = overwrite; }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwrite;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwrite;
                return true;
            }
        }

        /// <summary>可作為載入目標的已開啟專案文件：非族群檔、非連結文件</summary>
        private List<Document> RfaListOpenProjects()
        {
            var list = new List<Document>();
            foreach (Document d in _uiApp.Application.Documents)
            {
                if (d.IsFamilyDocument) continue;
                bool linked = false;
                try { linked = d.IsLinked; } catch { }
                if (linked) continue;
                list.Add(d);
            }
            return list;
        }

        /// <summary>文件標題去掉指定副檔名（不分大小寫）</summary>
        private static string RfaStripExtension(string title, string ext)
        {
            if (string.IsNullOrEmpty(title)) return title;
            return title.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? title.Substring(0, title.Length - ext.Length) : title;
        }

        /// <summary>
        /// 將作用中的族群文件載入到同一個 Revit 工作階段中已開啟的專案（等同族群編輯器的「載入至專案」）。
        /// 參數：
        ///   targetProject (string, optional): 專案標題（有無 .rvt 皆可）或完整路徑；省略時只有一個候選專案才自動選用
        ///   overwriteParameterValues (bool, 預設 false): 族群已存在時，是否以族群檔的類型參數值覆蓋專案中的值
        ///   activateProject (bool, 預設 false): 載入成功後切換到目標專案；失敗只記入 warnings，不讓工具失敗
        /// </summary>
        private object RfaLoadIntoProject(JObject parameters)
        {
            Document familyDoc = GetActiveFamilyDocument();

            string target = parameters["targetProject"]?.Value<string>()?.Trim();
            bool overwrite = parameters["overwriteParameterValues"]?.Value<bool>() ?? false;
            bool activate = parameters["activateProject"]?.Value<bool>() ?? false;
            var warnings = new List<string>();

            // ---- 解析目標專案 ----
            List<Document> candidates = RfaListOpenProjects();
            string candidateList = candidates.Count == 0
                ? "（目前沒有開啟任何專案）"
                : string.Join(", ", candidates.Select(d => $"「{d.Title}」"));

            Document project;
            if (string.IsNullOrEmpty(target))
            {
                if (candidates.Count == 0)
                    throw new Exception("Revit 目前沒有開啟任何專案（.rvt）可以載入族群。請先在 Revit 開啟目標專案，再執行 rfa_load_into_project。");
                if (candidates.Count > 1)
                    throw new Exception($"Revit 目前開啟了 {candidates.Count} 個專案，請以 targetProject 指定其中一個（標題或完整路徑）: {candidateList}");
                project = candidates[0];
            }
            else
            {
                string targetNoExt = RfaStripExtension(target, ".rvt");
                var matches = candidates.Where(d =>
                {
                    string title = d.Title ?? "";
                    string path = null;
                    try { path = d.PathName; } catch { }
                    return string.Equals(title, target, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(RfaStripExtension(title, ".rvt"), targetNoExt, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(path) && (string.Equals(path, target, StringComparison.OrdinalIgnoreCase) || (Path.IsPathRooted(target) && RfaSamePath(path, target))));
                }).ToList();
                if (matches.Count == 0)
                    throw new Exception($"找不到已開啟的專案「{target}」。可選的專案: {candidateList}");
                if (matches.Count > 1)
                    throw new Exception($"有 {matches.Count} 個已開啟的專案符合「{target}」，請改用完整路徑指定: {string.Join(" | ", matches.Select(d => string.IsNullOrEmpty(d.PathName) ? d.Title : d.PathName))}");
                project = matches[0];
            }

            if (project.IsReadOnly)
                throw new Exception($"目標專案「{project.Title}」是唯讀狀態，無法載入族群。");
            if (project.IsModifiable)
                throw new Exception($"目標專案「{project.Title}」有尚未結束的交易，無法載入族群。請先在 Revit 中完成或取消該專案中的編輯。");
            if (familyDoc.IsModifiable)
                throw new Exception($"族群文件「{familyDoc.Title}」有尚未結束的交易，無法載入。請先在 Revit 中完成或取消目前的編輯。");

            // ---- 載入前快照 ----
            string expectedName = RfaStripExtension(familyDoc.Title, ".rfa");
            // 先把 id / 名稱 / 類別抄成純值：重新載入時 Revit 可能換掉族群元素（id 改變），舊的 Family 物件之後不能再讀
            var familiesBefore = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>()
                .Select(f =>
                {
                    IdType? catId = null;
                    try { catId = f.FamilyCategory?.Id.GetIdValue(); } catch { }
                    return new { Id = f.Id.GetIdValue(), Name = f.Name, CategoryId = catId };
                })
                .ToList();

            if (string.IsNullOrEmpty(familyDoc.PathName))
                warnings.Add($"族群文件尚未存檔，專案會以目前的標題「{expectedName}」作為族群名稱。若要給它正式名稱，請先用 rfa_save_as 存檔再載入。");

            // ---- 載入 ----
            Family loaded;
            try
            {
                loaded = familyDoc.LoadFamily(project, new RfaLoadIntoProjectOptions(overwrite));
            }
            catch (Exception ex)
            {
                string cause = ex is Autodesk.Revit.Exceptions.InvalidOperationException
                    ? "最可能的原因：目標專案或族群文件正在交易中、族群文件尚未存檔，或族群類別不允許載入到該專案。"
                    : ex is Autodesk.Revit.Exceptions.ArgumentException
                        ? "最可能的原因：目標文件不是可載入族群的專案（例如唯讀、連結文件或另一個族群檔）。"
                        : "最可能的原因：目標專案正在交易中或為唯讀，或族群文件尚未存檔。";
                throw new Exception($"將族群「{familyDoc.Title}」載入專案「{project.Title}」失敗（{ex.GetType().Name}: {ex.Message}）。{cause}");
            }
            if (loaded == null)
                throw new Exception($"LoadFamily 沒有回傳族群（族群「{familyDoc.Title}」→ 專案「{project.Title}」）。可能是 IFamilyLoadOptions 拒絕了重新載入，或 Revit 判定無需載入。");

            // wasReload：載入前專案中已有同名、同類別的族群。
            // 不以「回傳的族群 id 是否在載入前的 id 集合中」判斷：實測重新載入（內容有變）時 Revit 可能給族群新的 id，類型 id 則不變。
            IdType loadedId = loaded.Id.GetIdValue();
            IdType? loadedCatId = null;
            try { loadedCatId = loaded.FamilyCategory?.Id.GetIdValue(); } catch { }
            var prior = familiesBefore.FirstOrDefault(f =>
                string.Equals(f.Name, loaded.Name, StringComparison.OrdinalIgnoreCase)
                && (f.CategoryId == null || loadedCatId == null || f.CategoryId == loadedCatId));
            bool wasReload = prior != null;
            bool familyIdChanged = prior != null && prior.Id != loadedId;
            if (!string.Equals(loaded.Name, expectedName, StringComparison.Ordinal))
                warnings.Add($"專案中的族群名稱「{loaded.Name}」與由族群文件標題推得的名稱「{expectedName}」不同。");

            var types = loaded.GetFamilySymbolIds()
                .Select(id => project.GetElement(id))
                .Where(e => e != null)
                .Select(e => new { name = e.Name, id = e.Id.GetIdValue() })
                .OrderBy(t => t.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ---- 切換到專案（選用） ----
            bool activated = false;
            if (activate)
            {
                string projPath = null;
                try { projPath = project.PathName; } catch { }
                if (string.IsNullOrEmpty(projPath))
                {
                    warnings.Add($"專案「{project.Title}」從未存檔（沒有檔案路徑），無法以 OpenAndActivateDocument 切換過去；族群已載入，請在 Revit 中手動切換到該專案視窗。");
                }
                else
                {
                    try
                    {
                        _uiApp.OpenAndActivateDocument(projPath);
                        activated = true;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"族群已載入，但切換到專案「{project.Title}」失敗（{ex.GetType().Name}: {ex.Message}）。請在 Revit 中手動切換。");
                    }
                }
            }

            string activeTitle = null;
            try { activeTitle = _uiApp.ActiveUIDocument?.Document?.Title; } catch { }

            return new Dictionary<string, object>
            {
                ["family"] = loaded.Name,
                ["familyId"] = loadedId,
                ["expectedFamilyName"] = expectedName,
                ["targetProject"] = project.Title,
                ["targetPath"] = string.IsNullOrEmpty(project.PathName) ? null : project.PathName,
                ["wasReload"] = wasReload,
                ["familyIdChanged"] = familyIdChanged,
                ["overwriteParameterValues"] = overwrite,
                ["category"] = loaded.FamilyCategory?.Name,
                ["typeCount"] = types.Count,
                ["types"] = types,
                ["activated"] = activated,
                ["activeDocument"] = activeTitle,
                ["warnings"] = warnings,
                ["note"] = activated
                    ? $"族群已載入專案「{project.Title}」，並已切換為作用中文件；要繼續編輯族群請用 rfa_open_family 切回族群檔。"
                    : $"族群已載入專案「{project.Title}」。族群編輯器仍是作用中文件，可繼續用 rfa_* 工具修改後再次載入。",
            };
        }

        #endregion
    }
}
