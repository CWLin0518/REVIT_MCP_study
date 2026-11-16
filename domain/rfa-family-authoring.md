---
name: rfa-family-authoring
description: "用 rfa_* 工具在族群編輯器中建立參數化族群的 SOP：前置條件、建立順序（類型→參數→參考平面→尺寸標籤→形體→對齊鎖定→撓曲測試）、四種形體（擠出/融合/掃掠/參考線掃掠）的座標約定、參數化管件配方，以及實測確認行不通的路線。觸發：族群建模、RFA 建模、參數化族群、族群編輯器、rfa_create_extrusion、參考線掃掠、Rhino 轉族群。"
metadata:
  version: "1.10"
  updated: "2026-11-16"
  created: "2026-11-10"
  references:
    - "Revit API: FamilyItemFactory (NewExtrusion / NewBlend / NewSweep / NewReferencePlane / NewDimension / NewAlignment)"
    - "Revit API: FamilyManager (AddParameter / SetFormula / AssociateElementParameterToFamilyParameter)"
  related:
    - GM_rfa-family-injection.md
    - tool-capability-boundary.md
    - family-inventory-cleanup.md
  referenced_by: []
  tags: [族群, family, rfa, 族群編輯器, 參數化, extrusion, sweep, blend, reference-plane, reference-line]
---

# RFA 族群建模工作流程（rfa_* Family Authoring）

`rfa_*` 系列共 22 支工具，作用對象是 **Revit 目前作用中的族群文件（.rfa）**。用途是讓 AI 在族群編輯器裡建立參考平面、參數、類型與形體，並把形體綁到參數上，做出可撓曲（flex）的參數化族群。

本文件定義「怎麼用、照什麼順序用、哪些路線已確認走不通」。工具的完整參數以 `MCP-Server/src/tools/rfa-family-tools.ts` 為準。

---

## 1. 前置條件（Prerequisites）

| 條件 | 說明 |
|---|---|
| 作用中文件必須是族群檔 | 用 `rfa_new_family`（樣板新建）或 `rfa_open_family`（開啟既有 .rfa）取得，也可由使用者在 Revit 手動開啟。作用中文件是專案檔時，其餘 `rfa_*` 工具都會直接拒絕 |
| 載入專案需要專案已開啟 | `rfa_load_into_project` 只能載入到同一個 Revit 工作階段中**已開啟**的專案，工具不會替使用者開啟 .rvt。存檔可用 `rfa_save_as` |
| 材質要先存在於族群檔 | `material` 參數只能填族群檔內已有的材質名稱，工具不會從專案或材質庫帶入。缺的材質用 `rfa_create_material` 建立；現有材質可從 `rfa_get_document_info` 的 `materials` 查到 |
| 單位 | 長度 mm、角度 度、面積 m²、體積 m³、是/否 用 true/false、材質用名稱 |
| 實機驗證範圍 | 2026-11 只在 **Revit 2027** 實測通過；R22–R26 可編譯但未實測。`rfa_new_family`、`rfa_open_family`、`rfa_load_into_project`、`rfa_create_revolution`、`rfa_create_swept_blend`、`rfa_create_material`、`rfa_set_form_visibility`、`rfa_place_nested_family`、`rfa_modify_parameter`、`rfa_delete_parameter`（及 `rfa_add_parameter` 的共用參數）於 2026-11-16 新增，**尚未實機驗證** |

---

## 2. 工具總覽（Tool Map）

