import { Tool } from "@modelcontextprotocol/sdk/types.js";

export const elevationTools: Tool[] = [
    {
        name: "clone_elevation_views_to_plans",
        description: "Create new native ElevationMarker/Elevation views on target floor plans using the XY position, direction, elevation view type, scale, and optionally view template of source elevation views. Use dryRun=true first. Existing view names are skipped.",
        inputSchema: {
            type: "object",
            properties: {
                sourceViewIds: {
                    type: "array",
                    items: { type: "number" },
                    description: "Source elevation ViewSection ElementIds."
                },
                sourcePlanViewId: {
                    type: "number",
                    description: "Floor-plan View ElementId that displays the source elevation markers; used to read their exact marker geometry origins."
                },
                targets: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            planViewId: { type: "number", description: "Target floor-plan View ElementId." },
                            sourcePrefix: { type: "string", description: "Leading source-floor text to replace; default 1F." },
                            targetPrefix: { type: "string", description: "Leading target-floor text used in new view names, e.g. 2F." }
                        },
                        required: ["planViewId", "targetPrefix"]
                    }
                },
                copyViewTemplate: { type: "boolean", default: true },
                dryRun: { type: "boolean", default: true }
            },
            required: ["sourceViewIds", "sourcePlanViewId", "targets"]
        }
    }
];
