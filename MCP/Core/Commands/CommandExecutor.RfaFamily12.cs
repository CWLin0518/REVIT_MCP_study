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
    /// RFA 族群編輯工具第十二批：檢視約束與刪除元素。
    /// 族群檔沒有可用的草圖編輯 API，「修改既有形體」實務上是：檢視 → 刪除 → 重建。
    /// - rfa_list_constraints：列出族群檔中所有尺寸標註與對齊鎖定（唯讀，不開交易）。
    /// - rfa_delete_elements：刪除形體／參考平面／線／尺寸等（破壞性；預設 dryRun，
    ///   以「試刪後回滾」取得 Revit 會一併刪除的完整清單，重大連帶刪除需 force 才執行）。
    /// </summary>
    public partial class CommandExecutor
    {
        #region 約束／元素描述（共用）

        /// <summary>讀取單一屬性；讀不到（例外）就回傳預設值，不讓單一欄位拖垮整個工具</summary>
        private static T RfaSafe<T>(Func<T> read, T fallback = default(T))
        {
            try { return read(); }
            catch { return fallback; }
        }

        private static string RfaBlankToNull(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        /// <summary>
        /// 判斷一個 Dimension 是否其實是「對齊鎖定」約束（NewAlignment 建立的也是 Dimension）。
        /// 依序：① 類別為「約束」（OST_Constraints）；② 沒有尺寸類型（GetTypeId 無效）。
        /// 回傳判定依據字串；不是對齊時回傳 null。
        /// </summary>
        private static string RfaAlignmentEvidence(Dimension dim)
        {
            IdType constraintsCat = (IdType)(int)BuiltInCategory.OST_Constraints;
            Category cat = RfaSafe(() => dim.Category);
            if (cat != null && RfaSafe(() => cat.Id.GetIdValue()) == constraintsCat)
                return "category=OST_Constraints";

            ElementId typeId = RfaSafe(() => dim.GetTypeId());
            if (typeId == null || typeId == ElementId.InvalidElementId)
                return "noDimensionType";
            return null;
        }

        /// <summary>
        /// 判斷一個 Dimension 是否為 Revit 自動產生的「自動草圖尺寸」（Automatic Sketch Dimensions，OST_WeakDims）。
        /// 實機觀察（Revit 2027）：參考線／符號線有未約束的端點時，Revit 求解器會自己建立這類尺寸
        /// （不帶標籤、0 mm 或與附近參考平面的距離），每次再生都可能刪除重建、換新 id。
        /// 它們不是使用者建立的約束，但會讓未約束的端點跟著附近的參考平面移動。
        /// 回傳判定依據字串；不是時回傳 null。
        /// </summary>
        private static string RfaImplicitEvidence(Dimension dim)
        {
            IdType weakCat = (IdType)(int)BuiltInCategory.OST_WeakDims;
            Category cat = RfaSafe(() => dim.Category);
            if (cat != null && RfaSafe(() => cat.Id.GetIdValue()) == weakCat)
                return "category=OST_WeakDims";
            // 後備：實機看到的類別是「Dimensions: Automatic Sketch Dimensions」——尺寸類別底下的子類別；
            // 使用者建立的尺寸／標籤尺寸的類別是「Dimensions」本身（沒有父類別）
            IdType dimsCat = (IdType)(int)BuiltInCategory.OST_Dimensions;
            Category parent = cat == null ? null : RfaSafe(() => cat.Parent);
            if (parent != null && RfaSafe(() => parent.Id.GetIdValue()) == dimsCat)
                return $"category=subcategory of Dimensions ({RfaSafe(() => cat.Name)})";
            return null;
        }

        /// <summary>尺寸種類：linear / angular / radial / diameter / alignment / implicit / other</summary>
        private static string RfaDimensionKind(Dimension dim, out string classifiedBy)
        {
            classifiedBy = RfaAlignmentEvidence(dim);
            if (classifiedBy != null) return "alignment";

            classifiedBy = RfaImplicitEvidence(dim);
            if (classifiedBy != null) return "implicit";

            DimensionShape? shape = RfaSafe<DimensionShape?>(() => dim.DimensionShape, null);
            classifiedBy = shape.HasValue ? "DimensionShape=" + shape.Value : "unknownShape";
            if (!shape.HasValue) return "other";
            switch (shape.Value)
            {
                case DimensionShape.Linear: return "linear";
                case DimensionShape.Angular: return "angular";
                case DimensionShape.Radial: return "radial";
                case DimensionShape.Diameter: return "diameter";
                default: return "other";
            }
        }

        /// <summary>元素種類名稱：參考線（IsReferenceLine 的 ModelCurve）與符號線特別標出，其餘用類別名稱</summary>
        private static string RfaElementKindName(Element e)
        {
            if (e == null) return null;
            if (e is ModelCurve mc && RfaSafe(() => mc.IsReferenceLine)) return "ReferenceLine";
            if (e is SymbolicCurve) return "SymbolicCurve";
            return e.GetType().Name;
        }

        /// <summary>參考的穩定表示（best effort）</summary>
        private static object RfaDescribeReference(Document doc, Reference r)
        {
            ElementId eid = RfaSafe(() => r.ElementId);
            Element e = eid != null && eid != ElementId.InvalidElementId ? RfaSafe(() => doc.GetElement(eid)) : null;
            string name = null;
            if (e is ReferencePlane rp) name = RfaBlankToNull(RfaSafe(() => rp.Name));
            else if (e is FamilyInstance fi) name = RfaBlankToNull(RfaSafe(() => fi.Name));

            string refType = RfaSafe(() => r.ElementReferenceType.ToString());
            return new
            {
                elementId = eid != null && eid != ElementId.InvalidElementId ? (IdType?)eid.GetIdValue() : null,
                elementKind = RfaElementKindName(e),
                name,
                referenceType = refType,
                stableRepresentation = RfaSafe(() => r.ConvertToStableRepresentation(doc)),
            };
        }

        private static double? RfaDimValueOut(double? raw, string kind)
        {
            if (!raw.HasValue) return null;
            if (kind == "angular")
                return Math.Round(UnitUtils.ConvertFromInternalUnits(raw.Value, UnitTypeId.Degrees), 6);
            return RfaToMm(raw.Value);
        }

        /// <summary>
        /// 描述一個尺寸／對齊約束。每個欄位各自 try/catch，讀不到就是 null。
        /// kind、label、referencedIds 另外以 out 回傳，供篩選與刪除報告使用。
        /// </summary>
        private static Dictionary<string, object> RfaDescribeDimension(Document doc, Dimension dim,
            out string kind, out string label, out HashSet<IdType> referencedIds)
        {
            kind = RfaDimensionKind(dim, out string classifiedBy);
            string k = kind;

            FamilyParameter fp = RfaSafe(() => dim.FamilyLabel);
            label = fp != null ? RfaSafe(() => fp.Definition.Name) : null;

            ElementId viewId = RfaSafe(() => dim.OwnerViewId);
            View view = viewId != null && viewId != ElementId.InvalidElementId ? RfaSafe(() => doc.GetElement(viewId) as View) : null;

            int segCount = RfaSafe(() => dim.NumberOfSegments);
            double? value = null;
            List<double?> segments = null;
            if (k != "alignment")
            {
                if (segCount <= 1) value = RfaDimValueOut(RfaSafe(() => dim.Value), k);
                else
                {
                    segments = new List<double?>();
                    DimensionSegmentArray segs = RfaSafe(() => dim.Segments);
                    if (segs != null)
                        foreach (DimensionSegment s in segs)
                            segments.Add(RfaDimValueOut(RfaSafe(() => s.Value), k));
                }
            }

            var refs = new List<object>();
            referencedIds = new HashSet<IdType>();
            ReferenceArray ra = RfaSafe(() => dim.References);
            if (ra != null)
            {
                foreach (Reference r in ra)
                {
                    if (r == null) continue;
                    refs.Add(RfaDescribeReference(doc, r));
                    ElementId eid = RfaSafe(() => r.ElementId);
                    if (eid != null && eid != ElementId.InvalidElementId) referencedIds.Add(eid.GetIdValue());
                }
            }

            return new Dictionary<string, object>
            {
                ["id"] = dim.Id.GetIdValue(),
                ["kind"] = k,
                ["classifiedBy"] = classifiedBy,
                ["view"] = view != null ? RfaSafe(() => view.Name) : null,
                ["viewId"] = view?.Id.GetIdValue(),
                ["label"] = label,
                ["valueMm"] = k == "angular" ? null : value,
                ["angleDeg"] = k == "angular" ? value : null,
                ["segmentsMm"] = k == "angular" ? null : segments,
                ["segmentsDeg"] = k == "angular" ? segments : null,
                ["isLocked"] = RfaSafe<bool?>(() => dim.IsLocked, null),
                ["areSegmentsEqual"] = segCount > 1 ? RfaSafe<bool?>(() => dim.AreSegmentsEqual, null) : null,
                ["references"] = refs,
            };
        }

        /// <summary>參考平面是否為族群原點（「定義原點」，BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN）</summary>
        private static bool RfaDefinesOrigin(ReferencePlane rp)
        {
            Parameter p = RfaSafe(() => rp.get_Parameter(BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN));
            return p != null && RfaSafe(() => p.HasValue && p.AsInteger() != 0);
        }

        /// <summary>本工具絕不刪除的元素：回傳拒絕原因，可刪則回傳 null</summary>
        private static string RfaProtectedReason(Document doc, Element e)
        {
            if (e.Id.GetIdValue() <= 0) return "內建元素，不可刪除";
            if (e is View) return "視圖不可刪除";
            if (e is Level) return "樓層不可刪除";
            if (e is Family) return "族群元素（族群本身或巢狀族群定義）不可以本工具刪除";
            if (e is ElementType) return "類型元素不可以本工具刪除";
            if (e is Material) return "材質不可以本工具刪除";
            if (e is ParameterElement) return "參數元素不可以本工具刪除（族群參數請用 rfa_delete_parameter）";
            if (e is GraphicsStyle) return "類別／子類別樣式不可以本工具刪除";
            if (e is Sketch) return "草圖不可直接刪除，請刪除擁有它的形體";
            if (e is ReferencePlane rp && RfaDefinesOrigin(rp)) return $"參考平面「{RfaSafe(() => rp.Name)}」定義族群原點（Defines Origin），不可刪除";
            return null;
        }

        /// <summary>描述任一元素（刪除報告用）：id、kind、name，尺寸另附 label 與 constraint</summary>
        private static Dictionary<string, object> RfaDescribeElementForDelete(Document doc, Element e, out string dimKind, out string dimLabel)
        {
            dimKind = null; dimLabel = null;
            var d = new Dictionary<string, object> { ["id"] = e.Id.GetIdValue() };
            if (e is Dimension dim)
            {
                Dictionary<string, object> c = RfaDescribeDimension(doc, dim, out dimKind, out dimLabel, out _);
                d["kind"] = dimKind == "alignment" ? "Alignment" : "Dimension";
                d["name"] = null;
                d["label"] = dimLabel;
                d["constraint"] = c;
                return d;
            }

            d["kind"] = RfaElementKindName(e);
            d["name"] = RfaBlankToNull(RfaSafe(() => e.Name));
            d["label"] = null;
            if (e is GenericForm gf) d["isSolid"] = RfaSafe<bool?>(() => gf.IsSolid, null);
            if (e is ReferencePlane rp) d["definesOrigin"] = RfaDefinesOrigin(rp);
            return d;
        }

        #endregion

        #region rfa_list_constraints

        /// <summary>
        /// 列出族群檔中所有尺寸標註與對齊鎖定（兩者在 API 中都是 Dimension）。
        /// 只讀取，不開交易；單一欄位讀不到就是 null，不會讓整個工具失敗。
        /// </summary>
        private object RfaListConstraints(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();

            HashSet<IdType> filterIds = null;
            if (parameters["elementIds"] is JArray idArr && idArr.Count > 0)
            {
                filterIds = new HashSet<IdType>();
                foreach (JToken t in idArr)
                {
                    if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
                        throw new Exception($"elementIds 只能是數字，收到: {t}");
                    filterIds.Add((IdType)t.Value<long>());
                }
            }

            HashSet<string> kinds = null;
            if (parameters["kinds"] is JArray kArr && kArr.Count > 0)
            {
                kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JToken t in kArr)
                {
                    string k = t.Type == JTokenType.String ? t.Value<string>().Trim() : null;
                    if (k == null || (!k.Equals("dimension", StringComparison.OrdinalIgnoreCase) && !k.Equals("alignment", StringComparison.OrdinalIgnoreCase)))
                        throw new Exception($"kinds 只能包含 \"dimension\" 或 \"alignment\"，收到: {t}");
                    kinds.Add(k);
                }
            }

            bool includeUnlabelled = parameters["includeUnlabelled"]?.Type == JTokenType.Boolean
                ? parameters["includeUnlabelled"].Value<bool>() : true;

            bool includeImplicit = parameters["includeImplicit"]?.Type == JTokenType.Boolean
                && parameters["includeImplicit"].Value<bool>();

            var items = new List<Dictionary<string, object>>();
            var errors = new List<object>();
            int implicitHidden = 0;
            foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(Dimension)))
            {
                var dim = el as Dimension;
                if (dim == null) continue;
                try
                {
                    var info = RfaDescribeDimension(doc, dim, out string kind, out string label, out HashSet<IdType> refIds);
                    if (kind == "implicit" && !includeImplicit)
                    {
                        if (filterIds == null || refIds.Overlaps(filterIds)) implicitHidden++;
                        continue;
                    }
                    bool isAlignment = kind == "alignment";
                    if (kinds != null && !kinds.Contains(isAlignment ? "alignment" : "dimension")) continue;
                    if (!includeUnlabelled && label == null) continue;
                    if (filterIds != null && !refIds.Overlaps(filterIds)) continue;
                    items.Add(info);
                }
                catch (Exception ex)
                {
                    errors.Add(new { id = RfaSafe<IdType?>(() => dim.Id.GetIdValue(), null), error = ex.Message });
                }
            }

            items = items.OrderBy(i => (string)i["kind"]).ThenBy(i => Convert.ToInt64(i["id"])).ToList();
            var counts = items.GroupBy(i => (string)i["kind"]).ToDictionary(g => g.Key, g => g.Count());

            return new
            {
                total = items.Count,
                counts,
                labelledCount = items.Count(i => i["label"] != null),
                implicitHiddenCount = implicitHidden,
                constraints = items,
                errors,
            };
        }

        #endregion

        #region rfa_delete_elements

        /// <summary>試刪：在會回滾的交易中呼叫 doc.Delete，回傳 Revit 實際會刪除的所有 id（含連帶）。不留下任何變更。</summary>
        private static List<ElementId> RfaProbeDelete(Document doc, ICollection<ElementId> ids, out string error)
        {
            error = null;
            var result = new List<ElementId>();
            using (var t = new Transaction(doc, "MCP: rfa_delete_elements（試刪）"))
            {
                var fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
                t.SetFailureHandlingOptions(fho);
                t.Start();
                try
                {
                    ICollection<ElementId> deleted = doc.Delete(ids);
                    if (deleted != null) result.AddRange(deleted);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                }
            }
            return result;
        }

        /// <summary>
        /// 刪除族群檔中的元素（形體、參考平面、參考線、符號線、尺寸／對齊、接頭、巢狀族群實例），
        /// 並回報 Revit 一併刪除的所有元素。預設 dryRun：試刪後回滾，只回報。
        /// 實際刪除時：受保護元素一律拒絕；連帶刪除中含「重大」元素（帶標籤尺寸、未要求的形體／參考平面／
        /// 參考線／符號線、接頭、巢狀實例、類型）時，整批拒絕，除非 force: true。
        /// </summary>
        private object RfaDeleteElements(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();

            var idsArr = parameters["elementIds"] as JArray;
            if (idsArr == null || idsArr.Count == 0)
                throw new Exception("請提供 elementIds 陣列（要刪除的元素 ElementId）");

            bool dryRun = parameters["dryRun"]?.Type == JTokenType.Boolean ? parameters["dryRun"].Value<bool>() : true;
            bool force = parameters["force"]?.Type == JTokenType.Boolean && parameters["force"].Value<bool>();

            var warnings = new List<string>();
            var notFound = new List<IdType>();
            var refused = new List<object>();
            var requestedIdVals = new List<IdType>();
            foreach (JToken t in idsArr)
            {
                if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
                {
                    warnings.Add($"略過非數字的 elementId: {t}");
                    continue;
                }
                IdType v = (IdType)t.Value<long>();
                if (!requestedIdVals.Contains(v)) requestedIdVals.Add(v);
            }

            // ── 1. 存在性與保護檢查（不論 force 一律拒絕） ──
            var candidates = new List<ElementId>();
            foreach (IdType v in requestedIdVals)
            {
                ElementId id = v.ToElementId();
                Element e = RfaSafe(() => doc.GetElement(id));
                if (e == null) { notFound.Add(v); continue; }

                string reason = RfaProtectedReason(doc, e);
                if (reason == null && !RfaSafe(() => DocumentValidation.CanDeleteElement(doc, id), true))
                    reason = "Revit 不允許刪除此元素（DocumentValidation.CanDeleteElement = false）";
                if (reason != null)
                {
                    refused.Add(new { id = v, kind = RfaElementKindName(e), name = RfaBlankToNull(RfaSafe(() => e.Name)), reason });
                    continue;
                }
                candidates.Add(id);
            }

            object Result(List<object> deletedList, List<object> requestedList, List<object> collateralList,
                          List<object> significantList, bool requiresForce, bool executed, int wouldDeleteCount)
            {
                // 輸出瘦身：deleted / requested 的尺寸只留精簡 constraint；
                // 非重大連帶項目不帶 constraint，且超過上限時截斷；significantCollateral 只列 id
                var counts = new Dictionary<string, int>();
                var shownCollateral = new List<object>();
                int nonSigShown = 0, truncated = 0;
                foreach (var o in collateralList)
                {
                    var c = (Dictionary<string, object>)o;
                    string k = c.TryGetValue("kind", out object kv) && kv != null ? kv.ToString() : "unknown";
                    counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
                    bool sig = c.TryGetValue("significant", out object sv) && sv is bool sb && sb;
                    if (sig)
                    {
                        shownCollateral.Add(c);
                        continue;
                    }
                    if (nonSigShown >= RfaDeleteCollateralLimit) { truncated++; continue; }
                    nonSigShown++;
                    var slim = new Dictionary<string, object>
                    {
                        ["id"] = c["id"],
                        ["kind"] = c.TryGetValue("kind", out object k2) ? k2 : null,
                        ["name"] = c.TryGetValue("name", out object nm) ? nm : null,
                        ["label"] = c.TryGetValue("label", out object lb) ? lb : null,
                        ["significant"] = false,
                    };
                    shownCollateral.Add(slim);
                }

                return new
                {
                    dryRun,
                    force,
                    executed,
                    deleted = deletedList.Select(RfaCompactDeleteItem).ToList(),
                    requested = requestedList.Select(RfaCompactDeleteItem).ToList(),
                    collateral = shownCollateral,
                    collateralCounts = counts,
                    collateralTruncated = truncated,
                    refused,
                    notFound,
                    requiresForce,
                    significantCollateral = significantList
                        .Select(s => ((Dictionary<string, object>)s)["id"]).ToList(),
                    totalDeletedByRevit = wouldDeleteCount,
                    warnings,
                };
            }

            var empty = new List<object>();
            if (candidates.Count == 0)
            {
                warnings.Add("沒有可刪除的元素");
                return Result(empty, empty, empty, empty, false, false, 0);
            }

            // ── 2. 試刪（回滾）取得完整刪除清單；整批失敗時逐一試刪找出無法刪除者 ──
            List<ElementId> probe = RfaProbeDelete(doc, candidates, out string probeError);
            if (probeError != null)
            {
                var ok = new List<ElementId>();
                foreach (ElementId id in candidates)
                {
                    RfaProbeDelete(doc, new List<ElementId> { id }, out string err);
                    if (err == null) { ok.Add(id); continue; }
                    Element e = doc.GetElement(id);
                    refused.Add(new { id = id.GetIdValue(), kind = RfaElementKindName(e), name = RfaBlankToNull(RfaSafe(() => e?.Name)), reason = $"Revit 無法刪除：{err}" });
                }
                candidates = ok;
                if (candidates.Count == 0)
                {
                    warnings.Add("沒有可刪除的元素");
                    return Result(empty, empty, empty, empty, false, false, 0);
                }
                probe = RfaProbeDelete(doc, candidates, out probeError);
                if (probeError != null)
                    throw new Exception($"試刪失敗：{probeError}（個別元素都可刪，但合併刪除時 Revit 拒絕）");
            }

            // 回滾後元素都還在，可以逐一描述
            var requestedSet = new HashSet<IdType>(candidates.Select(i => i.GetIdValue()));
            var probeSet = new HashSet<IdType>(probe.Select(i => i.GetIdValue()));
            foreach (IdType v in requestedSet.Where(v => !probeSet.Contains(v)).ToList())
                warnings.Add($"元素 {v} 未出現在 Revit 回傳的刪除清單中");

            var requestedDesc = new Dictionary<IdType, Dictionary<string, object>>();
            foreach (ElementId id in candidates)
            {
                Element e = doc.GetElement(id);
                requestedDesc[id.GetIdValue()] = e != null
                    ? RfaDescribeElementForDelete(doc, e, out _, out _)
                    : new Dictionary<string, object> { ["id"] = id.GetIdValue(), ["kind"] = null, ["name"] = null };
            }

            var collateralIds = probe.Where(i => !requestedSet.Contains(i.GetIdValue())).ToList();

            // 屬於（將被刪除的）草圖的曲線：形體的草圖線，屬預期的連帶刪除
            var sketchMembers = new HashSet<IdType>();
            foreach (ElementId id in collateralIds.Concat(candidates))
            {
                if (doc.GetElement(id) is Sketch sk)
                {
                    var members = RfaSafe(() => sk.GetAllElements());
                    if (members != null) foreach (ElementId m in members) sketchMembers.Add(m.GetIdValue());
                }
                else if (doc.GetElement(id) is GenericForm gf)
                {
                    // 形體的草圖（擠出／融合等）也一併涵蓋
                    foreach (Sketch s in RfaFormSketches(gf))
                    {
                        var members = RfaSafe(() => s.GetAllElements());
                        if (members != null) foreach (ElementId m in members) sketchMembers.Add(m.GetIdValue());
                    }
                }
            }

            var collateral = new List<object>();
            var significant = new List<object>();
            var blocked = new List<object>();
            foreach (ElementId id in collateralIds)
            {
                IdType v = id.GetIdValue();
                Element e = RfaSafe(() => doc.GetElement(id));
                if (e == null)
                {
                    collateral.Add(new Dictionary<string, object> { ["id"] = v, ["kind"] = null, ["name"] = null, ["label"] = null, ["significant"] = false });
                    continue;
                }

                var d = RfaDescribeElementForDelete(doc, e, out string dimKind, out string dimLabel);
                string sigReason = null;
                string blockReason = null;

                if (e is View || e is Level || e is Material || e is Family || e is ParameterElement)
                    blockReason = $"連帶刪除會移除受保護的 {e.GetType().Name}";
                else if (e is ReferencePlane rpo && RfaDefinesOrigin(rpo))
                    blockReason = $"連帶刪除會移除定義族群原點的參考平面「{RfaSafe(() => rpo.Name)}」";
                else if (e is Dimension)
                {
                    if (dimLabel != null) sigReason = $"帶參數標籤「{dimLabel}」的尺寸";
                }
                else if (e is GenericForm) sigReason = "未要求刪除的形體";
                else if (e is ReferencePlane) sigReason = "未要求刪除的參考平面";
                else if (e is ConnectorElement) sigReason = "MEP 接頭";
                else if (e is FamilyInstance) sigReason = "巢狀族群實例";
                else if (e is ElementType) sigReason = "類型元素";
                else if (e is CurveElement && !sketchMembers.Contains(v))
                    sigReason = $"未要求刪除、也不屬於被刪形體草圖的{RfaElementKindName(e)}";
                // Sketch、SketchPlane、草圖曲線、未帶標籤的尺寸／對齊：預期的連帶刪除

                d["significant"] = sigReason != null || blockReason != null;
                if (sigReason != null) d["significantReason"] = sigReason;
                collateral.Add(d);
                if (sigReason != null) significant.Add(d);
                if (blockReason != null) blocked.Add(new { id = v, kind = d["kind"], name = d["name"], reason = blockReason });
            }

            bool requiresForce = significant.Count > 0;
            var requestedList = candidates.Select(i => (object)requestedDesc[i.GetIdValue()]).ToList();
            int total = probe.Count;

            if (blocked.Count > 0)
            {
                foreach (var b in blocked) refused.Add(b);
                warnings.Add("連帶刪除會移除受保護的元素，整批" + (dryRun ? "實際執行時會被拒絕（force 也無法覆寫）" : "已拒絕（force 也無法覆寫），未刪除任何元素"));
                return Result(empty, requestedList, collateral, significant, requiresForce, false, total);
            }

            if (dryRun)
            {
                if (requiresForce)
                    warnings.Add("連帶刪除含重大元素（見 significantCollateral），實際執行需 force: true");
                return Result(empty, requestedList, collateral, significant, requiresForce, false, total);
            }

            if (requiresForce && !force)
            {
                warnings.Add("連帶刪除含重大元素（見 significantCollateral），整批已拒絕、未刪除任何元素；確認後可傳 force: true");
                return Result(empty, requestedList, collateral, significant, true, false, total);
            }

            // ── 3. 實際刪除（單一交易） ──
            List<ElementId> actual;
            using (var t = new Transaction(doc, "MCP: rfa_delete_elements"))
            {
                var fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
                t.SetFailureHandlingOptions(fho);
                t.Start();
                try
                {
                    actual = doc.Delete(candidates)?.ToList() ?? new List<ElementId>();
                }
                catch (Exception ex)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    throw new Exception($"刪除失敗：{ex.Message}（所有變更已復原）");
                }
                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                    throw new Exception($"rfa_delete_elements 交易未成功提交（狀態：{status}），所有變更已復原");
            }

            var actualSet = new HashSet<IdType>(actual.Select(i => i.GetIdValue()));
            var extra = actualSet.Where(v => !probeSet.Contains(v)).ToList();
            var missing = probeSet.Where(v => !actualSet.Contains(v)).ToList();
            if (extra.Count > 0) warnings.Add($"實際刪除比試刪多出 {extra.Count} 個元素（未描述）：{string.Join(", ", extra)}");
            if (missing.Count > 0) warnings.Add($"試刪清單中有 {missing.Count} 個元素實際未被刪除：{string.Join(", ", missing)}");

            var deletedList = candidates.Where(i => actualSet.Contains(i.GetIdValue()))
                .Select(i => (object)requestedDesc[i.GetIdValue()]).ToList();
            var collateralActual = collateral
                .Where(c => actualSet.Contains((IdType)((Dictionary<string, object>)c)["id"])).ToList();

            return Result(deletedList, requestedList, collateralActual, significant, requiresForce, true, actual.Count);
        }

        /// <summary>rfa_delete_elements 回傳中非重大連帶項目的上限（超過者只計數）</summary>
        private const int RfaDeleteCollateralLimit = 40;

        /// <summary>
        /// deleted / requested 項目瘦身：只有 Dimension / Alignment 保留 constraint，
        /// 且只留 id、kind、label、valueMm / angleDeg；其他種類不帶 constraint。
        /// </summary>
        private static object RfaCompactDeleteItem(object o)
        {
            var src = o as Dictionary<string, object>;
            if (src == null) return o;
            var d = new Dictionary<string, object>(src);
            if (!d.TryGetValue("constraint", out object co)) return d;
            string kind = d.TryGetValue("kind", out object k) ? k as string : null;
            var c = co as Dictionary<string, object>;
            if (c == null || (kind != "Dimension" && kind != "Alignment"))
            {
                d.Remove("constraint");
                return d;
            }
            var compact = new Dictionary<string, object>
            {
                ["id"] = c.TryGetValue("id", out object id) ? id : null,
                ["kind"] = c.TryGetValue("kind", out object ck) ? ck : null,
                ["label"] = c.TryGetValue("label", out object lb) ? lb : null,
            };
            if (c.TryGetValue("valueMm", out object vm) && vm != null) compact["valueMm"] = vm;
            if (c.TryGetValue("angleDeg", out object ad) && ad != null) compact["angleDeg"] = ad;
            d["constraint"] = compact;
            return d;
        }

        /// <summary>形體的草圖（擠出、融合、旋轉、掃掠的斷面草圖等）；讀不到的略過</summary>
        private static IEnumerable<Sketch> RfaFormSketches(GenericForm gf)
        {
            var list = new List<Sketch>();
            void Add(Sketch s) { if (s != null) list.Add(s); }
            switch (gf)
            {
                case Extrusion ex: Add(RfaSafe(() => ex.Sketch)); break;
                case Revolution rv: Add(RfaSafe(() => rv.Sketch)); break;
                case Blend bl: Add(RfaSafe(() => bl.BottomSketch)); Add(RfaSafe(() => bl.TopSketch)); break;
                case Sweep sw: Add(RfaSafe(() => sw.ProfileSketch)); Add(RfaSafe(() => sw.PathSketch)); break;
                case SweptBlend sb:
                    Add(RfaSafe(() => sb.BottomSketch)); Add(RfaSafe(() => sb.TopSketch)); Add(RfaSafe(() => sb.PathSketch));
                    break;
            }
            return list;
        }

        #endregion
    }
}