| 工具 | 作用 | 唯讀 |
|---|---|---|
| `rfa_new_family` | 以族群樣板（.rft）建立新族群、存檔並設為作用中文件 | |
| `rfa_open_family` | 開啟既有 .rfa 並設為作用中文件；已開啟則切換過去 | |
| `rfa_get_document_info` | 讀族群類別、放置型式、類型、參數現值、參考平面、樓層、視圖、既有形體（含子類別與可見性）、子類別、材質、巢狀族群 | ✅ |
| `rfa_set_family_type` | 建立或更新類型，設值／公式，可改名；被操作的類型會成為目前類型 | |
| `rfa_add_parameter` | 新增族群參數（9 種資料型別，可帶公式或初值）；`shared: true` 時從共用參數檔取定義 | |
| `rfa_modify_parameter` | 既有參數改名、實例／類型切換、換成共用參數 | |
| `rfa_delete_parameter` | 刪除參數（**破壞性**；預設 dryRun，仍被使用時拒絕） | |
| `rfa_create_material` | 在族群檔內建立材質（顏色、透明度、類別，或複製既有材質） | |
| `rfa_create_reference_plane` | 建立具名參考平面（簡式 normal + offsetMm，或進階式三向量） | |
| `rfa_create_dimension` | 在平行參考平面之間標尺寸（可掛長度參數、設 EQ）；`angular: true` 時標兩條參考線／平面的夾角並可掛角度參數 | |
| `rfa_create_extrusion` | 擠出（實體／空心），可多迴圈開孔 | |
| `rfa_create_blend` | 融合：底部一個迴圈、頂部一個迴圈 | |
| `rfa_create_revolution` | 旋轉：輪廓繞草圖平面上的軸線旋轉，可設起訖角度 | |
| `rfa_create_sweep` | 掃掠：沿平面路徑；或沿參考線＋直徑參數做參數化圓管 | |
| `rfa_create_swept_blend` | 掃掠融合：沿單一路徑，起點與終點各一個不同斷面 | |
| `rfa_create_reference_line` | 在參考平面上建立直線或三點圓弧參考線，並約束到其他參考平面 | |
| `rfa_set_form_visibility` | 把形體指派到子類別（可自動建立並設線色／線寬／材質），設定詳細等級與視圖方向的可見性 | |
| `rfa_place_nested_family` | 載入巢狀族群、放置實例、把巢狀參數關聯到主族群參數 | |
| `rfa_align_to_reference_plane` | 把形體上與參考平面共面的面鎖到該平面 | |
| `rfa_create_symbolic_line` | 在參考平面上建立符號線（直線或三點圓弧），可指定子類別線型，並約束到其他參考平面 | |
| `rfa_save_as` | 把作用中的族群文件另存為 .rfa | |
| `rfa_load_into_project` | 把作用中的族群載入已開啟的專案；已存在則重新載入 | |

同名的參考平面與參數會**跳過不重建**，所以重跑同一步是安全的。

---

## 3. 標準建立順序（Standard Sequence）

參數化族群的關係鏈是：**參數 → 尺寸標籤 → 參考平面 → 鎖定 → 形體**。任何一環沒接上，形體就不會跟著參數動。

```
0. rfa_new_family / rfa_open_family   沒有作用中的族群檔時，先建立或開啟
1. rfa_get_document_info         先讀現況，確認是族群檔、有沒有類型
2. rfa_set_family_type           樣板常常是 0 個類型 → 先建一個類型
3. rfa_create_material           形體或材質參數要用的材質，族群檔內沒有就先建
   rfa_add_parameter             建參數（長度、材質、可見性……）
4. rfa_create_reference_plane    建骨架平面（Left/Right/Front/Back/Top…）
5. rfa_create_dimension          兩平面＋label 綁參數；三平面＋equalize 設 EQ
6. rfa_create_extrusion / blend / revolution / sweep / swept_blend   形體畫在「剛好落在平面上」的位置
7. rfa_align_to_reference_plane  把形體的面鎖到平面
8. rfa_set_family_type           換一組值做撓曲測試，再換回來
9. rfa_save_as → rfa_load_into_project   存檔取得正式名稱，再載入已開啟的專案
```

### 步驟要點

- **第 2 步不可省略**：族群目前沒有任何類型時，`rfa_add_parameter` 的 `value` 不會寫入，回傳會帶 note 提醒。
- **公式驅動的參數不能直接給值**：有 `formula` 的參數，`rfa_set_family_type` 的 `values` 會被拒絕；要改值就先在 `formulas` 給空字串清掉公式。
- **第 5 步之前，平面位置要等於參數現值**：例如「寬度」= 600，Left/Right 就要先建在 ±300。尺寸綁參數後，平面會跟著參數移動。
- **第 6 步形體要畫在骨架上**：形體的面必須已經和參考平面共面，第 7 步才找得到那個面。
- **第 8 步撓曲測試**：至少換一組明顯不同的值（例如 600×400 → 800×500），確認形體跟著變，再換回原值。

