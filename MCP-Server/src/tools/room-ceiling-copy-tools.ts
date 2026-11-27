import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * Room-driven copy of native ceilings and the fixtures hosted on them (default: lighting fixtures)
 * from a source room to congruent rooms on other levels. C# side:
 * MCP/Core/Commands/CommandExecutor.RoomCeilingCopy.cs
 *
 * redraw_room_ceilings_with_lighting rebuilds the same content from extracted source data
 * (Ceiling.Create + NewFamilyInstance; no element copying). C# side:
 * MCP/Core/Commands/CommandExecutor.RoomCeilingRedraw.cs
 */
export const roomCeilingCopyTools: Tool[] = [
    {
        name: "copy_room_ceilings_with_lighting",
        description:
            "Copy native ceilings that lie inside a source Room, together with the fixtures hosted on those ceilings " +
            "(default OST_LightingFixtures), to target Rooms on other levels, e.g. A-206 -> A-306, A-406. " +
            "Translation per pair = target minus source finish-boundary centre in XY, and target minus source level elevation in Z; " +
            "the two room boundaries must be congruent under that translation (shapeToleranceMm) or the pair is refused. " +
            "Room numbers are resolved deterministically (placed rooms only; phaseName filter; main-model rooms unless allowDesignOptionRooms; " +
            "roomIdOverrides for explicit ids) and any unknown/ambiguous number aborts the call before anything changes. " +
            "Level-exact discovery: a ceiling belongs to a room only if its Level is the room's Level, a fixture only via its host ceiling's Level " +
            "(own level only when not hosted on a ceiling); verticalToleranceMm widens the search band but never admits elements of another level. " +
            "Only ceilings fully inside the source room (boundaryToleranceMm, room vertical extent) are copied; a ceiling that straddles the " +
            "boundary refuses that mapping. Each ceiling is copied with its hosted fixtures in one ElementTransformUtils.CopyElements call so " +
            "the copies stay hosted on the copied ceiling; the copied ceiling is re-levelled to the target room level with an offset that keeps " +
            "the world height, then host, position, level and containment are verified before commit. Requested-category fixtures in the room that are " +
            "not hosted on a copied ceiling are reported, never copied. Duplicate prevention: a target room that already contains level-exact ceilings or " +
            "requested fixtures present in its phase (New/Existing/Temporary; Demolished/Past/Future are only reported as ignoredInTarget) is skipped " +
            "(existingPolicy='skip', default) or failed ('fail'). DESTRUCTIVE opt-in existingPolicy='replace' (only when the caller explicitly asks): deletes " +
            "ONLY target ceilings fully contained in the target room on its level (phase New, not grouped, not in a design option, not pinned) and the " +
            "requested-category fixtures hosted on them; every dependent is inspected first, and a straddling/shared ceiling, any unrequested hosted or " +
            "dependent element (tags, other categories...) or a requested fixture the plan cannot remove refuses that target. replacePlan lists exactly what " +
            "would be deleted; deletion and copy share one transaction, Revit's actual deletion set must match the inspected scope, and afterwards the " +
            "target must hold only the new copies. dryRun=true (default) previews and, with verifyInDryRun, performs the trial delete+copy+verification " +
            "that is always rolled back. atomic=true (default) rolls the whole run (deletions and copies) back if any target fails. " +
            "Execution requires the Main Model to be the active design option.",
        inputSchema: {
            type: "object",
            properties: {
                mappings: {
                    type: "array",
                    minItems: 1,
                    maxItems: 50,
                    description: "Source -> targets by room number. A room may appear only once as a source and once as a target, and never as both.",
                    items: {
                        type: "object",
                        properties: {
                            sourceRoomNumber: { type: "string", description: "Source room number, e.g. A-206." },
                            targetRoomNumbers: {
                                type: "array",
                                minItems: 1,
                                items: { type: "string" },
                                description: "Target room numbers on other levels, e.g. [\"A-306\", \"A-406\"]."
                            }
                        },
                        required: ["sourceRoomNumber", "targetRoomNumbers"]
                    }
                },
                roomIdOverrides: {
                    type: "object",
                    additionalProperties: { type: "number" },
                    description: "Optional { roomNumber: roomElementId } to pin a specific room when a number is ambiguous. The room's Number must match the key."
                },
                phaseName: { type: "string", description: "Only consider rooms in this phase (exact phase name). Without it, a number found in several phases is refused as ambiguous." },
                allowDesignOptionRooms: { type: "boolean", default: false, description: "Also consider rooms that belong to a design option. Elements in design options are never copied." },
                hostedCategories: {
                    type: "array",
                    items: { type: "string" },
                    description: "BuiltInCategory names of ceiling-hosted families to copy with their ceiling. Default [\"OST_LightingFixtures\"]."
                },
                existingPolicy: {
                    type: "string",
                    enum: ["skip", "fail", "replace"],
                    default: "skip",
                    description: "When a target room already contains ceilings or requested fixtures: 'skip' leaves that target untouched (reported as skipped_existing), 'fail' counts it as a failure (with atomic=true nothing is copied), 'replace' (destructive; use only on explicit user request, preview with dryRun first) deletes the inspected, fully-contained target ceilings and their requested-category hosted fixtures, then copies; any element that cannot be safely scoped refuses that target."
                },
                atomic: { type: "boolean", default: true, description: "true: any failed target rolls back the whole run. false: successful targets are kept, failed ones are rolled back individually." },
                dryRun: { type: "boolean", default: true, description: "true: report the plan without changing the model." },
                verifyInDryRun: { type: "boolean", default: true, description: "With dryRun, perform the real copy and verification inside a transaction group that is always rolled back." },
                boundaryToleranceMm: { type: "number", default: 150, description: "Plan tolerance for ceilings/fixtures relative to the room finish boundary (ceilings drawn into the wall still count as inside)." },
                verticalToleranceMm: { type: "number", default: 50, description: "Tolerance on the room's vertical extent (Base Offset..Upper Limit) when collecting ceilings and fixtures. May reach above the Upper Limit, but only elements on the room's own level are ever considered." },
                shapeToleranceMm: { type: "number", default: 50, description: "Maximum boundary deviation between translated source room and target room." },
                positionToleranceMm: { type: "number", default: 2, description: "Verification tolerance for copied element positions." }
            },
            required: ["mappings"]
        }
    },
    {
        name: "redraw_room_ceilings_with_lighting",
        title: "Redraw room ceilings and lighting from a source room (no copying)",
        description:
            "Redraw (rebuild, never copy) native ceilings and their ceiling-hosted fixtures (default OST_LightingFixtures) in target Rooms from data extracted from a source Room. " +
            "No ElementTransformUtils.CopyElements, copy/paste or duplication is used: each ceiling is a new Ceiling.Create(profile loops, source CeilingType, target level) " +
            "and each fixture a new NewFamilyInstance against the NEW target ceiling (face reference for face/work-plane-based families, host element for ceiling-based OneLevelBasedHosted ones). " +
            "Source extraction is level- and phase-exact and only takes ceilings fully inside the source room; profiles come from the native ceiling sketch (all loops incl. holes, bound lines and arcs only). " +
            "Translation is the room finish-boundary centre difference in XY; the target room level replaces the source level and Height Offset From Level, Phase Created and safe writable instance parameters (Mark and IFC GUIDs excluded) are reproduced. " +
            "Fixtures reproduce symbol, host face side, position, rotation and hand/facing flips. Refused instead of approximated: straddling/sloped/shape-edited/free-form ceilings, extra sketch elements (slope arrows), " +
            "unsupported curves or placement types, non-point locations, nested components, grouped/design-option/pinned/other-user-owned or demolished elements, MEP-connected fixtures, " +
            "any unrequested hosted or dependent element (openings, tags, other categories), requested fixtures hosted on a source ceiling but outside the source room, and (unless ignoreNonCeilingHostedFixtures) " +
            "requested fixtures in the room that are not on a redrawable ceiling. Every new element is verified (type/symbol, exact level, loop/hole counts, curve-by-curve profile, height, bounding box, area, containment, " +
            "host = new ceiling and host face, position, full orientation/flip/mirror state, phase, parameters) before commit. Never deletes: a target that already holds level-exact current-phase ceilings or requested fixtures " +
            "fails (existingPolicy='fail', default) or is skipped ('skip'). dryRun=true (default) with verifyInDryRun performs the full create+verify inside a transaction group that is always rolled back (trial ids are not reported); " +
            "atomic=true (default) commits only if every target passes; created ids are reported only after commit. Requires Main Model as the active design option.",
        inputSchema: {
            type: "object",
            properties: {
                mappings: {
                    type: "array",
                    minItems: 1,
                    maxItems: 50,
                    description: "Source -> targets by room number. A room may appear only once as a source and once as a target, and never as both.",
                    items: {
                        type: "object",
                        properties: {
                            sourceRoomNumber: { type: "string", description: "Source room number." },
                            targetRoomNumbers: { type: "array", minItems: 1, items: { type: "string" }, description: "Target room numbers (congruent rooms)." }
                        },
                        required: ["sourceRoomNumber", "targetRoomNumbers"]
                    }
                },
                roomIdOverrides: {
                    type: "object",
                    additionalProperties: { type: "number" },
                    description: "Optional { roomNumber: roomElementId } to pin a specific room when a number is ambiguous. The room's Number must match the key."
                },
                phaseName: { type: "string", description: "Only consider rooms in this phase (exact name). Without it, a number found in several phases is refused as ambiguous." },
                allowDesignOptionRooms: { type: "boolean", default: false, description: "Also consider design-option rooms during resolution. Target rooms in a design option are still refused; design-option elements are never used." },
                hostedCategories: { type: "array", items: { type: "string" }, description: "BuiltInCategory names of ceiling-hosted families to redraw. Default [\"OST_LightingFixtures\"]." },
                existingPolicy: {
                    type: "string",
                    enum: ["fail", "skip"],
                    default: "fail",
                    description: "A target that already contains current-phase ceilings or requested fixtures on its level: 'fail' (default) counts it as failed (with atomic=true nothing is created), 'skip' leaves it untouched. Nothing is ever deleted."
                },
                ignoreNonCeilingHostedFixtures: { type: "boolean", default: false, description: "Accept (with a warning) requested-category fixtures in the source room that are not hosted on a redrawable ceiling; they are not redrawn. Default refuses the mapping." },
                atomic: { type: "boolean", default: true, description: "true: any failed target rolls back the whole run. false: passing targets are kept, failing ones rolled back individually." },
                dryRun: { type: "boolean", default: true, description: "true: do not change the model." },
                verifyInDryRun: { type: "boolean", default: true, description: "With dryRun, create and verify everything inside a transaction group that is always rolled back." },
                boundaryToleranceMm: { type: "number", default: 150, description: "Plan tolerance against the room finish boundary (ceilings drawn into the wall still count as inside)." },
                verticalToleranceMm: { type: "number", default: 50, description: "Tolerance on the room's vertical extent when collecting; only elements on the room's own level are ever considered." },
                shapeToleranceMm: { type: "number", default: 50, description: "Maximum boundary deviation between the translated source room and the target room." },
                positionToleranceMm: { type: "number", default: 2, description: "Verification tolerance for profile curves, heights, bounding boxes and fixture positions." },
                rotationToleranceDeg: { type: "number", default: 0.1, description: "Verification tolerance for fixture orientation (0 < value <= 5)." }
            },
            required: ["mappings"]
        }
    }
];
