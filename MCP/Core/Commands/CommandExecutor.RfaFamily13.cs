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
    /// RFA 族群編輯工具第十三批：匯入 CAD 幾何到作用中的族群文件。
    /// - rfa_import_cad：以 Document.Import 把 SAT / 3DM / DWG / DXF 匯入族群檔，
    ///   成為族群內的 ImportInstance（非參數化的匯入幾何）。
    ///   用途例：Rhino 匯出的 SAT（Brep）家具外形放進「Metric Furniture」族群。
    /// 使用的 API（SATImportOptions、ImportOptions3DM、DWGImportOptions、ImportUnit、ImportPlacement）
    /// 在 Revit 2022–2027 的 RevitAPI.dll 皆存在，所以 R22–R27 共用同一份程式碼。
    /// </summary>
    public partial class CommandExecutor
    {
        #region rfa_import_cad

        private static readonly string[] RfaImportExtensions = { ".sat", ".3dm", ".dwg", ".dxf" };

        private static ImportUnit RfaParseImportUnit(string s)
        {
            switch ((s ?? "auto").Trim().ToLowerInvariant())
            {
                case "":
                case "auto":
                case "default": return ImportUnit.Default;
                case "mm":
                case "millimeter":
                case "millimeters": return ImportUnit.Millimeter;
                case "cm":
                case "centimeter":
                case "centimeters": return ImportUnit.Centimeter;
                case "dm":
                case "decimeter":
                case "decimeters": return ImportUnit.Decimeter;
                case "m":
                case "meter":
                case "meters": return ImportUnit.Meter;
                case "in":
                case "inch":
                case "inches": return ImportUnit.Inch;
                case "ft":
                case "foot":
                case "feet": return ImportUnit.Foot;
                default:
                    throw new Exception($"不支援的 units：{s}（可用：auto, millimeter, centimeter, decimeter, meter, inch, foot）");
            }
        }

        private static ImportPlacement RfaParseImportPlacement(string s)
        {
            switch ((s ?? "origin").Trim().ToLowerInvariant())
            {
                case "":
                case "origin": return ImportPlacement.Origin;
                case "center":
                case "centered": return ImportPlacement.Centered;
                default:
                    throw new Exception($"不支援的 placement：{s}（可用：origin（原點對原點，預設）, center（置中））");
            }
        }

        /// <summary>
        /// 匯入用視圖：有給 viewName 就照名稱找；否則優先非透視的 3D 視圖，其次樓板平面、天花板平面、立面。
        /// </summary>
        private static View RfaPickImportView(Document doc, string viewName)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser)
                .ToList();

            if (!string.IsNullOrWhiteSpace(viewName))
            {
                View named = views.FirstOrDefault(v => v.Name == viewName);
                if (named != null) return named;
                string available = string.Join(", ", views
                    .Where(v => v is View3D || v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan ||
                                v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Section)
                    .Select(v => $"「{v.Name}」({v.ViewType})"));
                throw new Exception($"找不到視圖「{viewName}」。可用視圖：{available}");
            }

            View pick = views.OfType<View3D>().FirstOrDefault(v => !v.IsPerspective)
                        ?? views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
                        ?? views.FirstOrDefault(v => v.ViewType == ViewType.CeilingPlan)
                        ?? views.FirstOrDefault(v => v.ViewType == ViewType.Elevation);
            if (pick == null)
                throw new Exception("族群檔中找不到可用來匯入的 3D、平面或立面視圖，請以 viewName 指定");
            return pick;
        }

        /// <summary>統計匯入幾何的組成（實體／面／網格／曲線），用來確認 SAT 進來的是 Brep 而不是空殼</summary>
        private static object RfaSummarizeImportGeometry(Element el)
        {
            int solids = 0, faces = 0, meshes = 0, curves = 0;
            double volumeFt3 = 0;
            void Walk(GeometryElement ge, int depth)
            {
                if (ge == null || depth > 8) return;
                foreach (GeometryObject go in ge)
                {
                    switch (go)
                    {
                        case Solid s when s.Faces.Size > 0:
                            solids++;
                            faces += s.Faces.Size;
                            volumeFt3 += RfaSafe(() => s.Volume, 0.0);
                            break;
                        case Mesh _: meshes++; break;
                        case Curve _: curves++; break;
                        case PolyLine _: curves++; break;
                        case GeometryInstance gi: Walk(gi.GetInstanceGeometry(), depth + 1); break;
                    }
                }
            }
            try
            {
                Walk(el.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine }), 0);
            }
            catch (Exception ex)
            {
                return new { error = "讀取匯入幾何失敗：" + ex.Message };
            }
            return new
            {
                solidCount = solids,
                faceCount = faces,
                meshCount = meshes,
                curveCount = curves,
                solidVolumeM3 = Math.Round(UnitUtils.ConvertFromInternalUnits(volumeFt3, UnitTypeId.CubicMeters), 6),
            };
        }

        private object RfaImportCad(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();

            // ── 1. 檔案檢查 ──
            string filePath = parameters["filePath"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(filePath))
                throw new Exception("請提供 filePath（要匯入的 CAD 檔絕對路徑：.sat / .3dm / .dwg / .dxf）");
            if (!Path.IsPathRooted(filePath))
                throw new Exception($"filePath 必須是絕對路徑，收到：{filePath}");
            if (!File.Exists(filePath))
                throw new Exception($"找不到檔案：{filePath}");
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (!RfaImportExtensions.Contains(ext))
                throw new Exception($"不支援的副檔名「{ext}」。rfa_import_cad 支援：{string.Join(", ", RfaImportExtensions)}");

            // ── 2. 選項 ──
            string unitsInput = parameters["units"]?.Value<string>() ?? "auto";
            ImportUnit unit = RfaParseImportUnit(unitsInput);
            string placementInput = parameters["placement"]?.Value<string>() ?? "origin";
            ImportPlacement placement = RfaParseImportPlacement(placementInput);
            bool thisViewOnly = parameters["thisViewOnly"]?.Value<bool>() ?? false;

            View view = RfaPickImportView(doc, parameters["viewName"]?.Value<string>());
            if (thisViewOnly && view is View3D)
                throw new Exception($"視圖「{view.Name}」是 3D 視圖，不支援 thisViewOnly=true。3D 幾何請保持 thisViewOnly=false，或改指定平面／立面視圖");

            var warnings = new List<string>();
            if (unit == ImportUnit.Default)
                warnings.Add("units=auto：由 Revit 依檔案自行判斷單位。SAT／3DM 的單位判斷不一定可靠，建議明確指定（例如 Rhino 以 cm 建模就給 centimeter），並核對回傳的 boundingBoxMm 尺寸。");
            if (ext == ".sat" || ext == ".3dm")
            {
                if (thisViewOnly)
                    warnings.Add("3D 幾何以 thisViewOnly=true 匯入時只會出現在該視圖，不會成為族群的 3D 形體。");
            }

            // ── 3. 匯入 ──
            ElementId importedId = ElementId.InvalidElementId;
            string format;
            using (var t = new Transaction(doc, "MCP: rfa_import_cad"))
            {
                var fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
                t.SetFailureHandlingOptions(fho);
                t.Start();
                try
                {
                    switch (ext)
                    {
                        case ".sat":
                            {
                                format = "SAT";
                                var opts = new SATImportOptions
                                {
                                    Unit = unit,
                                    Placement = placement,
                                    ThisViewOnly = thisViewOnly,
                                };
                                importedId = doc.Import(filePath, opts, view);
                                break;
                            }
                        case ".3dm":
                            {
                                format = "3DM";
                                var opts = new ImportOptions3DM
                                {
                                    Unit = unit,
                                    Placement = placement,
                                    ThisViewOnly = thisViewOnly,
                                };
                                importedId = doc.Import(filePath, opts, view);
                                break;
                            }
                        default:
                            {
                                format = ext == ".dxf" ? "DXF" : "DWG";
                                var opts = new DWGImportOptions
                                {
                                    Unit = unit,
                                    Placement = placement,
                                    ThisViewOnly = thisViewOnly,
                                };
                                bool ok = doc.Import(filePath, opts, view, out importedId);
                                if (!ok)
                                {
                                    t.RollBack();
                                    throw new Exception($"Document.Import 回傳 false（{format}），沒有匯入任何東西。請檢查檔案是否損毀或版本過新、視圖「{view.Name}」是否適合匯入。");
                                }
                                break;
                            }
                    }
                }
                catch (Exception ex) when (t.GetStatus() == TransactionStatus.Started)
                {
                    t.RollBack();
                    throw new Exception($"匯入失敗：{ex.Message}（檔案：{Path.GetFileName(filePath)}，視圖：「{view.Name}」({view.ViewType})。SAT 需為 Brep 實體／曲面；網格無法匯出成 SAT）");
                }

                if (importedId == null || importedId == ElementId.InvalidElementId || doc.GetElement(importedId) == null)
                {
                    t.RollBack();
                    throw new Exception($"Document.Import 沒有回傳有效的元素（{format}），交易已復原。請檢查檔案內容是否為空、或單位設定是否造成幾何過大／過小。");
                }

                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                    throw new Exception($"rfa_import_cad 交易未成功提交（狀態：{status}），匯入已復原");
            }

            // ── 4. 讀回結果 ──
            Element el = doc.GetElement(importedId);
            if (el == null)
                throw new Exception("匯入後讀不到元素（可能在提交時被 Revit 移除），請檢查檔案內容");

            BoundingBoxXYZ bb = RfaSafe(() => el.get_BoundingBox(null));
            object bbox = null, sizeMm = null;
            if (bb != null)
            {
                bbox = new { min = RfaPt(bb.Min), max = RfaPt(bb.Max) };
                sizeMm = new[] { RfaToMm(bb.Max.X - bb.Min.X), RfaToMm(bb.Max.Y - bb.Min.Y), RfaToMm(bb.Max.Z - bb.Min.Z) };
            }
            else
            {
                warnings.Add("讀不到匯入元素的邊界框（boundingBoxMm 為 null）。");
            }

            var imp = el as ImportInstance;
            string unitUsed = unit.ToString();
            return new
            {
                id = importedId.GetIdValue(),
                elementClass = el.GetType().Name,
                category = RfaSafe(() => el.Category?.Name),
                isLinked = imp != null ? (bool?)RfaSafe(() => imp.IsLinked) : null,
                format,
                fileName = Path.GetFileName(filePath),
                unitsRequested = unitsInput,
                unitsUsed = unitUsed,
                placement = placement.ToString(),
                view = new { name = view.Name, id = view.Id.GetIdValue(), viewType = view.ViewType.ToString() },
                thisViewOnly,
                boundingBoxMm = bbox,
                sizeMm,
                geometry = RfaSummarizeImportGeometry(el),
                note = "匯入幾何是非參數化的 ImportInstance：不會跟著族群參數變形；要指定子類別／可見性請在 Revit 中處理（rfa_set_form_visibility 只處理擠出等形體）。",
                warnings,
            };
        }

        #endregion
    }
}