---

## 4. 座標與平面約定（Coordinate Conventions）

### 4.1 參考平面（簡式）

| normal | 平面方向 | 自動放置的視圖 |
|---|---|---|
| `X` | 垂直，位於 x = offsetMm | 平面圖 |
| `Y` | 垂直，位於 y = offsetMm | 平面圖 |
| `Z` | 水平，位於 z = offsetMm | 前／後立面 |

### 4.2 草圖平面與擠出方向

| plane | 2D 座標 [u, v] | 擠出／融合方向 |
|---|---|---|
| `XY` | u = X, v = Y | +Z |
| `XZ` | u = X, v = Z | **−Y** |
| `YZ` | u = Y, v = Z | +X |
| `referencePlane` | 沿該平面自己的 X/Y 向量，原點為平面原點 | 平面法向量 |

`endOffsetMm` 給負值會往反方向擠出。

### 4.3 「XY/XZ/YZ」與「referencePlane」的差別

用 `plane: "XY"` 等方式建立的形體，工作平面**沒有關聯到任何參考平面**；參考平面移動時形體不會跟著走。要讓形體的草圖平面跟著參數移動，請改用 `referencePlane: "<平面名稱>"`。

### 4.4 輪廓（profiles）

每個迴圈只能給 `points`、`rectangle`、`circle` 其中一種：

- `points`：至少 3 點，自動封閉；任一段短於 0.5 mm 會被拒絕（請先合併重複點）。
- 第一個迴圈是外框，後續落在外框內的迴圈變成開孔（擠出、掃掠適用；融合底部與頂部各只能一個迴圈）。
- 從 Rhino 等外部模型轉入時，**曲線邊要離散成 points，只有完整圓可以直接用 circle**。

---

## 5. 形體與文件操作的用法

### 5.1 擠出（Extrusion）

最常用。`associate` 可綁：`extrusionEnd`、`extrusionStart`（長度參數）、`material`（材質參數）、`visible`（是/否參數）。

> 例：板厚由參數「厚度」控制 → `associate: {"extrusionEnd": "厚度"}`。

### 5.2 融合（Blend）

`baseProfile` → `topProfile`，相距 `heightMm`。`associate` 可綁 `blendTop`、`blendBase`、`material`、`visible`。

- Revit 2027 的 `NewBlend` 實際上把**第一個**輪廓引數當成頂部，與 API 文件寫法相反。工具會比對回傳的頂部／底部迴圈組成（曲線種類＋總長），順序反了就刪掉重建，並在 warnings 註明。
- 回傳的 TopProfile / BottomProfile 曲線組成就是自檢結果：方底圓頂應看到 bottom「4 Line」、top「2 Arc」。

### 5.3 掃掠（Sweep，固定路徑）

用於扶手、線腳、框料。路徑必須**在同一平面**上：

- `pathPoints`（多段線，可 `closed`）或 `pathSegments`（line / 三點 arc）。
- 斷面以 [u, v] mm 繪製，Revit 會把它放在路徑起點、垂直於路徑。
- 3D 空間曲線請拆成多段平面路徑，各自建一個掃掠。
- 斷面太大會在路徑轉角自我交錯而失敗，先縮小斷面或放大轉角半徑。

固定路徑的掃掠**不會跟著參數移動**。要參數化請用 5.4。

### 5.4 參數化圓管（Reference Line + Sweep）

這是參數化管件、桿件、曬衣架、欄杆的標準配方：**位置由參考線控制，管徑由斷面直徑標籤控制**。

