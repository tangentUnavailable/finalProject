# CvHub connector for Odoo

Mirrors CvHub positions into Odoo and can export an Odoo position back to CvHub.

## Connection settings

Both live in `ir.config_parameter`, and are edited from the import wizard
(*Import positions from CvHub* → **CvHub URL** / **API Token**), which saves them on run:

| Key | Value |
|---|---|
| `cvhub_connector.base_url` | `https://cvms.tangent.website` |
| `cvhub_connector.api_token` | the token created below |

Use `https://`. Plain `http://` still works, but the app answers it with a
307 redirect to `https://`, so every import pays an extra round trip.

## The token

Create it in CvHub under **Attribute Library → API tokens** (or
`POST /api/ext/tokens/` with a Recruiter/Admin session):

```bash
curl -X POST https://cvms.tangent.website/api/ext/tokens/ \
  -H 'Content-Type: application/json' \
  -b cookies.txt \
  -d '{"name":"odoo-vps"}'          # no positionId => a GLOBAL token
```

CvHub issues two kinds of token, and this connector needs the **global** one:

- **Position-scoped** — created with a `positionId`, can read *only* that
  position. Used by integrations that own a single requisition. A scoped token
  gets `404` (not `403`) for any other position, so the error shape never
  confirms that another position exists.
- **Global** — created without a `positionId`. Readable only when its creator is
  a Recruiter or Admin; any other global token is refused with `403`. This is the
  shape Odoo needs, because it mirrors the **whole** board rather than one
  requisition.

Candidates cannot mint or use global tokens: the mint endpoint is
role-gated to Admin/Recruiter, and both read endpoints re-check staff status on
every request.

> **Changing this breaks the connector.** The legacy unscoped tokens created
> before per-position scoping were revoked on 2026-10-02, which is why Odoo
> started returning `403 "This API token has been revoked."`. If that message
> appears, mint a new global token and paste it into the wizard — the stored
> value in the database is only a cache of whatever was last saved there.

## What it does

- `action_import` — fetches `/api/ext/positions`, then `/api/ext/positions/{id}`
  per position, and upserts `cvhub.position` rows keyed on `cvhub_id`. Aggregate
  attribute results are stored verbatim in `aggregate_json`.
- `action_export_back` — `POST /api/ext/positions`, creating the position in
  CvHub. Attributes are matched by name against the CvHub library and created
  under the `Imported` category when missing; a `OneOfMany` attribute must carry
  its options or the API rejects it.

## Troubleshooting

| Error | Cause |
|---|---|
| `CvHub rejected the API token (401)` | token value is wrong or was never saved |
| `This API token has been revoked. (403)` | revoked, or a legacy unscoped token — mint a new global one |
| `This API token is not scoped to a position. (403)` | a global token whose creator is not staff |
| `Position not found. (404)` | a *scoped* token asked for a position that isn't its own |