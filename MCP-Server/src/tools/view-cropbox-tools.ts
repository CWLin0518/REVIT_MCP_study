import { Tool } from "@modelcontextprotocol/sdk/types.js";

export const viewCropBoxTools: Tool[] = [
    {
        name: "align_view_cropbox_to_element",
        description: "將指定視圖的 CropBox 對齊到目標元素（如 Detail Group 圖框）的 BoundingBox。保留原 Z 軸深度與 CropBox Transform。viewId 不指定時使用 active view。",
        inputSchema: {
            type: "object",
            properties: {
                elementId: { type: "number", description: "要對齊的目標元素 ID（例如 Detail Group 的 ID）" },
                viewId: { type: "number", description: "目標視圖 ID（選填）；不填則使用當前 active view" },
                padding_mm: { type: "number", description: "向外擴大的邊距（公釐），預設 0", default: 0 },
            },
            required: ["elementId"],
        },
    },
    {
        name: "shift_view_cropbox",
        description: "在 CropBox 自身座標系中平移視圖的 CropBox。dx 正值往右、dy 正值往上（單位公釐）。viewId 不指定時使用 active view。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "目標視圖 ID（選填）；不填則使用當前 active view" },
                dx_mm: { type: "number", description: "X 方向位移（公釐，正值往右）", default: 0 },
                dy_mm: { type: "number", description: "Y 方向位移（公釐，正值往上）", default: 0 },
            },
        },
    },
    {
        name: "export_view_image",
        description: "Export a plan view or region to PNG for remote preview. Supports floor, ceiling, structural and area plans. Does not change the model: a temporary dependent duplicate is cropped to the region + margin (annotation crop off, crop frame hidden), exported with ImageExportOptions, and the whole TransactionGroup is rolled back; only the PNG in outputDir remains. " +
            "Region (at most one): grids {x:[a,b], y:[c,d]} = rectangle bounded by those grid lines (vertical/horizontal decided from geometry; swapped with a warning if given the other way), roomNumber / roomId, areaId, or bboxMm {minX,minY,maxX,maxY} in project internal coordinates (mm). Omitted = the view's current crop (marginMm then defaults to 0). " +
            "Response (lengths in mm): imagePath, pixelWidth, pixelHeight, view {id,name,levelName,scale}, crop {minX,minY,maxX,maxY} = the extent the image covers, modelCrop = crop box actually set, viewRight / viewUp, mapping = 'px = (x - crop.minX) / (crop.maxX - crop.minX) * pixelWidth; py = (crop.maxY - y) / (crop.maxY - crop.minY) * pixelHeight', mappingCheck {status: outline_matches_crop | outline_extends_crop | aspect_mismatch | unverified}, region {type,label,outline}, origin {x,y,label} (grids: top-left grid intersection in image space, e.g. 'C/4'; otherwise top-left of the region bbox), grids [{name,start,end}] clipped to crop, warnings. " +
            "For an unrotated view these are project internal XY. If the view is rotated, crop/outline/origin/grids are in the view-aligned frame x' = X*viewRight.x + Y*viewRight.y, y' = X*viewUp.x + Y*viewUp.y (a warning says so) and cropWorldCorners gives the extent in internal XY.",
        inputSchema: {
            type: "object",
            properties: {
                viewName: { type: "string", description: "Plan view name (or use viewId). If several plan views share the name, the floor plan is preferred and a warning lists the ids." },
                viewId: { type: "number", description: "Plan view ElementId (or use viewName)." },
                region: {
                    type: "object",
                    description: "Optional; give at most one key. Omit to export the view's current crop.",
                    properties: {
                        grids: {
                            type: "object",
                            description: "Rectangle bounded by two pairs of grid lines, e.g. { x: [\"C\", \"E\"], y: [\"4\", \"6\"] }.",
                            properties: {
                                x: { type: "array", items: { type: "string" }, minItems: 2, maxItems: 2 },
                                y: { type: "array", items: { type: "string" }, minItems: 2, maxItems: 2 },
                            },
                            required: ["x", "y"],
                        },
                        roomNumber: { type: "string", description: "Room number; the room on the view's level is preferred." },
                        roomId: { type: "number", description: "Room ElementId." },
                        areaId: { type: "number", description: "Area ElementId." },
                        bboxMm: {
                            type: "object",
                            description: "Rectangle in project internal coordinates, mm.",
                            properties: {
                                minX: { type: "number" }, minY: { type: "number" },
                                maxX: { type: "number" }, maxY: { type: "number" },
                            },
                            required: ["minX", "minY", "maxX", "maxY"],
                        },
                    },
                },
                marginMm: { type: "number", default: 1000, description: "Margin added around the region (mm). Default 1000; 0 when no region is given." },
                pixelWidth: { type: "number", default: 2000, description: "Image width in pixels (200-15000); height follows the crop aspect." },
                outputDir: { type: "string", description: "Absolute output folder; created if missing. File name: <sanitized view name>_<yyyyMMdd_HHmmss>.png" },
            },
            required: ["outputDir"],
        },
    },
];