```
1. rfa_add_parameter          建「管徑」與各位置參數（長度）
2. rfa_create_reference_plane 建好約束用的參考平面
3. rfa_create_reference_line  hostPlane = 參考線所在平面
                              constraints:
                                - 對齊鎖定：省略 distanceParameter（該部位必須已在平面上）
                                - 以參數標距：給 distanceParameter（目前距離必須等於參數值）
                              圓弧可加 radiusParameter
4. rfa_create_sweep           pathReferenceLineIds = 第 3 步回傳的 id（首尾相接、同一平面）
                              diameterParameter = "管徑"，profiles 可省略
5. 檢查回傳的 flexCheck       工具會暫時把管徑 +10 mm，確認斷面圓弧半徑等於參數一半再復原
```

約束 target 說明：

| target | 適用 | 說明 |
|---|---|---|
| `curve` | 直線 | 線本身平行於某參考平面時，鎖到該平面或與它標距 |
| `start` / `end` | 直線、圓弧 | 端點 |
| `center` | 圓弧 | 圓心 |

- 參考線會隨 `hostPlane` 沿其法向移動；平面內的兩個方向要靠 constraints 控制。
- 參考線若高於平面圖的剖切面，工具會自動提高該平面圖的視圖範圍，並在 warnings 註明。
- Revit 拒絕約束（多半是過度約束）時，整個交易會被復原，不會留下半成品。

### 5.5 符號線（Symbolic Line）

符號線是族群的 2D 表現，例如門的立面開門方向線、平面門弧線。它只在**與 `hostPlane` 平行的視圖**顯示。

- `hostPlane`：符號線的工作平面。門的立面開門方向線通常畫在 `Interior` 或 `Exterior` 平面上。
- `subcategory`：族群類別底下的子類別名稱，決定線型（例如門的 `Elevation Swing`）。名稱不存在時，錯誤訊息會列出可用的子類別。
- `constraints`：與 `rfa_create_reference_line` 相同（`curve` / `start` / `end` / `center`，對齊鎖定或以參數標距）。

**修改既有族群時**：既有符號線的端點約束無法經由 API 改掛到別的參考平面。要讓它跟著新的平面走，作法是先用 `get_element_geometry` 記下原線的端點與 `get_element_info` 的 Subcategory，用 `rfa_create_symbolic_line` 在新位置重畫並約束，確認無誤後再刪除舊線。

> 例：門上方加了氣窗後，立面開門方向線的頂點要從洞口頂改鎖到門扇頂——重畫四條線，頂點約束到「門扇頂」與中心平面，鉸鏈側端點約束到 Left / Right 與門扇半高平面。

### 5.6 另存族群（Save As）

`rfa_save_as` 把作用中的族群文件存成 .rfa。

- `filePath` 必須是完整路徑，資料夾必須已存在（工具不建立資料夾）。
- 檔案已存在時預設拒絕；確定要覆蓋才傳 `overwrite: true`。**覆蓋前先向使用者確認**。
- 存檔後，作用中的族群文件就指向新檔；原本的 .rfa 不會被改動。

### 5.7 開新族群與開啟族群（New / Open）

`rfa_new_family` 以樣板建立新族群，`rfa_open_family` 開啟既有 .rfa；兩者完成後該族群就是作用中文件。

- `rfa_new_family`：`templatePath`（.rft 完整路徑）與 `templateName`（樣板檔名，在 Revit 的族群樣板資料夾內遞迴搜尋）擇一。樣板名稱隨 Revit 語言版本不同；找不到時錯誤訊息會列出可用名稱，照著選即可，不要憑印象猜。
- `savePath` 必填，規則同 `rfa_save_as`（完整路徑、資料夾需已存在、既有檔案要 `overwrite: true`）。新族群一定會先存檔，因為 Revit 無法把只存在記憶體中的文件設為作用中文件。
- `rfa_open_family`：檔案已在 Revit 開啟時只做切換，回傳 `wasAlreadyOpen: true`。
- 兩支工具都會切換 Revit 的作用中視窗。原本作用中的專案檔不會被關閉，但之後依賴作用中文件的工具會作用在族群檔上，要回專案需由使用者切換視窗。
- 若 Revit 拒絕切換文件，錯誤訊息會說明檔案是否已寫入磁碟；此時請使用者在 Revit 手動開啟該路徑。

