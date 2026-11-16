import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * RFA family-editor tools (rfa_*). They operate on the ACTIVE family document:
 * rfa_new_family (from a .rft template) or rfa_open_family (existing .rfa) makes a family
 * the active document — or the user opens one in Revit by hand — then the other tools
 * add reference planes, extrusions, parameters and types inside it, and
 * rfa_load_into_project loads the finished family into an open project.
 * C# handlers: MCP/Core/Commands/CommandExecutor.RfaFamily*.cs.
 * Units: length mm, angle degrees, area m², volume m³.
 */

const profileLoopSchema = {
    type: "object",
    description:
        "One closed loop. Give exactly one of: points (polyline, auto-closed), rectangle, or circle. " +
        "Coordinates are 2D [u, v] in mm on the sketch plane (XY: u=X, v=Y; XZ: u=X, v=Z; YZ: u=Y, v=Z; " +
        "referencePlane: u/v along the plane's own X/Y vectors from its origin). " +
        "Curved Rhino edges must be discretised into points, except full circles.",
    properties: {
        points: {
            type: "array",
            items: { type: "array", items: { type: "number" }, minItems: 2, maxItems: 2 },
            description: "Polyline vertices [[u, v], ...] in mm, at least 3. The loop closes automatically.",
        },
        rectangle: {
            type: "object",
            properties: {
                center: { type: "array", items: { type: "number" }, description: "[u, v] in mm. Default [0, 0]." },
                width: { type: "number", description: "Size along u, mm." },
                height: { type: "number", description: "Size along v, mm." },
            },
        },
        circle: {
            type: "object",
            properties: {
                center: { type: "array", items: { type: "number" }, description: "[u, v] in mm." },
                radius: { type: "number", description: "Radius in mm." },
            },
        },
    },
};

