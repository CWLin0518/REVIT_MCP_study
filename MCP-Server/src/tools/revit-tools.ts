/**
 * Revit MCP 工具定義 — 相容層
 *
 * 此檔案保留為相容層，實際工具定義已拆分到模組檔案：
 *   base-tools.ts, wall-tools.ts, room-tools.ts,
 *   visualization-tools.ts, schedule-tools.ts, mep-tools.ts
 *
 * 匯總與 Profile 篩選邏輯在 index.ts
 */

import { RevitSocketClient } from "../socket.js";
import { Tool } from "@modelcontextprotocol/sdk/types.js";
import { registerRevitTools as registerProfileTools } from "./index.js";

/**
 * 依 Profile 註冊工具（實際 Profile 篩選邏輯在 index.ts）
 */
export function registerRevitTools(): Tool[] {
    return registerProfileTools();
}

/**
 * 執行 Revit 工具
 */
export async function executeRevitTool(
    toolName: string,
    args: Record<string, any>,
    client: RevitSocketClient
): Promise<any> {
    // 將工具名稱轉換為 Revit 命令名稱
    // 如果是 query_elements_with_filter，映射到 C# 的 query_elements
    const commandName = toolName === "query_elements_with_filter" ? "query_elements" : toolName;

    // 跨文件命令需要較長 timeout（開啟 .rvt/.rfa 檔案較費時）
    const CROSS_DOC_COMMANDS = new Set(["read_source_file_sheets", "copy_sheets_from_file", "sync_sheet_parameters_from_source", "inject_green_material_into_family", "rfa_new_family", "rfa_open_family", "rfa_load_into_project", "rfa_place_nested_family", "load_family_from_path"]);
    // 大批次單一交易：逾時後 Revit 仍在執行且結果會遺失，需給足時間
    const LONG_TRANSACTION_COMMANDS = new Set(["set_wall_location_lines"]);
    // 影像匯出：大視圖或高解析度時 ExportImage 可能超過 30 秒
    const IMAGE_EXPORT_COMMANDS = new Set(["export_view_image"]);
    const timeoutMs = LONG_TRANSACTION_COMMANDS.has(commandName)
        ? 600000
        : CROSS_DOC_COMMANDS.has(commandName) || IMAGE_EXPORT_COMMANDS.has(commandName) ? 120000 : 30000;

    // 發送命令到 Revit
    const response = await client.sendCommand(commandName, args, timeoutMs);

    if (!response.success) {
        throw new Error(response.error || "命令執行失敗");
    }

    return response.data;
}
