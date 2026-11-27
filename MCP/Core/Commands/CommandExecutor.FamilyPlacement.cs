using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;

#nullable disable

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    // =====================================================================================
    // 泛用族群批次放置 —— Stage 2: place_family_instances
    // -------------------------------------------------------------------------------------
    // repo 內既有 9 處 NewFamilyInstance 呼叫全部綁死特定品類或情境（例如 PlaceFurniture
    // 寫死 OST_Furniture、CreateDoor/CreateWindow 寫死 OST_Doors/OST_Windows 並要求 hosting
    // wall）。本檔提供不綁品類的泛用放置：呼叫端給世界座標點（mm）與 FamilySymbol（優先用
    // ElementId，其次用 familyName/typeName 名稱比對），批次放置並可在放置後設定實例參數。
    //
    // 整體骨架參考 CommandExecutor.cs 既有的 PlaceFurniture（約 1485-1557 行）改寫。
    //
    // 設計要點：
    //   - 單一 Transaction 包住全部 placements，避免逐筆開交易的效能問題。
    //   - 用 TransactionHelper.Begin 而非裸 new Transaction，吞掉 MEP 族群放置常見的
    //     warning dialog。呼叫端仍需自行 Start()（該 helper 只負責 ctor + 設定
    //     FailureHandlingOptions，不主動 Start——與既有 new Transaction(doc, name) 呼叫慣例一致）。
    //   - 部分失敗不中止整批：每一筆用 try/catch 包起來，失敗記進該筆的 Error，
    //     繼續處理下一筆，最後回報成功/失敗計數。
    //   - ParametersSet 是誠實回報的關鍵：設定完一筆 placement 的所有參數後，一次性
    //     Regenerate（不是每個參數各自 Regenerate 一次，避免在「45 筆 x 每筆多參數」的雙層
    //     迴圈內疊加、撞破 MCP-Server 端非跨文件命令的 30 秒 timeout），再逐一讀回
    //     WrittenBackDisplay（Parameter.AsValueString()，Revit 依專案顯示單位格式化的值）與
    //     WrittenBackRaw（依 StorageType 取的內部單位原始值）兩個獨立欄位，兩者互不補值、
    //     各自可能為 null。不在工具內做任何單位換算或猜測——domain/tool-capability-boundary.md
    //     的 lesson L16（§L16，實測表在該檔「寫入值／讀回值／比值」表格）記錄過
    //     modify_element_parameter 走內部單位「寫入 89.2 讀回 9093」的落差，呼叫端無法從
    //     「Set() 回傳 true」推論寫進去的就是他要的量，只能靠讀回的兩個實際值自行判斷是數字
    //     寫錯還是單位換算錯。
    // =====================================================================================

    public partial class CommandExecutor
    {
        /// <summary>
        /// 批次把族群實例放置到指定世界座標點，並可在放置後設定實例參數。單一 Transaction。
        /// </summary>
        private object PlaceFamilyInstances(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            string categoryName = parameters["category"]?.Value<string>();
            ElementId categoryId = ElementId.InvalidElementId;
            if (!string.IsNullOrWhiteSpace(categoryName))
            {
                categoryId = ResolveCategoryId(doc, categoryName);
                if (categoryId == null || categoryId == ElementId.InvalidElementId)
                {
                    // 不靜默退回全域搜尋：category 給了但解析不出來，代表呼叫端的品類名稱有問題，
                    // 直接告知而不是默默擴大名稱比對範圍（會放大名稱誤中風險，見下方 ResolveFamilySymbolForPlacement 的說明）。
                    throw new Exception($"品類名稱 {categoryName} 無法解析，請用 Revit 品類名稱（如 'Air Terminals'）或 OST_ 開頭的 BuiltInCategory 名稱（如 'OST_DuctTerminal'）。");
                }
            }

            JArray placementsArray = parameters["placements"] as JArray;
            if (placementsArray == null || placementsArray.Count == 0)
            {
                throw new Exception("placements 為必填，且至少需要一筆放置指令。");
            }

            // FamilySymbol 候選清單只收集一次（效能），依 category 縮小範圍（若有指定且解析成功）。
            IEnumerable<FamilySymbol> symbolQuery = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>();

            if (categoryId != ElementId.InvalidElementId)
            {
                symbolQuery = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .WherePasses(new ElementCategoryFilter(categoryId))
                    .Cast<FamilySymbol>();
            }

            List<FamilySymbol> candidateSymbols = symbolQuery.ToList();

            var results = new List<object>();
            int successCount = 0;
            int failureCount = 0;

            using (Transaction trans = TransactionHelper.Begin(doc, "批次放置族群實例"))
            {
                trans.Start();

                for (int index = 0; index < placementsArray.Count; index++)
                {
                    JObject placement = placementsArray[index] as JObject ?? new JObject();
                    string tag = placement["tag"]?.Value<string>();
                    IdType? createdElementId = null;

                    try
                    {
                        object rowResult = PlaceSingleFamilyInstance(doc, candidateSymbols, index, tag, placement, out createdElementId);
                        results.Add(rowResult);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        // 若實例已經建立（createdElementId 有值）才在後續步驟（旋轉／參數／讀回）失敗，
                        // 該實例仍會隨整批交易一起 Commit 進模型——不能讓呼叫端拿不到 ElementId 而無從追蹤或清理。
                        string errorMessage = createdElementId.HasValue
                            ? $"實例已建立（ElementId={createdElementId.Value}）但後續步驟失敗: {ex.Message}"
                            : ex.Message;

                        results.Add(new
                        {
                            Index = index,
                            Tag = tag,
                            Success = false,
                            ElementId = createdElementId.HasValue ? (object)createdElementId.Value : null,
                            Error = errorMessage,
                            PlacedPoint = (object)null,
                            PlacedPointSource = (object)null,
                            PlacedBBoxCenter = (object)null,
                            LevelName = (object)null,
                            ParametersSet = new List<object>()
                        });
                        failureCount++;
                    }
                }

                trans.Commit();
            }

            return new
            {
                Success = failureCount == 0,
                TotalCount = placementsArray.Count,
                SuccessCount = successCount,
                FailureCount = failureCount,
                Results = results
            };
        }

        /// <summary>
        /// 放置單一筆 placement。任何步驟失敗都會拋出例外，由呼叫端的 try/catch 記錄成該筆的失敗結果——
        /// 這裡本身不吞例外，確保「部分失敗不中止整批」的邏輯集中在外層一處。
        /// createdElementId 在 NewFamilyInstance 成功的當下就會賦值，即使後續步驟（旋轉/參數）拋例外，
        /// 呼叫端仍可從這個 out 參數拿到已建立的 ElementId（避免孤兒元件追蹤不到）。
        /// </summary>
        private object PlaceSingleFamilyInstance(
            Document doc,
            List<FamilySymbol> candidateSymbols,
            int index,
            string tag,
            JObject placement,
            out IdType? createdElementId)
        {
            createdElementId = null;

            FamilySymbol symbol = ResolveFamilySymbolForPlacement(doc, candidateSymbols, placement);

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            JToken hostIdToken = placement["hostId"];
            bool hasHost = hostIdToken != null && hostIdToken.Type != JTokenType.Null;

            if (placement["x"] == null || placement["y"] == null || (!hasHost && placement["z"] == null))
            {
                throw new Exception("x / y 為必填；未給 hostId 時 z 也必填（世界座標，單位 mm）。");
            }

            double xMm = placement["x"].Value<double>();
            double yMm = placement["y"].Value<double>();

            if (hasHost)
            {
                return PlaceSingleHostedFamilyInstance(doc, symbol, index, tag, placement,
                    hostIdToken.Value<IdType>(), xMm, yMm, out createdElementId);
            }

            double zMm = placement["z"].Value<double>();
            // z 是世界座標（mm），與 get_space_centroid 回傳的座標系一致——BREAKING（2026-08-15）：
            // 舊版把這個值直接當成 NewFamilyInstance 的 point.Z（樓層偏移語意），已改為下方換算。
            double zWorldFeet = zMm / 304.8;

            string levelName = placement["levelName"]?.Value<string>();
            Level level = ResolveLevelForPlacement(doc, levelName, zWorldFeet, zMm);

            // 2026-08-14 在單一模型、單一批設備族群上實測到 NewFamilyInstance(XYZ, FamilySymbol, Level,
            // StructuralType) 的 point.Z 被當成相對該樓層標高的偏移，不是世界座標：對照 GROUND FLOOR
            // （標高 30480mm）傳 z=33123.2，實際落點卻是世界 Z=63603.2（多了 30480）；SECOND FLOOR 傳
            // z=0，實際落點卻是世界 Z=34544。Revit API 文件把這個 overload 的 location 參數定義為
            // "The physical location where the instance is to be placed."，未記載偏移語意；同一份
            // remarks 說明 Beams 等多端點族群「插入方式與單點族群相同」（"inserted in the same manner as
            // single point instances"），但插入後其端點需改由 Element.Location 調整——亦即 Location 的
            // 語意本來就隨族群型式而異，故本行為極可能是族群／放置型式相依，不是這個 API 方法本身的固定
            // 性質。本工具不綁品類，遇到未實測過的族群務必先放一件核對（見 domain/space-centroid-placement.md
            // 第三節）。這裡把呼叫端給的世界座標換算成偏移值，讓 get_space_centroid 的輸出可以直接餵進來，
            // 不必再由呼叫端手動扣除樓層標高。
            // 用 Level.ProjectElevation 而非 Level.Elevation：後者受 Elevation Base 型別參數
            // （Project／Shared）影響，在使用 shared coordinates 的專案可能回傳 shared 原點基準的值；
            // get_space_centroid 讀的是 internal（project）座標系，兩邊必須用同一個基準相減，
            // 否則在那類專案會整層偏移——這正是本次要修的同型錯誤，不能用「看起來也是標高」的屬性頂替。
            double zForPlacementFeet = zWorldFeet - level.ProjectElevation;
            XYZ pointFeet = new XYZ(xMm / 304.8, yMm / 304.8, zForPlacementFeet);
            // pointFeet 只餵 NewFamilyInstance（Z 是換算後的樓層相對偏移）；requestedWorldFeet 是同一筆
            // 輸入的世界座標版本，只供下面 PlacedPoint 的回退分支使用——兩者不可互換。若讓 pointFeet 外流到
            // PlacedPoint，PlacedPointSource='RequestedFallback' 分支會同時偏離「世界座標」（PlacedPoint 的
            // 宣告語意）與「呼叫端給的值」（RequestedFallback 的宣告語意），變成兩者皆非的第三種值。
            XYZ requestedWorldFeet = new XYZ(xMm / 304.8, yMm / 304.8, zWorldFeet);

            double rotationDeg = placement["rotation"]?.Value<double>() ?? 0;

            FamilyInstance instance = doc.Create.NewFamilyInstance(
                pointFeet, symbol, level, StructuralType.NonStructural);
            createdElementId = instance.Id.GetIdValue();
            return FinishPlacedInstance(doc, instance, index, tag, placement, requestedWorldFeet, rotationDeg, level.Name);
        }

        /// <summary>
        /// 放置後的共同收尾（Regenerate → 旋轉 → 設參數 → 讀回落點與 BBox），一般放置與主體放置共用。
        /// requestedWorldFeet 是讀不到 LocationPoint 時的回退值（世界座標）。
        /// </summary>
        private object FinishPlacedInstance(
            Document doc,
            FamilyInstance instance,
            int index,
            string tag,
            JObject placement,
            XYZ requestedWorldFeet,
            double rotationDeg,
            string levelName)
        {
            // 2026-08-14 實測：Exhauster with Cabinet 族群放置後，未 Regenerate 就讀 LocationPoint
            // 會拿到 (0,0,0)——BoundingBox 證實元件實際位置正確，屬「轉型成功但值無意義」（Location as
            // LocationPoint 非 null，但 .Point 尚未反映新建元件的實際位置）；同批 AHU 沒有這個問題，
            // 是族群相依的行為，當時差點因此誤刪三台放對的機器。
            // 在讀 LocationPoint（下面的旋轉軸）之前先補一次 Regenerate，順便修好「旋轉軸用到未重生
            // LocationPoint」的潛在錯位。這是本筆 placement 在「放置後」路徑上的 Regenerate；
            // ApplyPlacementParameters 內設完全部參數後另有一次（一次性，見該方法註解）。上限為每筆 2 次，
            // 再加上每個未啟用族群類型首次啟用時的 1 次（見上方 symbol.Activate() 處，每個 distinct
            // symbol 只會發生一次，不隨 placement 筆數累加）——不得在任何逐參數或逐筆的內層迴圈中額外呼叫
            // Regenerate，30 秒 MCP timeout 且逾時後交易可能已提交的教訓見同一方法註解，呼叫端請沿用
            // domain/space-centroid-placement.md 的分批估算。
            doc.Regenerate();

            // 旋轉軸要用「實際落點」而非呼叫端要求的座標——NewFamilyInstance 可能因族群
            // 放置條件對點做吸附/調整，用要求點當軸心會讓旋轉同時產生非預期位移。
            // 讀不到 LocationPoint 時退回 requestedWorldFeet（世界座標）而非 pointFeet（樓層相對偏移）：
            // 軸線只用 X/Y 定水平位置、沿鉛直方向延伸，Z 座標基準不影響旋轉結果本身；但這個變數同時是
            // 下面 PlacedPoint 讀不到 LocationPoint 時的最終回退來源（見 finalPointFeet），退到 pointFeet
            // 會讓 PlacedPoint 混進換算後的偏移值，見上方 requestedWorldFeet 宣告處的說明。
            XYZ pointBeforeRotationFeet = (instance.Location as LocationPoint)?.Point ?? requestedWorldFeet;

            if (Math.Abs(rotationDeg) > 1e-9)
            {
                Line axis = Line.CreateBound(pointBeforeRotationFeet, pointBeforeRotationFeet + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotationDeg * Math.PI / 180.0);
            }

            List<object> parametersSet = ApplyPlacementParameters(doc, instance, placement["parameters"] as JObject);

            // 旋轉完成後重新讀一次實際落點：旋轉理論上不移動位置，但一律「讀回真實狀態」而非假設，
            // 與整支工具「不靜默、不猜測」的原則一致。
            // 這裡不能靜默退回 pointBeforeRotationFeet（其自身的回退終點是 requestedWorldFeet，非
            // pointFeet，見上方宣告處說明）——取不到 LocationPoint 時 PlacedPoint 會
            // 等於呼叫端自己給的 x/y/z，若沒有來源標示，呼叫端無從分辨「這就是實際落點」還是「讀不到、這是你給的值」，
            // 會把 F7 想偵測吸附位移的訊號變成假陰性。用 PlacedPointSource 誠實揭露，比照 Stage 1
            // get_space_centroid 的 PointSource='None' 慣例，同一個專案對同一種不確定性給出一致的誠實度。
            // PlacedPoint 本身不需要換算：LocationPoint.Point 本來就是世界座標，讀回照舊即可——
            // 這正是呼叫端未來實測核對本次 z 語意修正是否生效的依據。
            LocationPoint finalLocationPoint = instance.Location as LocationPoint;
            XYZ finalPointFeet = finalLocationPoint?.Point ?? pointBeforeRotationFeet;
            string placedPointSource = finalLocationPoint != null ? "LocationPoint" : "RequestedFallback";

            // PlacedBBoxCenter 是與 LocationPoint 獨立的第二訊號：2026-08-14 實測某些族群
            // （如 Exhauster with Cabinet）的 LocationPoint.Point 會是 (0,0,0) 而 BoundingBox 是對的。
            // BoundingBox 為 null 時回 null，不得用 PlacedPoint 補值——補值會讓這個獨立訊號失去
            // 存在意義（互不補值原則同「參數寫入」一節的 WrittenBackDisplay/WrittenBackRaw）。
            // get_BoundingBox(null) 依 Revit API 文件未宣告任何例外、讀不到會直接回 null，理論上不需要
            // try/catch；但這裡仍包一層防禦——萬一它真的丟例外，不能讓次要診斷訊號的失敗連帶摧毀已經算好
            // 的 PlacedPoint/PlacedPointSource/ParametersSet（外層 catch 會把整列翻成失敗列），與
            // ApplyPlacementParameters 內「單一訊號失敗不拖累其他訊號」的既有原則一致。
            BoundingBoxXYZ finalBBox = null;
            try
            {
                finalBBox = instance.get_BoundingBox(null);
            }
            catch
            {
                finalBBox = null;
            }
            object placedBBoxCenter = null;
            if (finalBBox != null)
            {
                XYZ bboxCenterFeet = (finalBBox.Min + finalBBox.Max) * 0.5;
                placedBBoxCenter = new
                {
                    X = Math.Round(bboxCenterFeet.X * 304.8, 2),
                    Y = Math.Round(bboxCenterFeet.Y * 304.8, 2),
                    Z = Math.Round(bboxCenterFeet.Z * 304.8, 2)
                };
            }

            return new
            {
                Index = index,
                Tag = tag,
                Success = true,
                ElementId = instance.Id.GetIdValue(),
                Error = (object)null,
                PlacedPoint = new
                {
                    X = Math.Round(finalPointFeet.X * 304.8, 2),
                    Y = Math.Round(finalPointFeet.Y * 304.8, 2),
                    Z = Math.Round(finalPointFeet.Z * 304.8, 2)
                },
                PlacedPointSource = placedPointSource,
                PlacedBBoxCenter = placedBBoxCenter,
                LevelName = levelName,
                HostId = instance.Host != null ? (object)instance.Host.Id.GetIdValue() : null,
                ParametersSet = parametersSet
            };
        }

        /// <summary>
        /// 主體放置（placement 有 hostId）：以 (x, y) 作鉛直線與主體指定面（hostFace，預設 bottom）求交，
        /// 依族群放置型式選 NewFamilyInstance overload：
        ///   - WorkPlaneBased（面式族群）→ NewFamilyInstance(Reference face, XYZ, XYZ refDir, FamilySymbol)
        ///   - OneLevelBasedHosted（如天花板式燈具樣板）→ NewFamilyInstance(XYZ, FamilySymbol, Element host, Level, StructuralType)
        /// 其他放置型式直接報錯，不退回無主體放置。z 在此分支被忽略——高度由主體面決定。
        /// 需求來源：2026-11-18 燈具以無主體方式放置時，"Elevation from Level" 與 z 都無法把燈貼到天花板底面，
        /// 實務上燈具必須以天花板為主體才會隨天花板移動、也才會在天花平面圖正確顯示。
        /// </summary>
        private object PlaceSingleHostedFamilyInstance(
            Document doc,
            FamilySymbol symbol,
            int index,
            string tag,
            JObject placement,
            IdType hostIdValue,
            double xMm,
            double yMm,
            out IdType? createdElementId)
        {
            createdElementId = null;

            Element host = doc.GetElement(hostIdValue.ToElementId());
            if (host == null)
                throw new Exception($"hostId {hostIdValue} 找不到對應元素。");

            HostObject hostObj = host as HostObject;
            if (hostObj == null)
                throw new Exception($"hostId {hostIdValue}（品類 {host.Category?.Name}）不是 HostObject（天花板／樓板／牆／屋頂），無法作為主體。");

            if (host is Wall wallHost)
            {
                return PlaceSingleWallHostedFamilyInstance(doc, symbol, index, tag, placement,
                    wallHost, xMm, yMm, out createdElementId);
            }

            string hostFace = placement["hostFace"]?.Value<string>();
            string faceKey = string.IsNullOrWhiteSpace(hostFace) ? "bottom" : hostFace.Trim().ToLowerInvariant();
            IList<Reference> faceRefs;
            if (faceKey == "bottom")
                faceRefs = HostObjectUtils.GetBottomFaces(hostObj);
            else if (faceKey == "top")
                faceRefs = HostObjectUtils.GetTopFaces(hostObj);
            else
                throw new Exception($"hostFace 只接受 'bottom' 或 'top'，收到 '{hostFace}'。");

            BoundingBoxXYZ hostBox = host.get_BoundingBox(null);
            if (hostBox == null)
                throw new Exception($"hostId {hostIdValue} 讀不到 BoundingBox，無法建立垂直投射線。");

            double xFeet = xMm / 304.8;
            double yFeet = yMm / 304.8;
            Line ray = Line.CreateBound(
                new XYZ(xFeet, yFeet, hostBox.Min.Z - 1.0),
                new XYZ(xFeet, yFeet, hostBox.Max.Z + 1.0));

            Reference hitRef = null;
            XYZ hitPoint = null;
            foreach (Reference r in faceRefs)
            {
                Face f = hostObj.GetGeometryObjectFromReference(r) as Face;
                if (f == null) continue;
                IntersectionResultArray ira;
                if (f.Intersect(ray, out ira) == SetComparisonResult.Overlap && ira != null && ira.Size > 0)
                {
                    hitRef = r;
                    hitPoint = ira.get_Item(0).XYZPoint;
                    break;
                }
            }

            if (hitRef == null)
                throw new Exception($"點 ({xMm}, {yMm}) 不在主體 {hostIdValue} 的 {faceKey} 面範圍內（可能落在天花板開口或邊界外）。");

            double rotationDeg = placement["rotation"]?.Value<double>() ?? 0;

            Level level = doc.GetElement(host.LevelId) as Level;
            FamilyPlacementType placementType = symbol.Family.FamilyPlacementType;
            FamilyInstance instance;
            switch (placementType)
            {
                case FamilyPlacementType.WorkPlaneBased:
                    instance = doc.Create.NewFamilyInstance(hitRef, hitPoint, XYZ.BasisX, symbol);
                    break;
                case FamilyPlacementType.OneLevelBasedHosted:
                    if (level == null)
                        throw new Exception($"主體 {hostIdValue} 沒有所屬樓層，OneLevelBasedHosted 族群需要樓層。");
                    instance = doc.Create.NewFamilyInstance(hitPoint, symbol, host, level, StructuralType.NonStructural);
                    break;
                default:
                    throw new Exception($"族群 {symbol.FamilyName} 的放置型式為 {placementType}，不支援主體放置（僅支援 WorkPlaneBased 面式族群與 OneLevelBasedHosted）。");
            }
            createdElementId = instance.Id.GetIdValue();

            string levelName = level?.Name ?? (doc.GetElement(instance.LevelId) as Level)?.Name;
            return FinishPlacedInstance(doc, instance, index, tag, placement, hitPoint, rotationDeg, levelName);
        }

        /// <summary>
        /// 牆主體放置（hostId 是 Wall）：(x, y) 給「牆面那一側、房間內」的平面點。
        /// 沿牆位置 = (x, y) 投影到牆位置線（只支援直線牆）；放置側 = (x, y) 相對牆中心線落在哪一側
        /// （以 Wall.Orientation 判斷）；落點放在該側牆面上（中心線 ± Width/2）。
        /// 高度：落點 Z 取主體牆所屬樓層的 ProjectElevation；若族群有 INSTANCE_ELEVATION_PARAM
        /// 且呼叫端給了 elevationFromLevel（mm），放置後寫入。z 與 hostFace 在此分支不使用。
        /// 放置後以實例 BoundingBox 中心判斷幾何實際落在牆的哪一側；落在相反側且 CanFlipFacing
        /// 時自動 flipFacing 一次；不可翻轉時改以牆中心線鏡射副本並刪除原件。結果在回傳的 WallPlacement 揭露
        /// （RequestedSide / BBoxCenterOffset… / Flipped / MirroredAcrossWall / GeometryOnRequestedSide）。
        /// 需求來源：2026-11-19 無障礙廁所的扶手、沐浴椅、求助鈴都是牆主體通用模型，原工具只能放天花板／樓板。
        /// </summary>
        private object PlaceSingleWallHostedFamilyInstance(
            Document doc,
            FamilySymbol symbol,
            int index,
            string tag,
            JObject placement,
            Wall wall,
            double xMm,
            double yMm,
            out IdType? createdElementId)
        {
            createdElementId = null;

            if (symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBasedHosted)
                throw new Exception($"族群 {symbol.FamilyName} 的放置型式為 {symbol.Family.FamilyPlacementType}，牆主體放置只支援 OneLevelBasedHosted（牆式樣板）族群。");

            LocationCurve lc = wall.Location as LocationCurve;
            Line wallLine = lc?.Curve as Line;
            if (wallLine == null)
                throw new Exception($"牆 {wall.Id.GetIdValue()} 不是直線牆，牆主體放置目前只支援直線牆。");

            Level level = doc.GetElement(wall.LevelId) as Level;
            if (level == null)
                throw new Exception($"牆 {wall.Id.GetIdValue()} 沒有所屬樓層。");

            double zLine = wallLine.GetEndPoint(0).Z;
            XYZ requested = new XYZ(xMm / 304.8, yMm / 304.8, zLine);
            XYZ proj = wallLine.Project(requested).XYZPoint;
            XYZ normal = new XYZ(wall.Orientation.X, wall.Orientation.Y, 0).Normalize();
            double signedDist = (requested - proj).DotProduct(normal);
            if (Math.Abs(signedDist) * 304.8 < 1.0)
                throw new Exception("(x, y) 落在牆中心線上，無法判斷放在哪一側；請給牆面那一側、房間內的點。");
            double side = signedDist > 0 ? 1.0 : -1.0;
            XYZ sideVec = normal * side;

            XYZ facePt = new XYZ(proj.X, proj.Y, level.ProjectElevation) + sideVec * (wall.Width / 2.0);

            // 2026-11-19 Revit 2027 實測：牆主體 OneLevelBasedHosted 實例的幾何一律落在牆 Orientation
            // （外側）那面，與落點在哪一側無關；族群沒有翻轉控制時 CanFlipFacing = false，無法事後翻面。
            // 改用帶 referenceDirection 的 overload 也不會換面，且會丟失樓層（Z 落到 0），已退回本 overload。
            // 要放在牆的內側：先翻牆（Wall.Flip，flip_element）或在族群加翻轉控制。下方 GeometryOnRequestedSide
            // 會誠實回報是否落在要求的那側。
            FamilyInstance instance = doc.Create.NewFamilyInstance(facePt, symbol, wall, level, StructuralType.NonStructural);
            createdElementId = instance.Id.GetIdValue();

            JToken elevToken = placement["elevationFromLevel"];
            string elevationNote = null;
            if (elevToken != null && elevToken.Type != JTokenType.Null)
            {
                Parameter ep = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                if (ep != null && !ep.IsReadOnly)
                    ep.Set(elevToken.Value<double>() / 304.8);
                else
                    elevationNote = "族群沒有可寫的 Elevation from Level 參數，elevationFromLevel 未套用。";
            }

            doc.Regenerate();

            Func<double?> geometrySide = () =>
            {
                BoundingBoxXYZ bb = instance.get_BoundingBox(null);
                if (bb == null) return null;
                XYZ c = (bb.Min + bb.Max) * 0.5;
                return new XYZ(c.X - proj.X, c.Y - proj.Y, 0).DotProduct(sideVec) * 304.8;
            };

            double? before = geometrySide();
            bool flipped = false;
            bool mirrored = false;
            IdType? discardedOriginalId = null;
            if (before.HasValue && before.Value < 0 && instance.CanFlipFacing)
            {
                instance.flipFacing();
                doc.Regenerate();
                flipped = true;
            }
            else if (before.HasValue && before.Value < 0)
            {
                // 族群沒有翻轉控制（CanFlipFacing = false）：以牆中心線所在的鉛直面鏡射出副本（同 UI 的 Mirror），
                // 副本會掛在牆的另一面；保留副本、刪除原件。實例的 Mirrored 會是 true——對沿牆方向對稱的
                // 扶手／求助鈴／座椅沒有影響，非對稱族群請呼叫端自行核對。
                Plane mirrorPlane = Plane.CreateByNormalAndOrigin(normal, proj);
                IList<ElementId> copies = ElementTransformUtils.MirrorElements(
                    doc, new List<ElementId> { instance.Id }, mirrorPlane, true);
                FamilyInstance copy = copies
                    .Select(id => doc.GetElement(id) as FamilyInstance)
                    .FirstOrDefault(fi => fi != null);
                if (copy == null)
                    throw new Exception("幾何落在牆的另一側，且鏡射副本建立失敗；原實例保留，請手動處理。");
                discardedOriginalId = instance.Id.GetIdValue();
                doc.Delete(instance.Id);
                instance = copy;
                createdElementId = instance.Id.GetIdValue();
                doc.Regenerate();
                mirrored = true;
            }
            double? after = geometrySide();

            object baseResult = FinishPlacedInstance(doc, instance, index, tag, placement, facePt, 0, level.Name);
            JObject merged = JObject.FromObject(baseResult);
            merged["WallPlacement"] = JObject.FromObject(new
            {
                WallId = wall.Id.GetIdValue(),
                WallWidthMm = Math.Round(wall.Width * 304.8, 2),
                RequestedSide = side > 0 ? "exterior (Wall.Orientation)" : "interior (-Wall.Orientation)",
                FacePoint = new { X = Math.Round(facePt.X * 304.8, 2), Y = Math.Round(facePt.Y * 304.8, 2) },
                BBoxCenterOffsetFromWallCenterlineMm_BeforeFlip = before.HasValue ? (object)Math.Round(before.Value, 2) : null,
                BBoxCenterOffsetFromWallCenterlineMm = after.HasValue ? (object)Math.Round(after.Value, 2) : null,
                Flipped = flipped,
                MirroredAcrossWall = mirrored,
                DiscardedOriginalId = discardedOriginalId,
                CanFlipFacing = instance.CanFlipFacing,
                GeometryOnRequestedSide = after.HasValue ? (object)(after.Value > 0) : null,
                ElevationNote = elevationNote
            });
            return merged;
        }

        /// <summary>
        /// 解析單筆 placement 要用哪個 FamilySymbol：typeId 優先且不做名稱比對。
        /// 未給 typeId 時走名稱比對，採兩段式（而非單一 OR 串接，避免跨族群誤中）：
        ///   - familyName 有值 → 只在該 family 的子集合內比對 typeName（含 "FamilyName: TypeName" 複合寫法，比照 create_door）。
        ///     子集合內找不到、或命中多於一個都直接拋例外（多個必須改用 typeId，不取 FirstOrDefault 悶聲選第一個）。
        ///   - familyName 為空 → 才允許 typeName 單獨在全域比對；同樣地命中多於一個就拋例外要求改用 typeId。
        /// </summary>
        private FamilySymbol ResolveFamilySymbolForPlacement(Document doc, List<FamilySymbol> candidateSymbols, JObject placement)
        {
            JToken typeIdToken = placement["typeId"];
            if (typeIdToken != null && typeIdToken.Type != JTokenType.Null)
            {
                IdType typeId = typeIdToken.Value<IdType>();
                FamilySymbol byId = candidateSymbols.FirstOrDefault(fs => fs.Id.GetIdValue() == typeId);
                if (byId == null)
                {
                    // 也容許 typeId 落在 category 篩選範圍之外的情形：直接用 doc 查一次，
                    // 找到但不符 category 篩選時仍然採用（typeId 是明確指定，優先於 category 篩選）。
                    byId = doc.GetElement(typeId.ToElementId()) as FamilySymbol;
                }
                if (byId == null)
                {
                    throw new Exception($"typeId {typeId} 找不到對應的 FamilySymbol。");
                }
                return byId;
            }

            string familyName = placement["familyName"]?.Value<string>();
            string typeName = placement["typeName"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new Exception("未提供 typeId 時，familyName 與 typeName 為必填（至少需要 typeName）。");
            }

            if (!string.IsNullOrWhiteSpace(familyName))
            {
                List<FamilySymbol> familyPool = candidateSymbols
                    .Where(fs => fs.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (familyPool.Count == 0)
                {
                    throw new Exception($"找不到族群: {familyName}");
                }

                List<FamilySymbol> matches = familyPool
                    .Where(fs => fs.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                              || (fs.FamilyName + ": " + fs.Name).Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    string available = string.Join(", ", familyPool
                        .Select(fs => fs.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                    throw new Exception($"族群 {familyName} 底下找不到類型 {typeName}。該族群實際有的類型: {available}");
                }

                if (matches.Count > 1)
                {
                    throw new Exception($"族群 {familyName} 底下的類型 {typeName} 命中多個 FamilySymbol，請改用 typeId 明確指定。");
                }

                return matches[0];
            }
            else
            {
                List<FamilySymbol> matches = candidateSymbols
                    .Where(fs => fs.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                              || (fs.FamilyName + ": " + fs.Name).Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    throw new Exception($"找不到族群類型: {typeName}");
                }

                if (matches.Count > 1)
                {
                    string families = string.Join(", ", matches.Select(fs => fs.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase));
                    throw new Exception($"類型 {typeName} 命中多個 FamilySymbol（分屬族群: {families}），請改用 typeId 或加上 familyName 明確指定。");
                }

                return matches[0];
            }
        }

        /// <summary>
        /// 解析單筆 placement 要用哪個 Level。
        /// 給了 levelName：先做一次精確比對（l.Name == levelName，序數比較）；FindLevel（CommandExecutor.cs）
        /// 用的是雙向 Contains 模糊比對，"1F" 會命中 "B1F" 卻不報錯，所以精確比對優先，
        /// 沒有精確命中才退回既有 FindLevel(doc, levelName, false)（找不到就拋例外，不靜默 fallback 到第一個樓層）。
        /// 省略 levelName：取「ProjectElevation 最接近 z 且不高於 z」的 Level；仍找不到則拋例外由外層記為該筆失敗。
        /// zWorldFeet 是世界座標（BREAKING 2026-08-15：z 的語意從相對樓層偏移改為世界座標，這段自動選層
        /// 邏輯的比較基準同步改用 ProjectElevation——要用 z 改為世界座標之後才自洽。用 ProjectElevation
        /// 而非 Elevation 的理由見 PlaceSingleFamilyInstance 內換算 zForPlacementFeet 處的註解，兩處必須
        /// 用同一個基準，否則自動選層與實際放置換算會各用各的標高基準）。
        /// </summary>
        private Level ResolveLevelForPlacement(Document doc, string levelName, double zWorldFeet, double zMm)
        {
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                Level exact = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name == levelName);

                return exact ?? FindLevel(doc, levelName, false);
            }

            const double toleranceFeet = 1.0 / 304.8; // 1mm 容差，處理浮點邊界情形

            Level level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Where(l => l.ProjectElevation <= zWorldFeet + toleranceFeet)
                .OrderByDescending(l => l.ProjectElevation)
                .FirstOrDefault();

            if (level == null)
            {
                throw new Exception($"未指定 levelName，且找不到 ProjectElevation <= z（{zMm}mm，世界座標）的樓層。");
            }

            return level;
        }

        /// <summary>
        /// 設定放置後的實例參數。分兩階段：
        ///   1) 全部參數逐一 Set，成功的暫存 Parameter 參照（不立刻讀回）。
        ///   2) 全部 Set 完後「一次」Regenerate，再統一讀回——避免每個參數各自 Regenerate 一次，
        ///      在雙層迴圈（多筆 placement x 每筆多參數）內疊加成上百次呼叫，撞破 30 秒 timeout。
        /// 讀回仍然嚴格發生在 Regenerate 之後（維持「設定之後才讀回」的語意不變）。
        /// 這唯一一次 Regenerate() 本身若拋例外，不會向上傳播丟掉整批結果：改成把 pendingReadback
        /// 內的每一筆標記為失敗，Set 階段就已判定失敗、已有明確錯誤訊息的 slots 保持不受影響。
        /// 單一參數設定失敗不影響其他參數或該筆放置本身的成功狀態。回傳順序與輸入的 parameters 屬性順序一致。
        /// </summary>
        private List<object> ApplyPlacementParameters(Document doc, FamilyInstance instance, JObject parametersToken)
        {
            var results = new List<object>();
            if (parametersToken == null)
                return results;

            List<JProperty> propList = parametersToken.Properties().ToList();
            var slots = new object[propList.Count];
            var pendingReadback = new List<(int SlotIndex, string Name, object Requested, Parameter Param)>();

            for (int i = 0; i < propList.Count; i++)
            {
                JProperty prop = propList[i];
                string paramName = prop.Name;
                object requested = JTokenToPlainValue(prop.Value);

                try
                {
                    Parameter param = instance.LookupParameter(paramName);
                    if (param == null)
                    {
                        slots[i] = BuildParameterResult(paramName, requested, null, null, false, $"找不到參數: {paramName}");
                        continue;
                    }

                    if (param.IsReadOnly)
                    {
                        slots[i] = BuildParameterResult(paramName, requested, null, null, false, $"參數 {paramName} 是唯讀的");
                        continue;
                    }

                    bool set = SetParameterFromToken(param, prop.Value);
                    if (!set)
                    {
                        slots[i] = BuildParameterResult(paramName, requested, null, null, false, $"設定參數 {paramName} 失敗（型別不符或 Revit 拒絕該值）");
                        continue;
                    }

                    pendingReadback.Add((i, paramName, requested, param));
                }
                catch (Exception ex)
                {
                    slots[i] = BuildParameterResult(paramName, requested, null, null, false, ex.Message);
                }
            }

            if (pendingReadback.Count > 0)
            {
                bool regenerateOk = true;
                try
                {
                    doc.Regenerate();
                }
                catch (Exception ex)
                {
                    // Regenerate() 本身可能因參數值造成族群無法重生而拋例外（例如 InvalidOperationException）。
                    // 這裡不向上拋——若拋出去，外層 catch 會把整個 slots 陣列（含 Set 階段就已判定失敗、且已有
                    // 明確錯誤訊息的參數筆）一起丟棄，變成只剩一句籠統的 Regenerate 錯誤。
                    // 因此改為把 pendingReadback 內每一筆都標記失敗，其餘已寫入的 slots 保持原樣不受影響。
                    regenerateOk = false;
                    foreach (var pending in pendingReadback)
                    {
                        slots[pending.SlotIndex] = BuildParameterResult(pending.Name, pending.Requested, null, null, false, $"參數已 Set 但 Regenerate 失敗，無法讀回: {ex.Message}");
                    }
                }

                if (regenerateOk)
                {
                    foreach (var pending in pendingReadback)
                    {
                        try
                        {
                            // WrittenBackDisplay 與 WrittenBackRaw 各自獨立讀取，互不補值：
                            // 其中一個是 null 就照實回 null，不能用另一個去補，否則呼叫端會誤以為拿到兩個獨立訊號。
                            object writtenBackDisplay = pending.Param.AsValueString();
                            object writtenBackRaw = ReadBackParameterRawValue(pending.Param);
                            slots[pending.SlotIndex] = BuildParameterResult(pending.Name, pending.Requested, writtenBackDisplay, writtenBackRaw, true, null);
                        }
                        catch (Exception ex)
                        {
                            slots[pending.SlotIndex] = BuildParameterResult(pending.Name, pending.Requested, null, null, false, $"設定成功但讀回失敗: {ex.Message}");
                        }
                    }
                }
            }

            results.AddRange(slots);
            return results;
        }

        private static object BuildParameterResult(string name, object requested, object writtenBackDisplay, object writtenBackRaw, bool success, string error)
        {
            return new
            {
                Name = name,
                Requested = requested,
                WrittenBackDisplay = writtenBackDisplay,
                WrittenBackRaw = writtenBackRaw,
                Success = success,
                Error = (object)error
            };
        }

        private bool SetParameterFromToken(Parameter param, JToken value)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.Set(value?.Type == JTokenType.Null ? "" : value.ToString());

                case StorageType.Double:
                    return param.Set(value.Value<double>());

                case StorageType.Integer:
                    if (value.Type == JTokenType.Boolean)
                        return param.Set(value.Value<bool>() ? 1 : 0);
                    return param.Set(value.Value<int>());

                case StorageType.ElementId:
                    IdType idValue = value.Value<IdType>();
                    return param.Set(idValue.ToElementId());

                default:
                    return false;
            }
        }

        /// <summary>
        /// 依 StorageType 讀回參數的內部單位原始值（不經 Revit 顯示格式化）。
        /// 與 AsValueString()（顯示值）並列回傳，讓呼叫端能區分「數字寫錯」與「單位換算問題」——
        /// 這兩種錯誤的處置完全不同（前者改輸入，後者改換算係數），只給其中一個訊號無法判斷。
        /// </summary>
        private object ReadBackParameterRawValue(Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Double:
                    return param.AsDouble();
                case StorageType.Integer:
                    return param.AsInteger();
                case StorageType.ElementId:
                    // id 為 null 才回 null（讀不出來）；id 為 InvalidElementId 時仍回 GetIdValue()（即 -1），
                    // 讓「已設定但無效的 ElementId」與「這個 StorageType 讀不出原始值」在回傳值上可區分，不壓成同一個 null 訊號。
                    ElementId id = param.AsElementId();
                    return id != null ? (object)id.GetIdValue() : null;
                default:
                    return null;
            }
        }

        private static object JTokenToPlainValue(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.Integer: return token.Value<long>();
                case JTokenType.Float: return token.Value<double>();
                case JTokenType.Boolean: return token.Value<bool>();
                case JTokenType.String: return token.Value<string>();
                case JTokenType.Null: return null;
                default: return token.ToString();
            }
        }
    }
}
