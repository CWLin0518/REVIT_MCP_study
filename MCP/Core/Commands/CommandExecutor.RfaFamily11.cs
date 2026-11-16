using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// RFA 族群編輯工具第十一批：MEP 接頭（ConnectorElement）。
    /// - rfa_create_connector：在形體的平面上建立管／風管／電氣／電管／電纜架接頭，
    ///   設定系統類型、斷面形狀、尺寸、流向，並可把尺寸綁定到族群參數。
    /// - RfaDescribeConnector：讀出接頭資訊，rfa_get_document_info 的 connectors 清單共用。
    /// 只用 Revit 2022 起就存在的 API：ConnectorElement.Create*Connector(Document, …, Reference) 這組不含 Edge 的多載。
    /// </summary>
    public partial class CommandExecutor
    {
        #region rfa_create_connector

        // 平面法向量比對容差：約 1 度
        private const double RfaConnectorNormalCos = 0.99985;

        /// <summary>收集元素幾何中「有參考」的平面（ComputeReferences + IncludeNonVisibleObjects，與 rfa_align_to_reference_plane 相同）</summary>
        private static List<PlanarFace> RfaCollectPlanarFaces(Element element)
        {
            var faces = new List<PlanarFace>();
            var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
            void Collect(GeometryElement ge)
            {
                if (ge == null) return;
                foreach (GeometryObject go in ge)
                {
                    if (go is Solid s && s.Faces.Size > 0)
                    {
                        foreach (Face f in s.Faces)
                            if (f is PlanarFace pf && pf.Reference != null) faces.Add(pf);
                    }
                    else if (go is GeometryInstance gi)
                    {
                        Collect(gi.GetSymbolGeometry());
                    }
                }
            }
            Collect(element.get_Geometry(opt));
            return faces;
        }

        /// <summary>以 enum 名稱（不分大小寫）解析；數字字串不接受。無效時錯誤訊息列出所有可用名稱</summary>
        private static T RfaParseEnumName<T>(string value, string label) where T : struct
        {
            string[] names = Enum.GetNames(typeof(T));
            string match = names.FirstOrDefault(n => string.Equals(n, value?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new Exception($"{label}「{value}」無效（可用: {string.Join(", ", names)}）");
            return (T)Enum.Parse(typeof(T), match);
        }

        private static string RfaConnectorDomainName(ConnectorElement ce)
        {
            switch (ce.Domain)
            {
                case Domain.DomainPiping: return "pipe";
                case Domain.DomainHvac: return "duct";
                case Domain.DomainElectrical: return "electrical";
                case Domain.DomainCableTrayConduit:
                    // API 不分電管與電纜架：電管為圓形接頭，電纜架為矩形接頭（推斷）
                    ConnectorProfileType shape;
                    try { shape = ce.Shape; } catch { return "cableTrayConduit"; }
                    return shape == ConnectorProfileType.Round ? "conduit" : shape == ConnectorProfileType.Rectangular ? "cableTray" : "cableTrayConduit";
                default: return ce.Domain.ToString();
            }
        }

        private static string RfaShapeName(ConnectorProfileType shape)
        {
            switch (shape)
            {
                case ConnectorProfileType.Round: return "round";
                case ConnectorProfileType.Rectangular: return "rectangular";
                case ConnectorProfileType.Oval: return "oval";
                default: return null; // Invalid：電氣接頭或未定義
            }
        }

        private static string RfaReadFlowDirection(ConnectorElement ce)
        {
            foreach (var bip in new[] { BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM, BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM })
            {
                Parameter p = ce.get_Parameter(bip);
                if (p == null || p.StorageType != StorageType.Integer || !p.HasValue) continue;
                switch ((FlowDirectionType)p.AsInteger())
                {
                    case FlowDirectionType.In: return "in";
                    case FlowDirectionType.Out: return "out";
                    case FlowDirectionType.Bidirectional: return "bidirectional";
                    default: return p.AsInteger().ToString();
                }
            }
            return null;
        }

        /// <summary>
        /// 描述一個接頭；每個欄位獨立讀取，讀不到就是 null，不讓單一欄位失敗拖垮整筆。
        /// rfa_create_connector 與 rfa_get_document_info 共用。
        /// </summary>
        private static Dictionary<string, object> RfaDescribeConnector(ConnectorElement ce)
        {
            T Safe<T>(Func<T> f) where T : class
            {
                try { return f(); } catch { return null; }
            }

            var d = new Dictionary<string, object>
            {
                ["id"] = ce.Id.GetIdValue(),
                ["domain"] = Safe(() => RfaConnectorDomainName(ce)),
                ["systemType"] = Safe(() => ce.SystemClassification.ToString()),
                ["shape"] = Safe(() => RfaShapeName(ce.Shape)),
                ["originMm"] = Safe(() => RfaPt(ce.Origin)),
                ["directionVector"] = Safe(() => RfaVec(ce.CoordinateSystem.BasisZ)),
                ["flowDirection"] = Safe(() => RfaReadFlowDirection(ce)),
                ["isPrimary"] = Safe(() => (object)ce.IsPrimary),
            };

            double radius = -1, width = -1, height = -1;
            try { radius = ce.Radius; } catch { }
            try { width = ce.Width; } catch { }
            try { height = ce.Height; } catch { }
            if (radius > 0) d["diameterMm"] = RfaToMm(radius * 2);
            if (width > 0) d["widthMm"] = RfaToMm(width);
            if (height > 0) d["heightMm"] = RfaToMm(height);
            return d;
        }

        /// <summary>把長度（內部單位）寫進接頭的內建參數；參數不存在或唯讀時回傳 false</summary>
        private static bool RfaTrySetConnectorLength(ConnectorElement ce, BuiltInParameter bip, double valueFt)
        {
            Parameter p = ce.get_Parameter(bip);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return false;
            return p.Set(valueFt);
        }

        /// <summary>
        /// 在形體的平面上建立 MEP 接頭。每筆在各自的子交易中執行，單筆失敗只回滾該筆。
        /// </summary>
        private object RfaCreateConnector(JObject parameters)
        {
            Document doc = GetActiveFamilyDocument();
            FamilyManager fm = doc.FamilyManager;
            string familyCategory = doc.OwnerFamily?.FamilyCategory?.Name;

            var list = parameters["connectors"] as JArray;
            if (list == null || list.Count == 0)
                throw new Exception("請提供 connectors 陣列（每個元素含 elementId、faceNormal、domain，可選 facePointMm、systemType、shape、diameterMm／widthMm＋heightMm、associate、flowDirection）");

            var created = new List<object>();
            var errors = new List<object>();
            var warnings = new List<string>();

            using (var t = new Transaction(doc, "MCP: rfa_create_connector"))
            {
                t.Start();

                for (int index = 0; index < list.Count; index++)
                {
                    var itemWarnings = new List<string>();
                    using (var st = new SubTransaction(doc))
                    {
                        try
                        {
                            var p = list[index] as JObject ?? throw new Exception("每個元素必須是物件");

                            // ── 驗證輸入（不碰 Revit） ──
                            if (p["elementId"] == null || p["elementId"].Type == JTokenType.Null)
                                throw new Exception("缺少 elementId（承載接頭的形體 ElementId）");
                            IdType idVal = p["elementId"].Value<IdType>();
                            Element host = doc.GetElement(idVal.ToElementId()) ?? throw new Exception($"找不到元素 {idVal}");

                            XYZ wantNormal = RfaReadVector(p["faceNormal"], "faceNormal");
                            XYZ facePoint = p["facePointMm"] != null && p["facePointMm"].Type != JTokenType.Null
                                ? RfaReadXyzMm(p["facePointMm"], "facePointMm") : null;

                            string domain = (p["domain"]?.Value<string>() ?? throw new Exception("缺少 domain（pipe / duct / electrical / conduit / cableTray）")).Trim();
                            switch (domain.ToLowerInvariant())
                            {
                                case "pipe": domain = "pipe"; break;
                                case "duct": domain = "duct"; break;
                                case "electrical": domain = "electrical"; break;
                                case "conduit": domain = "conduit"; break;
                                case "cabletray": domain = "cableTray"; break;
                                default: throw new Exception($"domain「{domain}」無效（可用: pipe, duct, electrical, conduit, cableTray）");
                            }

                            string systemTypeIn = p["systemType"]?.Type == JTokenType.String ? p["systemType"].Value<string>() : null;
                            if (string.IsNullOrWhiteSpace(systemTypeIn)) systemTypeIn = null;

                            string shapeIn = p["shape"]?.Type == JTokenType.String ? p["shape"].Value<string>()?.Trim().ToLowerInvariant() : null;
                            ConnectorProfileType profile = ConnectorProfileType.Round;
                            if (domain == "duct")
                            {
                                switch (shapeIn ?? "round")
                                {
                                    case "round": profile = ConnectorProfileType.Round; break;
                                    case "rectangular": profile = ConnectorProfileType.Rectangular; break;
                                    case "oval": profile = ConnectorProfileType.Oval; break;
                                    default: throw new Exception($"shape「{shapeIn}」無效（可用: round, rectangular, oval）");
                                }
                            }
                            else if (shapeIn != null)
                            {
                                itemWarnings.Add($"shape 只適用於 duct，已忽略（{domain} 接頭形狀由 Revit 決定）");
                            }

                            double? diameterMm = p["diameterMm"]?.Type == JTokenType.Null ? null : p["diameterMm"]?.Value<double?>();
                            double? widthMm = p["widthMm"]?.Type == JTokenType.Null ? null : p["widthMm"]?.Value<double?>();
                            double? heightMm = p["heightMm"]?.Type == JTokenType.Null ? null : p["heightMm"]?.Value<double?>();
                            foreach (var (k, v) in new[] { ("diameterMm", diameterMm), ("widthMm", widthMm), ("heightMm", heightMm) })
                                if (v.HasValue && v.Value <= 0) throw new Exception($"{k} 必須大於 0");

                            bool isRound = domain == "pipe" || domain == "conduit" || (domain == "duct" && profile == ConnectorProfileType.Round);
                            bool isRectLike = domain == "cableTray" || (domain == "duct" && profile != ConnectorProfileType.Round);
                            if (isRound && (widthMm.HasValue || heightMm.HasValue))
                                itemWarnings.Add("圓形接頭只用 diameterMm，已忽略 widthMm／heightMm");
                            if (isRectLike && diameterMm.HasValue)
                                itemWarnings.Add("矩形／橢圓接頭只用 widthMm＋heightMm，已忽略 diameterMm");
                            if (domain == "electrical" && (diameterMm.HasValue || widthMm.HasValue || heightMm.HasValue))
                                itemWarnings.Add("電氣接頭沒有尺寸，已忽略尺寸欄位");

                            string flowIn = p["flowDirection"]?.Type == JTokenType.String ? p["flowDirection"].Value<string>()?.Trim().ToLowerInvariant() : null;
                            FlowDirectionType? flow = null;
                            if (flowIn != null)
                            {
                                switch (flowIn)
                                {
                                    case "in": flow = FlowDirectionType.In; break;
                                    case "out": flow = FlowDirectionType.Out; break;
                                    case "bidirectional": flow = FlowDirectionType.Bidirectional; break;
                                    default: throw new Exception($"flowDirection「{flowIn}」無效（可用: in, out, bidirectional）");
                                }
                            }

                            var associate = p["associate"] as JObject;

                            // ── 選面 ──
                            var faces = RfaCollectPlanarFaces(host);
                            if (faces.Count == 0)
                                throw new Exception($"元素 {idVal}（{host.GetType().Name}）沒有可放接頭的平面（需為擠出、融合、掃掠、旋轉或掃掠融合等實體形體）");

                            var candidates = faces.Where(f => f.FaceNormal.Normalize().DotProduct(wantNormal) >= RfaConnectorNormalCos).ToList();
                            if (candidates.Count == 0)
                            {
                                var available = faces
                                    .Select(f => $"normal [{string.Join(", ", (double[])RfaVec(f.FaceNormal.Normalize()))}] origin [{string.Join(", ", (double[])RfaPt(f.Origin))}]")
                                    .Distinct().ToList();
                                throw new Exception($"元素 {idVal} 沒有法向量為 [{string.Join(", ", (double[])RfaVec(wantNormal))}] 的平面。此形體的平面：{string.Join("；", available)}");
                            }

                            PlanarFace face;
                            if (candidates.Count == 1)
                            {
                                face = candidates[0];
                            }
                            else if (facePoint != null)
                            {
                                // 以平面距離為主，同一平面上的多個面再以投影距離區分
                                face = candidates
                                    .OrderBy(f => Math.Round(Math.Abs((facePoint - f.Origin).DotProduct(f.FaceNormal.Normalize())), 6))
                                    .ThenBy(f =>
                                    {
                                        try { return f.Project(facePoint)?.Distance ?? double.MaxValue; }
                                        catch { return double.MaxValue; }
                                    })
                                    .First();
                                itemWarnings.Add($"{candidates.Count} 個平面的法向量符合，依 facePointMm 選了 origin [{string.Join(", ", (double[])RfaPt(face.Origin))}] 的面");
                            }
                            else
                            {
                                // 沒給參考點：取沿法向量最外側的面
                                face = candidates.OrderByDescending(f => f.Origin.DotProduct(wantNormal)).First();
                                itemWarnings.Add($"{candidates.Count} 個平面的法向量符合，未給 facePointMm，選了最外側 origin [{string.Join(", ", (double[])RfaPt(face.Origin))}] 的面；候選: " +
                                                 string.Join("；", candidates.Select(f => $"[{string.Join(", ", (double[])RfaPt(f.Origin))}]")));
                            }

                            // ── 系統類型 ──
                            string systemTypeUsed = null;
                            PipeSystemType pipeSys = PipeSystemType.Global;
                            DuctSystemType ductSys = DuctSystemType.Global;
                            ElectricalSystemType elecSys = ElectricalSystemType.PowerBalanced;
                            switch (domain)
                            {
                                case "pipe":
                                    if (systemTypeIn != null) pipeSys = RfaParseEnumName<PipeSystemType>(systemTypeIn, "systemType（PipeSystemType）");
                                    systemTypeUsed = pipeSys.ToString();
                                    break;
                                case "duct":
                                    if (systemTypeIn != null) ductSys = RfaParseEnumName<DuctSystemType>(systemTypeIn, "systemType（DuctSystemType）");
                                    systemTypeUsed = ductSys.ToString();
                                    break;
                                case "electrical":
                                    if (systemTypeIn != null) elecSys = RfaParseEnumName<ElectricalSystemType>(systemTypeIn, "systemType（ElectricalSystemType）");
                                    systemTypeUsed = elecSys.ToString();
                                    break;
                                default:
                                    if (systemTypeIn != null) itemWarnings.Add($"systemType 不適用於 {domain} 接頭，已忽略");
                                    break;
                            }

                            // ── 建立（子交易） ──
                            st.Start();
                            ConnectorElement ce;
                            try
                            {
                                switch (domain)
                                {
                                    case "pipe": ce = ConnectorElement.CreatePipeConnector(doc, pipeSys, face.Reference); break;
                                    case "duct": ce = ConnectorElement.CreateDuctConnector(doc, ductSys, profile, face.Reference); break;
                                    case "electrical": ce = ConnectorElement.CreateElectricalConnector(doc, elecSys, face.Reference); break;
                                    case "conduit": ce = ConnectorElement.CreateConduitConnector(doc, face.Reference); break;
                                    default: ce = ConnectorElement.CreateCableTrayConnector(doc, face.Reference); break;
                                }
                            }
                            catch (Exception ex)
                            {
                                throw new Exception($"建立 {domain} 接頭失敗：{ex.Message}（族群類別：{familyCategory ?? "未知"}；部分類別不接受某些接頭種類）");
                            }
                            if (ce == null) throw new Exception($"建立 {domain} 接頭失敗（Revit 回傳 null；族群類別：{familyCategory ?? "未知"}）");

                            // ── 尺寸 ──
                            var sizeParamsUsed = new List<string>();
                            if (isRound && diameterMm.HasValue)
                            {
                                double dFt = RfaMm(diameterMm.Value);
                                if (RfaTrySetConnectorLength(ce, BuiltInParameter.CONNECTOR_DIAMETER, dFt)) sizeParamsUsed.Add("CONNECTOR_DIAMETER");
                                else if (RfaTrySetConnectorLength(ce, BuiltInParameter.CONNECTOR_RADIUS, dFt / 2)) sizeParamsUsed.Add("CONNECTOR_RADIUS");
                                else itemWarnings.Add("接頭沒有可寫的直徑／半徑參數，diameterMm 未套用");
                            }
                            if (isRectLike)
                            {
                                if (widthMm.HasValue)
                                {
                                    if (RfaTrySetConnectorLength(ce, BuiltInParameter.CONNECTOR_WIDTH, RfaMm(widthMm.Value))) sizeParamsUsed.Add("CONNECTOR_WIDTH");
                                    else itemWarnings.Add("接頭沒有可寫的寬度參數（CONNECTOR_WIDTH），widthMm 未套用");
                                }
                                if (heightMm.HasValue)
                                {
                                    if (RfaTrySetConnectorLength(ce, BuiltInParameter.CONNECTOR_HEIGHT, RfaMm(heightMm.Value))) sizeParamsUsed.Add("CONNECTOR_HEIGHT");
                                    else itemWarnings.Add("接頭沒有可寫的高度參數（CONNECTOR_HEIGHT），heightMm 未套用");
                                }
                            }

                            // ── 流向 ──
                            if (flow.HasValue)
                            {
                                BuiltInParameter? flowBip = domain == "pipe" ? BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM
                                                          : domain == "duct" ? BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM
                                                          : (BuiltInParameter?)null;
                                Parameter fpar = flowBip.HasValue ? ce.get_Parameter(flowBip.Value) : null;
                                if (fpar == null || fpar.IsReadOnly || fpar.StorageType != StorageType.Integer)
                                    itemWarnings.Add($"{domain} 接頭沒有可寫的流向參數，flowDirection 已忽略");
                                else
                                {
                                    try { fpar.Set((int)flow.Value); }
                                    catch (Exception ex) { itemWarnings.Add($"設定流向失敗：{ex.Message}"); }
                                }
                            }

                            // ── 尺寸參數綁定族群參數 ──
                            var associated = new List<object>();
                            if (associate != null)
                            {
                                foreach (var kv in associate)
                                {
                                    string key = kv.Key.Trim().ToLowerInvariant();
                                    string famParamName = kv.Value?.Type == JTokenType.String ? kv.Value.Value<string>() : null;
                                    BuiltInParameter bip;
                                    switch (key)
                                    {
                                        case "diameter": bip = BuiltInParameter.CONNECTOR_DIAMETER; break;
                                        case "radius": bip = BuiltInParameter.CONNECTOR_RADIUS; break;
                                        case "width": bip = BuiltInParameter.CONNECTOR_WIDTH; break;
                                        case "height": bip = BuiltInParameter.CONNECTOR_HEIGHT; break;
                                        default:
                                            itemWarnings.Add($"associate 不支援的鍵「{kv.Key}」（可用: diameter, radius, width, height）");
                                            continue;
                                    }
                                    if (string.IsNullOrEmpty(famParamName)) { itemWarnings.Add($"associate.{kv.Key} 需要族群參數名稱字串"); continue; }

                                    Parameter elemParam = ce.get_Parameter(bip);
                                    if (elemParam == null)
                                    {
                                        string hint = key == "diameter" && ce.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS) != null
                                            ? "；此接頭只有半徑（CONNECTOR_RADIUS），請改綁 radius，並把該族群參數設公式 = 直徑 / 2"
                                            : key == "radius" && ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER) != null
                                                ? "；此接頭只有直徑（CONNECTOR_DIAMETER），請改綁 diameter" : "";
                                        itemWarnings.Add($"接頭沒有「{kv.Key}」（{bip}）參數，未綁定「{famParamName}」{hint}");
                                        continue;
                                    }
                                    FamilyParameter fp = RfaFindParam(fm, famParamName);
                                    if (fp == null) { itemWarnings.Add($"找不到族群參數「{famParamName}」（要綁接頭的 {kv.Key}），請先用 rfa_add_parameter 建立長度參數"); continue; }
                                    if (!fm.CanElementParameterBeAssociated(elemParam)) { itemWarnings.Add($"接頭的「{kv.Key}」（{bip}）無法綁定族群參數「{famParamName}」"); continue; }
                                    try
                                    {
                                        fm.AssociateElementParameterToFamilyParameter(elemParam, fp);
                                        associated.Add(new { connectorParameter = kv.Key, builtInParameter = bip.ToString(), familyParameter = famParamName });
                                    }
                                    catch (Exception ex)
                                    {
                                        itemWarnings.Add($"綁定接頭「{kv.Key}」（{bip}）→ 族群參數「{famParamName}」失敗：{ex.Message}（兩邊需同為長度型別）");
                                    }
                                }
                            }

                            doc.Regenerate();
                            st.Commit();

                            // ── 讀回 ──
                            var info = RfaDescribeConnector(ce);
                            var item = new Dictionary<string, object>
                            {
                                ["index"] = index,
                                ["id"] = ce.Id.GetIdValue(),
                                ["domain"] = domain,
                                ["systemType"] = systemTypeUsed,
                                ["systemClassification"] = info["systemType"],
                                ["shape"] = info["shape"],
                                ["hostElementId"] = idVal,
                                ["faceNormal"] = RfaVec(face.FaceNormal.Normalize()),
                                ["faceOriginMm"] = RfaPt(face.Origin),
                                ["directionVector"] = info["directionVector"],
                                ["originMm"] = info["originMm"],
                            };
                            foreach (var k in new[] { "diameterMm", "widthMm", "heightMm" })
                                if (info.ContainsKey(k)) item[k] = info[k];
                            item["sizeParametersSet"] = sizeParamsUsed;
                            item["associated"] = associated;
                            item["flowDirection"] = info["flowDirection"];
                            created.Add(item);

                            foreach (var w in itemWarnings) warnings.Add($"#{index}: {w}");
                        }
                        catch (Exception ex)
                        {
                            if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                            errors.Add(new { index, error = ex.Message });
                        }
                    }
                }

                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                    throw new Exception($"rfa_create_connector 交易未成功提交（狀態：{status}），所有變更已復原");
            }

            return new
            {
                createdCount = created.Count,
                created,
                errors,
                warnings,
                familyCategory,
            };
        }

        #endregion
    }
}
