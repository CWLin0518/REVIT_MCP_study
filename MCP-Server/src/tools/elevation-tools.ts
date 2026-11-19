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
    },
    {
        name: "create_room_interior_elevations",
        description: "Place one 4-way ElevationMarker at a room's location point, create the four interior elevation views (named namePrefix+1..4) of the given elevation ViewFamilyType (default 'Interior Elevation'), and crop each view to the room's finish boundary and height. Use dryRun=true first; fails if any target view name already exists.",
        inputSchema: {
            type: "object",
            properties: {
                roomId: { type: "number", description: "Room ElementId (or use roomNumber)." },
                roomNumber: { type: "string", description: "Room number, e.g. A-107 (or use roomId)." },
                planViewId: { type: "number", description: "Floor plan on the room's level to host the marker. Default: active view if it matches, else first floor plan on that level." },
                viewFamilyTypeName: { type: "string", default: "Interior Elevation", description: "Elevation ViewFamilyType name." },
                scale: { type: "number", default: 100 },
                namePrefix: { type: "string", description: "View name prefix; default room number + room name. Views are named prefix1..prefix4." },
                viewTemplateId: { type: "number", description: "View template ElementId to apply." },
                copyTemplateFromViewId: { type: "number", description: "Copy the view template from this existing view (used if viewTemplateId is not given)." },
                fitToHosts: { type: "boolean", default: true, description: "Extend crop/far clip by the thickest bounding wall, down to the bottom of the slab below, and up to the top of the slab above, so wall and slab thickness are visible." },
                offsetMm: { type: "number", default: 0, description: "Extra margin outside the computed extents for crop and far clip. Default 0 (crop hugs wall/slab thickness)." },
                dryRun: { type: "boolean", default: true }
            }
        }
    }
];
