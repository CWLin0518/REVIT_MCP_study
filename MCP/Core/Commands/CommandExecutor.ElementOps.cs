using System;
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
    public partial class CommandExecutor
    {
        #region 元素操作（移動、翻轉）

        /// <summary>
        /// 移動元素（依 dx, dy, dz 公釐位移）
        /// 來源 fork: poisonsam/main:MCP/Core/Commands/CommandExecutor.Base.cs:718-751
        /// </summary>
        private object MoveElement(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType elementId = parameters["elementId"]?.Value<IdType>() ?? 0;
            double dx = parameters["dx"]?.Value<double>() ?? 0;
            double dy = parameters["dy"]?.Value<double>() ?? 0;
            double dz = parameters["dz"]?.Value<double>() ?? 0;

            Element element = doc.GetElement(new ElementId(elementId));
            if (element == null)
            {
                throw new Exception($"找不到元素 ID: {elementId}");
            }

            JObject startPoint = parameters["startPoint"] as JObject;
            JObject endPoint = parameters["endPoint"] as JObject;
            if (startPoint != null || endPoint != null)
            {
                if (dx != 0 || dy != 0 || dz != 0)
                    throw new Exception("startPoint/endPoint 不可與 dx/dy/dz 同時使用");
                return MoveLineEndpoints(doc, element, startPoint, endPoint);
            }

            using (Transaction trans = new Transaction(doc, $"移動元素: {elementId}"))
            {
                trans.Start();

                // 單位原本為公釐，需要轉換成 Revit 專案的內部單位英尺
                XYZ translation = new XYZ(dx / 304.8, dy / 304.8, dz / 304.8);
                ElementTransformUtils.MoveElement(doc, new ElementId(elementId), translation);

                trans.Commit();

                return new
                {
                    ElementId = elementId,
                    Dx = dx,
                    Dy = dy,
                    Dz = dz,
                    Message = "成功移動元素"
                };
            }
        }

        /// <summary>
        /// Moves the XY of one or both ends of a straight line-based element. Z is kept.
        /// The transaction rolls back if Revit does not land the ends within 0.01 mm.
        /// </summary>
        private object MoveLineEndpoints(Document doc, Element element, JObject startPoint, JObject endPoint)
        {
            LocationCurve location = element.Location as LocationCurve;
            Line oldLine = location?.Curve as Line;
            if (oldLine == null || !oldLine.IsBound)
                throw new Exception($"元素 {element.Id} 不是直線定位元素，無法指定端點");
            if (element.Pinned)
                throw new Exception($"元素 {element.Id} 已釘選，請先解除釘選");

            XYZ oldStart = oldLine.GetEndPoint(0);
            XYZ oldEnd = oldLine.GetEndPoint(1);
            XYZ newStart = ResolveEndpointXY(startPoint, oldStart, "startPoint");
            XYZ newEnd = ResolveEndpointXY(endPoint, oldEnd, "endPoint");
            if (newStart.DistanceTo(newEnd) <= doc.Application.ShortCurveTolerance)
                throw new Exception("新端點距離過短，無法建立直線");

            const double toleranceFeet = 0.01 / 304.8;
            using (Transaction trans = new Transaction(doc, $"調整端點: {element.Id}"))
            {
                trans.Start();
                location.Curve = Line.CreateBound(newStart, newEnd);
                doc.Regenerate();

                Curve actual = ((LocationCurve)element.Location).Curve;
                if (actual.GetEndPoint(0).DistanceTo(newStart) > toleranceFeet
                    || actual.GetEndPoint(1).DistanceTo(newEnd) > toleranceFeet)
                {
                    trans.RollBack();
                    throw new Exception("Revit 未將端點放到指定位置，已復原");
                }

                if (trans.Commit() != TransactionStatus.Committed)
                    throw new Exception("Revit 拒絕提交端點變更，模型未修改");
            }

            return new
            {
                ElementId = element.Id.GetIdValue(),
                OldStart = new { X = Math.Round(oldStart.X * 304.8, 2), Y = Math.Round(oldStart.Y * 304.8, 2), Z = Math.Round(oldStart.Z * 304.8, 2) },
                OldEnd = new { X = Math.Round(oldEnd.X * 304.8, 2), Y = Math.Round(oldEnd.Y * 304.8, 2), Z = Math.Round(oldEnd.Z * 304.8, 2) },
                NewStart = new { X = Math.Round(newStart.X * 304.8, 2), Y = Math.Round(newStart.Y * 304.8, 2), Z = Math.Round(newStart.Z * 304.8, 2) },
                NewEnd = new { X = Math.Round(newEnd.X * 304.8, 2), Y = Math.Round(newEnd.Y * 304.8, 2), Z = Math.Round(newEnd.Z * 304.8, 2) },
                Message = "成功調整元素端點"
            };
        }

        private static XYZ ResolveEndpointXY(JObject point, XYZ current, string name)
        {
            if (point == null) return current;
            double? x = point["x"]?.Value<double>();
            double? y = point["y"]?.Value<double>();
            if (x == null || y == null)
                throw new Exception($"{name} 需要 x 與 y (mm)");
            return new XYZ(x.Value / 304.8, y.Value / 304.8, current.Z);
        }

        /// <summary>
        /// 翻轉元素 facing 或 hand（門/窗）
        /// 來源 fork: poisonsam/main:MCP/Core/Commands/CommandExecutor.Base.cs:754-806
        /// </summary>
        private object FlipElement(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType elementId = parameters["elementId"]?.Value<IdType>() ?? 0;
            string flipType = parameters["flipType"]?.Value<string>() ?? "facing";

            Element element = doc.GetElement(new ElementId(elementId));
            if (element == null)
            {
                throw new Exception($"找不到元素 ID: {elementId}");
            }

            if (!(element is FamilyInstance familyInstance))
            {
                throw new Exception($"元素 ID: {elementId} 不是有效的家庭實體，無法翻轉");
            }

            using (Transaction trans = new Transaction(doc, $"翻轉元素: {elementId}"))
            {
                trans.Start();

                if (flipType.ToLower() == "facing")
                {
                    if (familyInstance.CanFlipFacing)
                        familyInstance.flipFacing();
                    else
                        throw new Exception("此元素不支援翻轉面向 (Facing)");
                }
                else if (flipType.ToLower() == "hand")
                {
                    if (familyInstance.CanFlipHand)
                        familyInstance.flipHand();
                    else
                        throw new Exception("此元素不支援翻轉開向 (Hand)");
                }
                else
                {
                    throw new Exception("無效的翻轉類型，請使用 'facing' 或 'hand'");
                }

                trans.Commit();

                return new
                {
                    ElementId = elementId,
                    FlipType = flipType,
                    Message = "成功翻轉元素"
                };
            }
        }

        #endregion

        #region 元素參數複製（CreateDoor / CreateWindow 之 sourceElementId 支援用）

        /// <summary>
        /// 複製來源 Element 的 instance parameters 到 target Element
        /// 排除：唯讀、樓層 / 主體 / ID 類別、標記欄位（由建立時決定，不應複製）
        /// 來源 fork: poisonsam/main:MCP/Core/Commands/CommandExecutor.Architecture.cs:171-213
        /// 改動：移除原檔重複的 IsReadOnly check（line 178）
        /// </summary>
        private void CopyInstanceParameters(Element source, Element target)
        {
            foreach (Parameter sourceParam in source.Parameters)
            {
                if (sourceParam.IsReadOnly || !sourceParam.HasValue) continue;

                string paramName = sourceParam.Definition.Name;
                if (paramName.Contains("樓層") || paramName.Contains("Level") ||
                    paramName.Contains("主體") || paramName.Contains("Host") ||
                    paramName.Contains("ID") ||
                    paramName == "標記" || paramName == "Mark")
                    continue;

                Parameter targetParam = target.LookupParameter(sourceParam.Definition.Name);
                if (targetParam != null && !targetParam.IsReadOnly)
                {
                    try
                    {
                        switch (sourceParam.StorageType)
                        {
                            case StorageType.String:
                                targetParam.Set(sourceParam.AsString());
                                break;
                            case StorageType.Double:
                                targetParam.Set(sourceParam.AsDouble());
                                break;
                            case StorageType.Integer:
                                targetParam.Set(sourceParam.AsInteger());
                                break;
                            case StorageType.ElementId:
                                targetParam.Set(sourceParam.AsElementId());
                                break;
                        }
                    }
                    catch { /* 忽略個別參數設定失敗 */ }
                }
            }
        }

        #endregion

    }
}