### 5.8 載入專案（Load into Project）

`rfa_load_into_project` 把作用中的族群載入同一個 Revit 工作階段中已開啟的專案。

- **先存檔再載入**：專案看到的族群名稱就是族群文件的標題。從未存檔的族群會以「Family1」這類暫時名稱進入專案，所以先用 `rfa_save_as` 取好檔名。
- `targetProject`：只開一個專案時可省略；開了多個時必須指定（標題或完整路徑），工具不會替你挑。
- 專案內已有同名族群時一律重新載入。`overwriteParameterValues` 預設 false，保留專案中已調整的類型參數值；**設為 true 會覆蓋專案中的值，執行前先向使用者確認**。
- 預設不切換視窗，族群編輯器仍是作用中文件，可以繼續修改後再載入一次。`activateProject: true` 會在載入後切到專案；切換失敗只會出現在 warnings，族群仍已載入。
- 載入後回傳專案內的族群 id 與各類型 id，後續放置實例請用這些 id，不要沿用族群檔內的 id。

### 5.9 旋轉（Revolution）

輪廓繞一條軸線旋轉，適合圓盤、把手、燈罩、瓶狀構件。

- `axis`：`{ start: [u, v], end: [u, v] }`，與輪廓使用**同一套草圖平面 2D 座標**（mm）。例：在 `plane: "XZ"` 上 `start [0,0]`、`end [0,1000]` 就是 Z 軸。
- **輪廓不可跨越軸線**，但可以貼在軸上（貼軸 = 中心沒有孔的實心旋轉體）。工具會在送進 Revit 前先檢查，錯誤訊息會指出是哪個迴圈、超出幾 mm。
- `startAngleDeg` / `endAngleDeg` 預設 0 / 360，跨度需介於 0.01° 與 360° 之間。
- `associate` 可綁 `startAngle`、`endAngle`（角度參數）、`material`、`visible`。
- 回傳 `boundingBoxMm`，用來比對形體是否落在預期位置。
- **尚未實機驗證的項目**：部分旋轉（非 360°）的起始方向與旋轉方向。做部分旋轉時，務必用回傳的 `boundingBoxMm` 確認方向，不對就對調軸線的 start / end 或調整角度，不要假設。

### 5.10 掃掠融合（Swept Blend）

沿一條路徑，從起點斷面漸變到終點斷面，適合變斷面構件（漸縮管、錐形桿、變斷面梁托）。

- 路徑只能是**單一曲線**：`path`（一段直線或一段三點圓弧，3D mm）或 `pathReferenceLineId`（一條參考線，形體會跟著參考線動）。多段路徑請拆成多個掃掠融合。
- `startProfile`、`endProfile` 各一個封閉迴圈，座標約定同掃掠斷面（[u, v] mm，Revit 會放到路徑端點並垂直於路徑）。
- `associate` 可綁 `material`、`visible`。
- **建立後務必核對回傳值**：`startProfileCurves` / `endProfileCurves` 是 Revit 實際放在兩端的斷面組成（例如「4 Line」「2 Arc」），`profilesSwapped` 表示工具是否對調重建過，`boundingBoxMm` 可確認哪一端較寬。
- **尚未實機驗證的項目**：工具用幾何位置判斷哪個斷面落在路徑起點，這個判斷假設 Revit 讀回的斷面是模型座標。若 warnings 出現「無法判斷兩端」，以 `boundingBoxMm` 為準人工確認；兩端相反時，把 `startProfile` 與 `endProfile` 對調重建。

### 5.11 材質（Material）

`rfa_create_material` 在族群檔內建立材質，讓形體的 `material` 與材質型別的族群參數有東西可以引用。

