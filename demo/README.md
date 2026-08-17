# Atlas demo landscape

A ready-to-import example IT landscape, so you can explore Atlas Community without entering your
own data first. The data is **fictional** — a fast-grown SMB (a made-up Danish D2C e-commerce
company, roughly 5–10 years old that scaled quickly). Company, vendors and values are illustrative;
no real customer data.

## What's inside

| File | Format | Contents |
|------|--------|----------|
| `smb-landscape.atlas.json` | canonical `atlas-json` bundle (`mode: replace`) | **57 assets / 71 relationships** |

The landscape covers what such a company typically accumulates:

- **6 systems** — E-commerce, ERP & Finance, CRM & Marketing, HR & People, Data & Analytics, IT & Collaboration.
- **22 applications** — a mix of SaaS (Stripe, HubSpot, Zendesk, Personio, Snowflake, Power BI, Entra ID, …) and custom-built services, including one **retired** legacy Magento shop from before the replatform.
- **6 servers** and **7 infrastructure** items — Kubernetes, managed PostgreSQL, object storage, CDN/DNS, plus an on-prem office network and NAS kept from the early days.
- A full **data layer** — 4 data areas → 4 datasets → 8 columns, with PII marked and dataset joins.

## Import it

**Today (JSON, via the portability API):**

```bash
curl -X POST "http://localhost:5199/api/v1/import?format=atlas-json" \
  -H "Content-Type: application/json" \
  --data-binary @demo/smb-landscape.atlas.json
```

- `mode: replace` matches your tenant to this bundle. Switch to `merge` to upsert into an existing
  landscape instead of replacing it.

**From the UI:** open the **Import** panel (next to **Export JSON** in the landscape toolbar), pick this
file, choose **Merge** or **Replace**, and import — the panel links straight back to this folder. Import
is an author-only action, so sign in with an author-capable principal to see it.

## Keeping it valid

The bundle conforms to the published `atlas-contracts` model: valid asset kinds, lifecycle states
and relationship types, and the data-layer containment rule (each data area is `part-of` a system,
each dataset `part-of` a data area, each column `part-of` a dataset). If you extend it, keep those
invariants so it imports cleanly.
