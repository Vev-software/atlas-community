using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts.Taxonomy;

namespace Vev.Atlas.Domain;

/// <summary>
/// Atlas role names. Atlas owns its role <i>definitions</i>; Fabric owns the authorization mechanism
/// that evaluates them (handbook 11 §4).
/// </summary>
public static class AtlasRoles
{
    /// <summary>May edit the catalogue.</summary>
    public const string Architect = "AtlasArchitect";

    /// <summary>Read-only access to the catalogue (e.g. the customer portal).</summary>
    public const string Customer = "AtlasCustomer";
}

/// <summary>Coarse Atlas actions passed to the Fabric authorizer.</summary>
public static class AtlasActions
{
    /// <summary>Read the catalogue.</summary>
    public const string AssetRead = "atlas.asset.read";

    /// <summary>Create, edit or delete catalogue entries.</summary>
    public const string AssetWrite = "atlas.asset.write";

    /// <summary>
    /// Export the whole tenant landscape as one downloadable document. A full-map export is the
    /// highest-value reconnaissance read, so it is a distinct, elevated action — not plain read
    /// (atlas#36).
    /// </summary>
    public const string LandscapeExport = "atlas.landscape.export";
}

/// <summary>
/// Atlas capability identifiers from the VEV taxonomy (fabric#7). The <b>free</b> asset-management
/// capabilities do not pass through entitlement — they are the hook. The <b>paid</b> capabilities are
/// reserved here so the entitlement seam exists before the features do (handbook 09, 11 §4, atlas#8),
/// and are denied in Community.
/// </summary>
public static class AtlasCapabilities
{
    // --- Paid capabilities: reserved seams, entitlement-denied in Community ---
    //
    // These are *aliases* of the canonical Fabric taxonomy ids (Vev.Fabric.Contracts AtlasTaxonomy),
    // which is the single source of truth for the reserved-paid set. CommunityEntitlementService
    // evaluates a signed snapshot keyed on those exact canonical ids, so a Community gate must use the
    // canonical id or it can never be granted. The ReservedPaidTaxonomyAlignmentTests fail closed if any
    // of these drifts away from a Reserved capability in the Fabric catalog.

    /// <summary>Integration mapping with ownership + criticality (paid Atlas core).</summary>
    public static readonly CapabilityId IntegrationMapping = AtlasTaxonomy.AnalysisIntegrationMap;

    /// <summary>End-of-life tracking + risk (paid Atlas core).</summary>
    public static readonly CapabilityId EndOfLifeTracking = AtlasTaxonomy.AnalysisEndOfLife;

    /// <summary>
    /// Application-portfolio-management analysis (paid Atlas core) — the canonical reserved capability id
    /// from the public Fabric taxonomy (<c>atlas.analysis.apm</c>). Gates the mounted "portfolio health"
    /// analysis ui-extension (atlas#141): grants are matched by id string against the signed snapshot,
    /// which is keyed on the canonical taxonomy ids, so the gate must use this id for a grant to resolve.
    /// </summary>
    public static readonly CapabilityId PortfolioAnalysis = AtlasTaxonomy.AnalysisApm;

    /// <summary>EA roadmap generation (paid Atlas core, via the Fabric AI contract).</summary>
    public static readonly CapabilityId RoadmapGeneration = AtlasTaxonomy.AnalysisRoadmap;

    /// <summary>AI architecture review (paid Atlas core, via the Fabric AI contract).</summary>
    public static readonly CapabilityId AiReview = AtlasTaxonomy.AiReview;

    /// <summary>AI-generated draft deliverables over a selected landscape slice (paid Atlas Enterprise).</summary>
    public static readonly CapabilityId AiGenerate = AtlasTaxonomy.AiGenerate;

    /// <summary>Grounded read-only chat over the tenant's own landscape.</summary>
    public static readonly CapabilityId AiChat = new("atlas.ai.chat");

    /// <summary>Grounded setup copilot for first-run onboarding and feature explanation (via the Fabric AI contract).</summary>
    public static readonly CapabilityId SetupAssist = new("atlas.ai.assist.setup");

    /// <summary>Narrative plain-language brief for a selected landscape slice (via the Fabric AI contract).</summary>
    public static readonly CapabilityId AiBrief = new("atlas.ai.brief");

    /// <summary>AI-assisted draft structuring of pasted or uploaded customer content into Atlas assets and relationships.</summary>
    public static readonly CapabilityId AiStructure = new("atlas.ai.structure");

    /// <summary>Deterministic export of a selected landscape slice as a portable context pack.</summary>
    public static readonly CapabilityId ContextExport = new("atlas.context.export");

    /// <summary>Read-only MCP access to the tenant catalogue for the customer's own AI agent.</summary>
    public static readonly CapabilityId McpRead = new("atlas.mcp.read");

    /// <summary>Data introspection seam for schema auto-scan into the catalogue (paid Atlas Enterprise).</summary>
    public static readonly CapabilityId DataIntrospection = AtlasTaxonomy.DataIntrospection;

    /// <summary>Data overlap analysis seam for domain/dublet and consumer-map analysis (paid Atlas Enterprise).</summary>
    public static readonly CapabilityId DataOverlap = AtlasTaxonomy.DataOverlap;

    /// <summary>Data quality seam for classification, provenance and quality reporting (paid Atlas Enterprise).</summary>
    public static readonly CapabilityId DataQuality = AtlasTaxonomy.DataQuality;

    /// <summary>ArchiMate export seam for data-layer round-trip interoperability (paid Atlas Enterprise).</summary>
    public static readonly CapabilityId ArchimateExport = AtlasTaxonomy.ExportArchiMate;

    /// <summary>
    /// The reserved paid capabilities, as one authoritative set. The free/paid line is entitlement-only:
    /// a Community-installed module may add value at the edges (importers/exporters, connectors, panels)
    /// but may never declare or satisfy one of these — that would be a back-door around the entitlement
    /// gate. The module install guard rejects any module that claims one (atlas#22, engineering#3).
    /// </summary>
    public static readonly IReadOnlySet<CapabilityId> ReservedPaid = new HashSet<CapabilityId>
    {
        IntegrationMapping,
        EndOfLifeTracking,
        PortfolioAnalysis,
        RoadmapGeneration,
        AiReview,
        AiGenerate,
        DataIntrospection,
        DataOverlap,
        DataQuality,
        ArchimateExport,
    };

    /// <summary>Whether <paramref name="capability"/> is a reserved paid capability no module may claim.</summary>
    public static bool IsReservedPaid(CapabilityId capability) => ReservedPaid.Contains(capability);
}