- 先用 `rfa_get_document_info` 的 `materials` 看族群檔內已有哪些，沒有才建。
- 每個材質可設 `color`（[r, g, b]，0–255，著色顏色）、`transparency`（0–100）、`materialClass`。
- `duplicateFrom`：複製族群檔內既有材質再套用上述設定。
- 同名材質預設跳過（名稱比對不分大小寫）；`updateExisting: true` 才會修改既有材質，**修改前先向使用者確認**。
- 這支工具只設定「圖形」頁籤的著色顏色與透明度，**不建立外觀資產（彩現貼圖）**。需要彩現外觀時，由使用者在 Revit 材質瀏覽器設定，或在專案端以綠建材／材質工具處理。
- 載入專案時，專案內若已有同名材質，Revit 會沿用專案的材質定義；族群檔內設定的顏色不一定會出現在專案中。

### 5.12 子類別與可見性（Subcategory / Visibility）

`rfa_set_form_visibility` 處理兩件事：形體屬於哪個子類別，以及形體在哪些詳細等級與視圖方向顯示。

- `subcategory`：族群類別底下的子類別名稱。不存在時預設自動建立；傳空字串則清除指派，回到族群類別本身。`elementIds` 可以是空陣列，只用來建立並設定子類別。
- `subcategoryStyle`：`lineColor`、`projectionLineWeight`、`cutLineWeight`（1–16）、`material`。**套用在既有子類別上會影響該子類別的所有元素**，回傳的 `styleApplied.modifiedExisting` 與 warnings 會標示。
- `visibility`：`coarse` / `medium` / `fine`（詳細等級）與 `planRcp` / `frontBack` / `leftRight` / `whenCutInPlanRcp`（視圖方向）。只給要改的欄位，其餘維持原值。
- 常見用法：細部形體設 `coarse: false`，讓粗略等級只顯示簡化量體；五金、把手放到獨立子類別，專案端才能用可見性／圖形單獨控制。
- 每個形體各自處理，一個失敗不影響其他；失敗原因列在 `errors`。讀回值與要求不同時會出現在 warnings（例如不可切割的類別設定 `whenCutInPlanRcp`）。
- 與「可見」參數的差別：`associate.visible` 是讓某個類型或實例**整個不產生**該形體；這裡設定的是形體存在、但在特定等級或視圖方向不畫出來。

### 5.13 巢狀族群（Nested Family）

`rfa_place_nested_family` 一次處理「載入 → 放置 → 關聯參數」。門五金、設備附件、家具配件都靠這個。

- 來源擇一：`familyPath`（載入一個 .rfa）或 `familyName`（族群檔內已載入的族群，可從 `rfa_get_document_info` 的 `nestedFamilies` 查）。
- `typeName` 省略時取名稱排序第一個類型；回傳的 `availableTypes` 列出全部類型。
- `instances`：每個給 `locationMm` 與可選的 `rotationDeg`（繞通過該點的垂直軸）。可以省略，只做載入或只補關聯。
- `associate`：`{ 巢狀參數名: 主族群參數名 }`。先找實例參數，再找類型參數；特殊鍵 `visible` 代表實例的「可見」。回傳的 `nestedParameters` 列出巢狀族群可關聯的參數名稱，**先看這份清單再填，不要猜參數名**。
- **型別與層級要對得上**：兩邊資料型別需相同；巢狀族群的**類型參數只能關聯到主族群的類型參數**，關聯到實例參數會被略過並出現在 warnings。
- 支援的放置型式：樓層式、工作平面式（非面式）、2D 視圖式。需要主體或面的型式（牆上、面上、線式、兩樓層式、自適應）會逐筆回報不支援，請使用者在 Revit 手動放置後，再用 `applyToExisting: true` 補上參數關聯。
- **已知限制**：實例放在固定座標，這支工具不會把它約束到參考平面。主族群撓曲時實例不會跟著移動，除非巢狀族群本身的尺寸由關聯參數驅動。位置需要跟著參數動的情況，目前要由使用者在 Revit 手動對齊鎖定。
- `reload: true` 會以檔案重新載入已存在的巢狀族群；`overwriteParameterValues: true` 會覆蓋其類型參數值，**執行前先向使用者確認**。

### 5.14 參數維護（Shared / Modify / Delete）

