import { Tool } from "@modelcontextprotocol/sdk/types.js";

export const elevationExtentTools: Tool[] = [{
    name: "sync_elevation_view_extents",
    description: "Synchronize elevation crop extents from source views to target views. Width and depth follow the source crop/far clip. Vertical limits are translated using the source crop bottom/top offsets relative to the source plan level and the target plan level elevation. Use dryRun=true first.",
    inputSchema: {
        type: "object",
        properties: {
            sourcePlanViewId: { type: "number", description: "Level-based source floor plan used as the datum for bottom/top crop offsets." },
            mappings: {
                type: "array",
                items: {
                    type: "object",
                    properties: {
                        sourceViewId: { type: "number" },
                        targetViewId: { type: "number" },
                        targetPlanViewId: { type: "number", description: "Level-based target floor plan providing the target datum elevation." }
                    },
                    required: ["sourceViewId", "targetViewId", "targetPlanViewId"]
                }
            },
            dryRun: { type: "boolean", default: true }
        },
        required: ["sourcePlanViewId", "mappings"]
    }
}];