export const rfaFamilyTools: Tool[] = [
    {
        name: "rfa_get_document_info",
        description:
            "Read the active family (.rfa) document. Returns category, placement type, types, family parameters with current values, reference planes, levels, views, existing solid/void forms (each with its subcategory name or null and its visibility settings: coarse, medium, fine, planRcp, frontBack, leftRight, whenCutInPlanRcp, or null when the form has none), the subcategories of the family category (id + name), the materials available in the family document (id + name; forms and material parameters can only reference these) " +
            "and nestedFamilies: every editable, non-in-place family loaded into this family document with id, name, category, type names and instanceCount (use rfa_place_nested_family to load and place more). " +
            "Call this first before the other editing rfa_* tools; it fails if the active document is a project instead of a family (use rfa_new_family or rfa_open_family to make a family active).",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "rfa_create_reference_plane",
        description:
            "Create reference planes in the active family document. Creates one or more named planes. " +
            "Shortcut form: normal X/Y/Z + offsetMm (X/Y planes are vertical and drawn in a plan view; Z planes are horizontal and drawn in a front/back elevation). " +
            "Advanced form: bubbleEndMm + freeEndMm + cutVector. Planes whose name already exists are skipped.",
        inputSchema: {
            type: "object",
            properties: {
                planes: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            name: { type: "string", description: "Reference plane name, e.g. \"Left\", \"Top\"." },
                            normal: { type: "string", enum: ["X", "Y", "Z"], description: "Plane normal axis (shortcut form)." },
                            offsetMm: { type: "number", description: "Plane position along the normal axis, mm. Default 0." },
                            extentMm: { type: "number", description: "Half-length of the drawn plane line, mm. Default 1000." },
                            bubbleEndMm: { type: "array", items: { type: "number" }, description: "[x, y, z] mm (advanced form)." },
                            freeEndMm: { type: "array", items: { type: "number" }, description: "[x, y, z] mm (advanced form)." },
                            cutVector: { type: "array", items: { type: "number" }, description: "[x, y, z] direction lying in the plane, perpendicular to the line; must be parallel to the host view's direction (advanced form)." },
                            viewName: { type: "string", description: "Optional host view name. Auto-picked when omitted." },
                        },
                        required: ["name"],
                    },
                    description: "Reference planes to create.",
                },
            },
            required: ["planes"],
        },
    },
    {
        name: "rfa_create_extrusion",
        description:
            "Create an extrusion in the active family document. Builds a solid or void from one or more closed 2D profiles (extra loops inside the first become holes). " +
            "Sketch on plane XY/XZ/YZ (+ planeOffsetMm) or on a named reference plane; the extrusion runs along the sketch plane normal (XY → +Z, XZ → -Y, YZ → +X). " +
            "Optionally assign a material and bind extrusionEnd/extrusionStart/material/visible to existing family parameters.",
        inputSchema: {
            type: "object",
            properties: {
                profiles: { type: "array", items: profileLoopSchema, description: "Closed loops; the first is the outer boundary." },
                plane: { type: "string", enum: ["XY", "XZ", "YZ"], description: "Sketch plane when referencePlane is not given. Default XY." },
                planeOffsetMm: { type: "number", description: "Offset of the sketch plane along its normal axis, mm. Default 0." },
                referencePlane: { type: "string", description: "Sketch on this named reference plane instead of plane/planeOffsetMm (keeps the form hosted on it)." },
                startOffsetMm: { type: "number", description: "Extrusion start along the normal, mm. Default 0." },
                endOffsetMm: { type: "number", description: "Extrusion end along the normal, mm. Negative values extrude the other way." },
                isSolid: { type: "boolean", description: "true = solid (default), false = void.", default: true },
                material: { type: "string", description: "Material name that already exists in the family document (solid only). Create it first with rfa_create_material if missing." },
                associate: {
                    type: "object",
                    description: "Bind extrusion properties to family parameters, e.g. {\"extrusionEnd\": \"Height\", \"material\": \"Body Material\"}. Keys: extrusionEnd, extrusionStart (length params), material (material param), visible (yes/no param).",
                    additionalProperties: { type: "string" },
                },
            },
            required: ["profiles", "endOffsetMm"],
        },
    },
    {
        name: "rfa_add_parameter",
        description:
            "Add family parameters to the active family document. Each may carry a formula or an initial value for the current type. " +
            "Existing names are skipped. Values use mm / degrees / m² / m³ / true-false / material name.",
        inputSchema: {
            type: "object",
            properties: {
                parameters: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            name: { type: "string" },
                            type: {
                                type: "string",
                                enum: ["length", "number", "integer", "yes_no", "text", "angle", "area", "volume", "material"],
                                description: "Parameter data type. Default length.",
                            },
                            group: {
                                type: "string",
                                enum: ["dimensions", "constraints", "materials", "identity_data", "data", "text", "graphics", "construction", "visibility", "other"],
                                description: "Properties-palette group. Default: dimensions (materials for material, identity_data for text).",
                            },
                            isInstance: { type: "boolean", description: "Instance parameter (true) or type parameter (false, default)." },
                            formula: { type: "string", description: "Optional Revit formula, e.g. \"Width / 2\"." },
                            value: { description: "Optional initial value for the current type (ignored when formula is set)." },
                        },
                        required: ["name", "type"],
                    },
                },
            },
            required: ["parameters"],
        },
    },
    {
        name: "rfa_set_family_type",
        description:
            "Create or update a family type in the active family document. The type becomes current; sets parameter values and formulas, optionally rename. " +
            "A new type copies values from baseType (or the current type).",
        inputSchema: {
            type: "object",
            properties: {
                typeName: { type: "string", description: "Type to create or update." },
                baseType: { type: "string", description: "When creating, copy values from this existing type." },
                renameTo: { type: "string", description: "Optional new name for the type." },
                values: {
                    type: "object",
                    description: "Parameter name → value. Length mm, angle degrees, area m², volume m³, yes/no true-false, material by name.",
                    additionalProperties: true,
                },
                formulas: {
                    type: "object",
                    description: "Parameter name → formula string (empty string or null clears the formula).",
                    additionalProperties: true,
                },
            },
            required: ["typeName"],
        },
    },
    {
        name: "rfa_create_dimension",
        description:
            "Create a dimension in the active family document. Measures between parallel reference planes. " +
            "Two planes + label binds the dimension to a length parameter (the planes then follow the parameter); three or more planes + equalize sets EQ. " +
            "Combine with rfa_align_to_reference_plane so forms follow the planes.",
        inputSchema: {
            type: "object",
            properties: {
                referencePlanes: {
                    type: "array",
                    items: { type: "string" },
                    description: "Names of parallel reference planes (order does not matter; they are sorted along the normal).",
                },
                label: { type: "string", description: "Length family parameter to label the dimension with (exactly two planes)." },
                equalize: { type: "boolean", description: "Set EQ on a multi-segment dimension (three or more planes)." },
                throughPointMm: {
                    type: "array",
                    items: { type: "number" },
                    description: "[x, y, z] mm point the dimension line passes through. Default: end of the first plane, usually outside the geometry.",
                },
                viewName: { type: "string", description: "Host view. Auto-picked (plan for vertical planes, elevation for horizontal) when omitted." },
            },
            required: ["referencePlanes"],
        },
    },
    {
        name: "rfa_align_to_reference_plane",
        description:
            "Lock form faces to reference planes. Works in the active family document. " +
            "For each plane, finds the form's planar face lying on it and creates a locked alignment, so the form moves with the plane (e.g. when a labelled dimension changes). " +
            "The face must already be coplanar with the plane.",
        inputSchema: {
            type: "object",
            properties: {
                elementId: { type: "number", description: "ElementId of the extrusion / blend / sweep." },
                referencePlanes: { type: "array", items: { type: "string" }, description: "Reference plane names to align to." },
                lock: { type: "boolean", description: "Lock the alignments. Default true.", default: true },
                viewName: { type: "string", description: "Optional host view; auto-picked when omitted." },
            },
            required: ["elementId", "referencePlanes"],
        },
    },
    {
        name: "rfa_create_sweep",
        description:
            "Create a sweep in the active family document. Sweeps closed profile loops along a planar path (handrails, mouldings, frames). " +
            "Profiles are drawn in 2D [u, v] mm and Revit places them perpendicular to the path at its start. " +
            "Path: pathPoints polyline or pathSegments of lines and 3-point arcs, all in one plane; or pathReferenceLineIds (reference lines from " +
            "rfa_create_reference_line) so the sweep follows the lines when parameters move them. With diameterParameter and no profiles, a circular " +
            "profile is generated and its diameter is labelled with the parameter (parametric round pipe); the result includes a flex check.",
        inputSchema: {
            type: "object",
            properties: {
                pathReferenceLineIds: {
                    type: "array",
                    items: { type: "number" },
                    description: "Reference line element ids forming a connected planar path (closed loops allowed). The sweep follows these lines.",
                },
                diameterParameter: { type: "string", description: "Length parameter for a circular profile's diameter (pathReferenceLineIds mode). Profiles may then be omitted." },
                profileCurveIndex: { type: "number", description: "Index of the path curve where the profile is placed (pathReferenceLineIds mode). Default 0." },
                viewName: { type: "string", description: "View looking along the path at the profile, for the diameter label. Auto-picked when omitted." },
                pathPoints: {
                    type: "array",
                    items: { type: "array", items: { type: "number" } },
                    description: "Polyline path [[x, y, z], ...] in mm (family coordinates).",
                },
                closed: { type: "boolean", description: "Close the pathPoints polyline back to its first point." },
                pathSegments: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            line: { type: "object", properties: { start: { type: "array", items: { type: "number" } }, end: { type: "array", items: { type: "number" } } } },
                            arc: { type: "object", properties: { start: { type: "array", items: { type: "number" } }, mid: { type: "array", items: { type: "number" } }, end: { type: "array", items: { type: "number" } } } },
                        },
                    },
                    description: "Alternative to pathPoints: connected line / arc segments, [x, y, z] mm.",
                },
                profiles: { type: "array", items: profileLoopSchema, description: "Cross-section loops in [u, v] mm; extra loops inside the first become holes." },
                isSolid: { type: "boolean", description: "true = solid (default), false = void.", default: true },
                material: { type: "string", description: "Material name that already exists in the family document (solid only). Create it first with rfa_create_material if missing." },
                associate: {
                    type: "object",
                    description: "Bind properties to family parameters. Keys: material (material param), visible (yes/no param).",
                    additionalProperties: { type: "string" },
                },
            },
        },
    },
    {
        name: "rfa_create_blend",
        description:
            "Create a blend in the active family document. Lofts from a base loop to a top loop (one closed loop each, any shapes) over heightMm along the sketch plane normal. " +
            "Sketch on plane XY/XZ/YZ (+ planeOffsetMm) or a named reference plane; both loops use the same [u, v] mm coordinates.",
        inputSchema: {
            type: "object",
            properties: {
                baseProfile: profileLoopSchema,
                topProfile: profileLoopSchema,
                heightMm: { type: "number", description: "Distance from base to top along the sketch plane normal, mm." },
                plane: { type: "string", enum: ["XY", "XZ", "YZ"], description: "Sketch plane when referencePlane is not given. Default XY." },
                planeOffsetMm: { type: "number", description: "Offset of the sketch plane along its normal axis, mm. Default 0." },
                referencePlane: { type: "string", description: "Sketch the base on this named reference plane instead." },
                isSolid: { type: "boolean", description: "true = solid (default), false = void.", default: true },
                material: { type: "string", description: "Material name that already exists in the family document (solid only). Create it first with rfa_create_material if missing." },
                associate: {
                    type: "object",
                    description: "Bind properties to family parameters. Keys: blendTop, blendBase (length params), material, visible.",
                    additionalProperties: { type: "string" },
                },
            },
            required: ["baseProfile", "topProfile", "heightMm"],
        },
    },
    {
        name: "rfa_create_reference_line",
        description:
            "Create a reference line (straight line or 3-point arc) hosted on a reference plane in the active family document, and constrain it to other reference planes. " +
            "Reference lines are the rig for parametric pipes and bars: constrain the line here (align, or dimension with a length parameter), then sweep a profile along it " +
            "with rfa_create_sweep pathReferenceLineIds. This works where constraining sketch curves does not (family documents have no sketch-edit API). " +
            "The line moves with hostPlane along its normal; use constraints for the in-plane directions. Targets: curve (straight line parallel to the plane), start, end, center (arc). " +
            "Arcs can also carry radiusParameter. Every point must already sit where the constraint says (on the plane, or at the parameter's current distance).",
        inputSchema: {
            type: "object",
            properties: {
                hostPlane: { type: "string", description: "Reference plane the line lies in (its work plane)." },
                line: {
                    type: "object",
                    properties: {
                        startMm: { type: "array", items: { type: "number" }, description: "[x, y, z] mm" },
                        endMm: { type: "array", items: { type: "number" }, description: "[x, y, z] mm" },
                    },
                },
                arc: {
                    type: "object",
                    properties: {
                        startMm: { type: "array", items: { type: "number" } },
                        midMm: { type: "array", items: { type: "number" } },
                        endMm: { type: "array", items: { type: "number" } },
                    },
                    description: "3-point arc, [x, y, z] mm.",
                },
                constraints: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            target: { type: "string", enum: ["curve", "start", "end", "center"], description: "Which part to constrain. Default curve." },
                            referencePlane: { type: "string" },
                            distanceParameter: { type: "string", description: "Length parameter for the distance. Omit to align and lock (the part must lie on the plane)." },
                            viewName: { type: "string", description: "View for the alignment/dimension. Auto-picked (elevations first) when omitted." },
                        },
                        required: ["referencePlane"],
                    },
                },
                radiusParameter: { type: "string", description: "Arc only: length parameter labelling the radius." },
                viewName: { type: "string", description: "View for the radius dimension. Auto-picked when omitted." },
            },
            required: ["hostPlane"],
        },
    },
    {
        name: "rfa_create_symbolic_line",
        description:
            "Create a symbolic line (straight line or 3-point arc) in the active family document, and constrain it to reference planes. " +
            "Symbolic lines are the family's 2D representation (door elevation swing, plan swing arc); they show only in views parallel to hostPlane. " +
            "Constraints work like rfa_create_reference_line: align a part to a plane, or dimension it with a length parameter, so the line follows the family parameters. " +
            "Constraints on an existing symbolic line cannot be re-hosted through the API — to change what a line follows, delete it and redraw it here.",
        inputSchema: {
            type: "object",
            properties: {
                hostPlane: { type: "string", description: "Reference plane the line lies in (its work plane), e.g. \"Interior\" for a door elevation swing." },
                line: {
                    type: "object",
                    properties: {
                        startMm: { type: "array", items: { type: "number" }, description: "[x, y, z] mm" },
                        endMm: { type: "array", items: { type: "number" }, description: "[x, y, z] mm" },
                    },
                },
                arc: {
                    type: "object",
                    properties: {
                        startMm: { type: "array", items: { type: "number" } },
                        midMm: { type: "array", items: { type: "number" } },
                        endMm: { type: "array", items: { type: "number" } },
                    },
                    description: "3-point arc, [x, y, z] mm.",
                },
                subcategory: {
                    type: "string",
                    description: "Subcategory of the family category that sets the line style, e.g. \"Elevation Swing\". Optional suffix \" [projection]\" (default) or \" [cut]\". Omit for the category default.",
                },
                constraints: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            target: { type: "string", enum: ["curve", "start", "end", "center"], description: "Which part to constrain. Default curve." },
                            referencePlane: { type: "string" },
                            distanceParameter: { type: "string", description: "Length parameter for the distance. Omit to align and lock (the part must lie on the plane)." },
                            viewName: { type: "string", description: "View for the alignment/dimension. Auto-picked (elevations first) when omitted." },
                        },
                        required: ["referencePlane"],
                    },
                },
            },
            required: ["hostPlane"],
        },
    },
    {
        name: "rfa_save_as",
        description:
            "Save the active family document to a .rfa file. The active document then points at that file. " +
            "filePath must be an absolute path in an existing folder; an existing file is refused unless overwrite is true. " +
            "Use it to keep the original family untouched by saving the edited one under a new name.",
        inputSchema: {
            type: "object",
            properties: {
                filePath: { type: "string", description: "Absolute path of the .rfa to write (\".rfa\" is appended when no extension is given)." },
                overwrite: { type: "boolean", description: "Replace the file if it already exists. Default false.", default: false },
            },
            required: ["filePath"],
        },
    },
    {
        name: "rfa_new_family",
        description:
            "Create a new family document from a template. Builds the family from a .rft, saves it to savePath, then opens it as the active document so the other rfa_* tools work on it. " +
            "Give exactly one of templatePath (absolute .rft path) or templateName (file name searched under Revit's family template folder, e.g. \"Metric Generic Model\"); " +
            "when the name is not found the error lists available templates. savePath must be an absolute path in an existing folder; an existing file is refused unless overwrite is true, " +
            "and a file currently open in Revit is always refused. If Revit cannot activate the new document, the error says whether the file was saved so it can be opened by hand.",
        inputSchema: {
            type: "object",
            properties: {
                templatePath: { type: "string", description: "Absolute path of a family template (.rft)." },
                templateName: { type: "string", description: "Template file name with or without \".rft\", searched recursively under Revit's family template folder." },
                savePath: { type: "string", description: "Absolute path of the .rfa to write (\".rfa\" is appended when no extension is given)." },
                overwrite: { type: "boolean", description: "Replace savePath if it already exists. Default false.", default: false },
            },
            required: ["savePath"],
        },
    },
    {
        name: "rfa_open_family",
        description:
            "Open a family file as the active document. Opens an existing .rfa in Revit (or switches to it when it is already open) so the other rfa_* tools work on it. " +
            "Returns category, placement type, types and wasAlreadyOpen. filePath must be an absolute path to an existing .rfa.",
        inputSchema: {
            type: "object",
            properties: {
                filePath: { type: "string", description: "Absolute path of the .rfa to open." },
            },
            required: ["filePath"],
        },
    },
    {
        name: "rfa_load_into_project",
        description:
            "Load the active family into an open project. Equivalent to the family editor's Load into Project: the active family document is loaded into a project (.rvt) already open in the same Revit session. " +
            "targetProject matches a project title (with or without \".rvt\") or full path; when omitted, the only open project is used and the error lists the candidates if there are none or several. " +
            "If the family already exists in the project it is reloaded (wasReload: true); overwriteParameterValues decides whether existing type parameter values are replaced. " +
            "Returns the family name and id in the project, its types and category. The family editor stays the active document unless activateProject is true. " +
            "A family that was never saved loads under its temporary title (e.g. \"Family1\"); use rfa_save_as first to give it a proper name.",
        inputSchema: {
            type: "object",
            properties: {
                targetProject: { type: "string", description: "Title (with or without \".rvt\") or full path of an open project document. Optional when exactly one project is open." },
                overwriteParameterValues: {
                    type: "boolean",
                    description: "When the family already exists in the project, overwrite its existing type parameter values with the family's. Default false.",
                    default: false,
                },
                activateProject: {
                    type: "boolean",
                    description: "After loading, switch Revit's active document to the target project. A failed switch is reported in warnings, not as an error. Default false.",
                    default: false,
                },
            },
        },
    },
    {
        name: "rfa_create_revolution",
        description:
            "Create a revolution in the active family document. Spins a solid or void from one or more closed 2D profiles (extra loops inside the first become holes) around an axis line. " +
            "Sketch on plane XY/XZ/YZ (+ planeOffsetMm) or on a named reference plane. The axis is given in the SAME 2D [u, v] mm coordinates as the profiles, so it always lies in the sketch plane " +
            "(e.g. on plane XZ, axis {start: [0, 0], end: [0, 1000]} is the vertical Z axis). " +
            "Every profile must stay on one side of the axis; touching the axis is allowed (that is how you get a solid with no centre hole). A profile that crosses the axis is rejected before Revit is called, with the loop index and how far it crosses. " +
            "startAngleDeg/endAngleDeg default to 0/360; the span must be between 0.01° and 360°. " +
            "Returns the angles read back from the element and its bounding box in mm as a self-check. " +
            "Optionally assign a material and bind startAngle/endAngle/material/visible to existing family parameters.",
        inputSchema: {
            type: "object",
            properties: {
                profiles: { type: "array", items: profileLoopSchema, description: "Closed loops; the first is the outer boundary. All must lie on one side of the axis." },
                plane: { type: "string", enum: ["XY", "XZ", "YZ"], description: "Sketch plane when referencePlane is not given. Default XY." },
                planeOffsetMm: { type: "number", description: "Offset of the sketch plane along its normal axis, mm. Default 0." },
                referencePlane: { type: "string", description: "Sketch on this named reference plane instead of plane/planeOffsetMm (keeps the form hosted on it)." },
                axis: {
                    type: "object",
                    description: "Revolution axis as a line in the sketch plane's 2D coordinates (mm). Its direction sets the rotation sense (right-hand rule).",
                    properties: {
                        start: { type: "array", items: { type: "number" }, minItems: 2, maxItems: 2, description: "[u, v] in mm." },
                        end: { type: "array", items: { type: "number" }, minItems: 2, maxItems: 2, description: "[u, v] in mm; must differ from start." },
                    },
                    required: ["start", "end"],
                },
                startAngleDeg: { type: "number", description: "Start angle in degrees. Default 0.", default: 0 },
                endAngleDeg: { type: "number", description: "End angle in degrees. Default 360.", default: 360 },
                isSolid: { type: "boolean", description: "true = solid (default), false = void.", default: true },
                material: { type: "string", description: "Material name that already exists in the family document (solid only). Create it first with rfa_create_material if missing." },
                associate: {
                    type: "object",
                    description: "Bind revolution properties to family parameters, e.g. {\"endAngle\": \"Sweep Angle\", \"material\": \"Body Material\"}. Keys: startAngle, endAngle (angle params), material (material param), visible (yes/no param).",
                    additionalProperties: { type: "string" },
                },
            },
            required: ["profiles", "axis"],
        },
    },
    {
        name: "rfa_create_swept_blend",
        description:
            "Create a swept blend in the active family document. Lofts from startProfile to endProfile (one closed loop each, any shapes) along ONE path curve " +
            "(transitions, tapered ducts, horns, curved tapering arms). " +
            "Path: exactly one of path ({line: {start, end}} or {arc: {start, mid, end}}, [x, y, z] mm in family coordinates) or pathReferenceLineId " +
            "(one reference line from rfa_create_reference_line, so the form follows the line when parameters move it). A swept blend path is a single curve: " +
            "for a polyline or multi-segment path, split it into several swept blends. " +
            "Profiles are drawn in 2D [u, v] mm around the origin, like rfa_create_sweep profiles, and Revit places each one perpendicular to the path at its own end. " +
            "The tool verifies which profile Revit actually put at the path start and, if Revit swapped them, recreates the form the right way round (reported in warnings). " +
            "Returns path start/end/length, the curve makeup Revit has at each end (startProfileCurves / endProfileCurves, e.g. \"4 Line\" or \"2 Arc\"), the placement evidence, " +
            "and the bounding box in mm as a self-check. Optionally assign a material and bind material/visible to existing family parameters.",
        inputSchema: {
            type: "object",
            properties: {
                path: {
                    type: "object",
                    description: "Single path curve in mm (family coordinates): {line: {start, end}} or {arc: {start, mid, end}}. Not an array.",
                    properties: {
                        line: { type: "object", properties: { start: { type: "array", items: { type: "number" } }, end: { type: "array", items: { type: "number" } } } },
                        arc: { type: "object", properties: { start: { type: "array", items: { type: "number" } }, mid: { type: "array", items: { type: "number" } }, end: { type: "array", items: { type: "number" } } } },
                    },
                },
                pathReferenceLineId: { type: "number", description: "Alternative to path: element id of ONE reference line; the swept blend follows it." },
                startProfile: { ...profileLoopSchema, description: "Closed loop at the path start, [u, v] mm around the origin." },
                endProfile: { ...profileLoopSchema, description: "Closed loop at the path end, [u, v] mm around the origin." },
                isSolid: { type: "boolean", description: "true = solid (default), false = void.", default: true },
                material: { type: "string", description: "Material name that already exists in the family document (solid only). Create it first with rfa_create_material if missing." },
                associate: {
                    type: "object",
                    description: "Bind properties to family parameters. Keys: material (material param), visible (yes/no param).",
                    additionalProperties: { type: "string" },
                },
            },
            required: ["startProfile", "endProfile"],
        },
    },
    {
        name: "rfa_create_material",
        description:
            "Create materials in the active family document. Forms and material parameters can only reference materials that already exist in the family file; this adds them. " +
            "Each item creates a blank material or duplicates an existing one (duplicateFrom), then applies the shading color [r, g, b] 0-255, transparency 0-100 and materialClass. " +
            "Existing names are skipped, or updated in place with updateExisting: true. " +
            "Returns created / updated / skipped / errors (values read back from Revit) and the sorted list of every material now in the family document.",
        inputSchema: {
            type: "object",
            properties: {
                materials: {
                    type: "array",
                    items: {
                        type: "object",
                        properties: {
                            name: { type: "string", description: "Material name." },
                            color: {
                                type: "array",
                                items: { type: "integer", minimum: 0, maximum: 255 },
                                minItems: 3,
                                maxItems: 3,
                                description: "Shading color [r, g, b], each 0-255.",
                            },
                            transparency: { type: "integer", minimum: 0, maximum: 100, description: "Shading transparency 0-100." },
                            materialClass: { type: "string", description: "Material class, e.g. \"Metal\", \"Glass\", \"Wood\"." },
                            duplicateFrom: { type: "string", description: "Name of an existing material in the family document to duplicate instead of creating a blank one; the other fields are applied on top." },
                            updateExisting: { type: "boolean", description: "When a material with this name already exists: false (default) = skip it; true = apply color/transparency/materialClass to it.", default: false },
                        },
                        required: ["name"],
                    },
                    description: "Materials to create.",
                },
            },
            required: ["materials"],
        },
    },
    {
        name: "rfa_set_form_visibility",
        description:
            "Set subcategory and visibility of family forms. Works on forms (extrusion, blend, sweep, revolution, swept blend) in the active family document. " +
            "subcategory assigns the forms to a subcategory of the family category, creating it when missing (unless createSubcategory: false); an empty string clears the assignment back to the family category. " +
            "subcategoryStyle sets the subcategory's line color, projection/cut line weights and material (the material must already exist in the family document; create it with rfa_create_material) — it modifies an existing subcategory too, which affects every element on it. " +
            "visibility sets the Family Element Visibility Settings: detail levels (coarse, medium, fine) and view directions (planRcp, frontBack, leftRight, whenCutInPlanRcp); fields not given keep the form's current value. " +
            "Each form is processed on its own; invalid combinations (e.g. void forms, or whenCutInPlanRcp on a category that does not support it) are reported per id in errors or warnings without aborting the others. " +
            "Returns the subcategory (name, id, created, style read back), updated forms with subcategory and visibility read back from Revit, errors, the sorted available subcategories and warnings. Use rfa_get_document_info to find form ids.",
        inputSchema: {
            type: "object",
            properties: {
                elementIds: {
                    type: "array",
                    items: { type: "integer" },
                    description: "Element ids of forms in the family document (see forms in rfa_get_document_info).",
                },
                subcategory: {
                    type: "string",
                    description: "Subcategory of the family category to put the forms on. Created if missing (see createSubcategory). Empty string = clear, back to the family category itself.",
                },
                createSubcategory: {
                    type: "boolean",
                    description: "Create the subcategory when it does not exist (default true). When false, a missing name is an error listing the available subcategories.",
                    default: true,
                },
                subcategoryStyle: {
                    type: "object",
                    description: "Style applied to the subcategory (new or existing). Requires a non-empty subcategory.",
                    properties: {
                        lineColor: {
                            type: "array",
                            items: { type: "integer", minimum: 0, maximum: 255 },
                            minItems: 3,
                            maxItems: 3,
                            description: "Line color [r, g, b], each 0-255.",
                        },
                        projectionLineWeight: { type: "integer", minimum: 1, maximum: 16, description: "Projection line weight 1-16." },
                        cutLineWeight: { type: "integer", minimum: 1, maximum: 16, description: "Cut line weight 1-16 (only for cuttable categories)." },
                        material: { type: "string", description: "Name of a material that exists in the family document. Create it first with rfa_create_material if missing." },
                    },
                },
                visibility: {
                    type: "object",
                    description: "Family Element Visibility Settings. Give any subset; omitted fields keep the form's current value.",
                    properties: {
                        coarse: { type: "boolean", description: "Show at Coarse detail level." },
                        medium: { type: "boolean", description: "Show at Medium detail level." },
                        fine: { type: "boolean", description: "Show at Fine detail level." },
                        planRcp: { type: "boolean", description: "Show in Plan/RCP views." },
                        frontBack: { type: "boolean", description: "Show in Front/Back views." },
                        leftRight: { type: "boolean", description: "Show in Left/Right views." },
                        whenCutInPlanRcp: { type: "boolean", description: "Show when cut in Plan/RCP (only for categories that are cut in the symbol)." },
                    },
                },
            },
            required: ["elementIds"],
        },
    },
    {
        name: "rfa_place_nested_family",
        description:
            "Load and place a nested family in the active family. Source is exactly one of familyPath (absolute path of an .rfa to load into the active family document; reload/overwriteParameterValues control what happens when it is already loaded, " +
            "and an already-loaded family is reused with wasAlreadyLoaded: true) or familyName (a family already loaded in the family document; see nestedFamilies in rfa_get_document_info). The active family's own file cannot be nested into itself. " +
            "typeName picks the type (default: first by name; the error lists available types). Each item in instances is placed at locationMm [x, y, z] with an optional rotationDeg about the vertical axis through that point; " +
            "instances may be omitted to only load the family and/or bind parameters. " +
            "Supported placement types: OneLevelBased (hosted on the family's level whose elevation is at or below z), WorkPlaneBased that is not face-based, and ViewBased 2D families (detail items / annotations, placed in viewName or a plan view). " +
            "Face-based, wall/floor/ceiling-hosted, line-based, two-level and adaptive families are rejected per item with the placement type named. " +
            "associate maps NESTED parameter name -> HOST family parameter name (e.g. {\"Width\": \"Handle Width\", \"visible\": \"Show Handle\"}); each key is looked up on the instance first, then on the type (type parameters are bound once on the type and only to host type parameters); " +
            "the key \"visible\" is the instance's built-in Visible parameter. applyToExisting also binds existing instances of that type. Binding problems are reported in warnings naming both sides and never abort. " +
            "Returns family (name, id, category, placementType, wasAlreadyLoaded, reloaded), type, availableTypes, placed (id, locationMm and rotationDeg read back), associated, nestedParameters (editable instance / type parameter names of the nested family), errors (per instance index) and warnings. " +
            "Limitation: placed instances sit at fixed coordinates and are NOT constrained to reference planes, so they do not move when the host family flexes unless the nested family is driven through associated parameters.",
        inputSchema: {
            type: "object",
            properties: {
                familyPath: { type: "string", description: "Absolute path of the .rfa to load into the active family document. Give this or familyName, not both." },
                familyName: { type: "string", description: "Name of a family already loaded in the active family document. Give this or familyPath, not both." },
                reload: {
                    type: "boolean",
                    description: "With familyPath: when a family of that name is already loaded, reload it from the file. Default false (the loaded version is reused).",
                    default: false,
                },
                overwriteParameterValues: {
                    type: "boolean",
                    description: "With reload: overwrite the existing type parameter values with the file's. Default false.",
                    default: false,
                },
                typeName: { type: "string", description: "Type of the nested family to place. Default: the first type by name." },
                instances: {
                    type: "array",
                    description: "Instances to place. May be empty or omitted.",
                    items: {
                        type: "object",
                        properties: {
                            locationMm: { type: "array", items: { type: "number" }, minItems: 3, maxItems: 3, description: "[x, y, z] in mm, family coordinates." },
                            rotationDeg: { type: "number", description: "Rotation about the vertical Z axis through locationMm, degrees. Default 0." },
                        },
                        required: ["locationMm"],
                    },
                },
                viewName: { type: "string", description: "ViewBased (2D) families only: name of the view to place them in. Default: the active plan view, else the first floor plan." },
                associate: {
                    type: "object",
                    description: "NESTED parameter name -> HOST family parameter name. The host parameter must already exist (rfa_add_parameter) and have the same data type. Key \"visible\" = the instance's Visible parameter (bind to a yes/no host parameter).",
                    additionalProperties: { type: "string" },
                },
                applyToExisting: {
                    type: "boolean",
                    description: "Also apply associate to instances of this type that already exist in the family document. Default false.",
                    default: false,
                },
            },
        },
    },
];