**共用參數**：`rfa_add_parameter` 的項目加上 `shared: true`，定義就從共用參數檔**依名稱**取得（`sharedParameterFile` 指定 .txt，省略則用 Revit 目前設定的檔案）。

- 資料型別由定義決定，`type` 會被忽略。
- 工具**只讀**共用參數檔，不會新增或修改定義。名稱找不到時，錯誤訊息會列出搜尋過的群組與可用名稱。
- 同名但 GUID 不同的定義需以 `sharedGuid` 指定。
- 明細表或標籤要讀得到的參數（例如綠建材、機電容量）必須是共用參數；純粹驅動幾何的尺寸用一般族群參數即可。

**修改**：`rfa_modify_parameter` 可改名、切換實例／類型、把一般參數換成共用參數。

- 內建參數與共用參數不能改名。
- 改名後檢查回傳的 `formulasNowReferencing`，確認引用它的公式已跟著更新。
- 同一個項目不能同時 `renameTo` 與 `replaceWithShared`（共用參數的名稱由定義決定）。

**刪除**：`rfa_delete_parameter` 是破壞性工具，流程固定為兩步。

```
1. dryRun: true（預設）   讀使用報告：被哪些公式引用、綁到哪些形體參數、掛在哪些尺寸標籤上
2. 向使用者確認後 dryRun: false   只刪沒有被使用的參數
```

- 仍被使用的參數會列在 `refused`。**不要為了讓刪除通過就直接加 `force`**：`force` 會連帶讓引用它的公式失效、尺寸標籤脫落、形體不再跟著動。要用 `force` 必須先讓使用者看過使用報告並明確同意。
- 一次刪多個參數時，只被「同一批其他參數」引用的參數會在後續輪次一併刪除，所以實際刪除的數量可能多於 dry run 顯示的 `wouldDelete`。
- 公式引用是用名稱比對判斷的。參數名稱若與函數或單位同名（`if`、`sin`、`pi`、`mm`），會被誤判為「仍在使用」；此時由使用者判斷。

---

## 6. 對齊與尺寸（Constraints）

### 6.1 `rfa_create_dimension`

- 參考平面必須互相平行；順序不重要，工具會沿法向排序。
- `label`：只能用在**剛好兩個**平面，且參數必須是長度型別。
- `equalize`：需要**三個以上**平面。
- 視圖自動挑選：垂直平面用平面圖，水平平面用立面；也可用 `viewName` 指定。

常見骨架：Left / 中心 / Right 三平面設 EQ，Left / Right 再標一道「寬度」。這樣改寬度時形體會對稱縮放。

### 6.2 `rfa_align_to_reference_plane`

- 對每個參考平面，找出形體上**已經與它共面**的平面面，建立鎖定對齊。
- 面沒有落在平面上就找不到，不會自動移動形體。

### 6.3 角度標註（`rfa_create_dimension` 的 `angular: true`）

2026-11-16 新增，**尚未實機驗證**。

- 剛好兩個參考：`referencePlanes`（不平行的兩個平面）與 `referenceLineIds`（直線參考線）任意組合，順序是先平面、後參考線。
- `label` 必須是**角度**參數，且目前夾角要等於參數現值，否則工具會回報兩個數值並拒絕。
- 量的是第一個參考方向到第二個參考方向的夾角（0–180°）：參考線方向為起點→終點。拿到的是補角（例如要 45° 卻得到 135°）時，加 `supplementary: true` 重做。
- 回傳的 `angleDeg` 是從尺寸讀回的值，`flexCheck` 是暫時把參數 +5° 後重新量測的結果。`flexCheck` 不是 OK 時，通常是會轉動的那條參考線沒有固定在頂點上——先用 `rfa_create_reference_line` 的 constraints 把端點鎖在頂點。
- `equalize`、`throughPointMm` 不能與 `angular` 併用。

---

## 7. 確認行不通的路線（Dead Ends）

以下是 2026-11 實測確認走不通的作法，**不要再嘗試**：

