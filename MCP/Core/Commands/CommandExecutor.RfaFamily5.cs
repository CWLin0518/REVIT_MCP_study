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
    /// RFA 族群編輯工具第五批：開新族群（rfa_new_family）與開啟族群（rfa_open_family）。
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
        private static string RfaDescribeAvailableTemplates(string root, List<string> all)
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
            string shown = string.Join(", ", names.Take(RfaTemplateListLimit));
            string more = names.Count > RfaTemplateListLimit ? $" …（共 {names.Count} 個，只列前 {RfaTemplateListLimit} 個）" : $"（共 {names.Count} 個）";
            return $"搜尋的資料夾: {root}（含子資料夾）。可用樣板: {shown}{more}";
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
                    throw new Exception($"找不到族群樣板: {template}。{RfaDescribeAvailableTemplates(templateRoot, RfaListTemplates(templateRoot))}");
            }
            else
            {
                string wanted = templateName.EndsWith(".rft", StringComparison.OrdinalIgnoreCase)
                    ? templateName.Substring(0, templateName.Length - 4).Trim()
                    : templateName;
                List<string> all = RfaListTemplates(templateRoot);
                var matches = all.Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                    throw new Exception($"找不到名為「{wanted}」的族群樣板。{RfaDescribeAvailableTemplates(templateRoot, all)}");
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
    }
}
