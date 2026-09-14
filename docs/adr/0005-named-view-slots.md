# ADR 0005: Named view slots in host-offer V2

Status: Accepted

The Community shell needs main-area extension views as well as its existing right rail.
The host-offer envelope advances to version 2; the fragment mount protocol remains version 1.
V1 clients fail closed on the new envelope. V2 clients also accept V1 for the rail only.

The closed slot set is `landscape-right-rail`, `view-roadmaps`, `view-lifecycle`, and
`view-reviews`. Unknown envelope versions, slots, mount kinds, or mount versions are ignored.
Roadmaps maps to `atlas.analysis.roadmap`, Lifecycle to `atlas.analysis.eol`, and Reviews to
`atlas.ai.review`. Integration mapping (`atlas.analysis.integration-map`) and APM
(`atlas.analysis.apm`) remain right-rail extensions.

A named view activates only with an enabled summary capability and a matching valid offer.
Account closure refreshes both surfaces and removes stale mounts. The server remains the
entitlement authority; fragment GET endpoints must enforce access independently.
Fragments are self-contained HTML isolated in an iframe with an empty sandbox and no referrer.
The host supplies routing and mount points; Atlas Enterprise supplies analysis content.