| 作法 | 結果 | 改用 |
|---|---|---|
| 用 `SketchEditScope` 編輯族群內的草圖 | 拋出「document is not a project document」，此 API 只能在專案檔使用 | 參考線＋掃掠（5.4） |
| 在草圖模式外，對草圖曲線（例如圓心）建立對齊 | 「Failed to create the alignment」 | 參考線＋掃掠（5.4） |
| 在草圖模式外，對草圖圓弧的參考標尺寸並掛參數 | Revit 接受，但約束錯誤：圓跳到平面上、管徑變成兩倍，還改寫了其他參數值 | 位置走參考線，尺寸只用斷面直徑標籤 |

結論：族群文件沒有可用的草圖編輯 API。**唯一可靠的草圖約束是斷面圓弧上的直徑標籤**；位置類約束一律走參考平面或參考線。

---

## 8. 常見錯誤訊息對照（Troubleshooting）

| 訊息重點 | 原因 | 處理 |
|---|---|---|
| 作用中的文件是專案檔 | 目前視窗不是 .rfa | 請使用者切換到族群編輯器視窗 |
| 族群目前沒有任何類型，未寫入 value | 樣板沒有類型 | 先 `rfa_set_family_type` 建類型，再補值 |
| 找不到材質 | 材質不在族群檔中 | 先用 `rfa_create_material` 建立；或省略 material |
| 綁定失敗（兩邊型別需相同） | 例如把長度屬性綁到數字參數 | 參數型別改成相符的型別 |
| 建立擠出失敗 | 輪廓不封閉、自我交錯，或樣板不支援（概念量體／自適應族群） | 檢查輪廓；換一般族群樣板 |
| 輪廓跨越旋轉軸 | `rfa_create_revolution` 的輪廓有一部分在軸線另一側 | 把輪廓整個移到軸線同一側（可貼軸） |
| 掃掠路徑必須位於同一個平面上 | 3D 路徑 | 拆成多個平面掃掠 |
| 距離是 X mm，但參數是 Y mm | 約束前幾何位置與參數現值不一致 | 先把參數值設成目前距離，或把幾何畫到正確位置 |
| 交易被復原，可能是過度約束 | 同一方向被約束兩次 | 拿掉重複的約束 |
| 族群類別沒有子類別「X」 | `subcategory` 名稱不在此族群類別下 | 改用錯誤訊息列出的名稱，或省略；形體可用 `rfa_set_form_visibility` 自動建立子類別 |
| 找不到名為「X」的族群樣板 | `templateName` 與實際檔名不符（語言版本不同） | 從錯誤訊息列出的可用樣板中選，或改傳 `templatePath` |
| OpenAndActivateDocument 失敗 | Revit 不允許在目前狀態切換文件 | 請使用者在 Revit 手動開啟訊息中的路徑 |
| Revit 目前開啟了 N 個專案 | `rfa_load_into_project` 未指定目標 | 以 `targetProject` 指定錯誤訊息列出的其中一個 |
| 沒有開啟任何專案可以載入族群 | 目標 .rvt 未開啟 | 請使用者先在 Revit 開啟專案 |
| 共用參數檔裡找不到「X」 | `shared: true` 的名稱不在共用參數檔中 | 從錯誤訊息列出的名稱中選，或指定正確的 `sharedParameterFile`；工具不會替你新增定義 |
| 參數仍被使用，已拒絕刪除 | `rfa_delete_parameter` 發現公式、形體或尺寸仍引用它 | 先解除引用；確定要連帶破壞時，經使用者同意後才用 `force` |
| 檔案已存在 | `rfa_save_as`、`rfa_new_family` 預設不覆蓋 | 換檔名，或經使用者確認後傳 `overwrite: true` |

---

## 9. 回報要求（Reporting）

依 `CLAUDE.md` Tool Call Data Honesty：回報給使用者的元素 ID、參數值、平面名稱、類型名稱，都必須來自本輪工具回傳。完成後至少回報：

1. 建立了哪些參數、參考平面、形體（附 id）。
2. 哪些形體綁了哪些參數。
3. 撓曲測試用了哪兩組值、結果是否跟著變。
4. 所有 warnings（不要省略）。
