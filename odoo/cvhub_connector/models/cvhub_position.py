# -*- coding: utf-8 -*-
import json
import logging

import requests
from odoo import api, fields, models

_logger = logging.getLogger(__name__)

# Attribute type of CvHub -> label used in the tree/form views.
TYPE_LABELS = {
    "String": "String",
    "Text": "Text",
    "Image": "Image",
    "Numeric": "Numeric",
    "Date": "Date",
    "Period": "Period",
    "Boolean": "Boolean",
    "OneOfMany": "One of many",
}


class CvhubPosition(models.Model):
    _name = "cvhub.position"
    _description = "CvHub Position (imported)"
    _order = "cvhub_updated_at desc, id desc"

    name = fields.Char(string="Title", required=True, index=True)
    # 0 = created in Odoo (not yet exported); NULL-safe via export wizard below.
    cvhub_id = fields.Integer(string="CvHub Id", index=True)
    company_name = fields.Char(string="Company")
    level = fields.Char(string="Level")
    short_description = fields.Text(string="Description")
    access = fields.Selection(
        [("public", "Public"), ("restricted", "Restricted")],
        string="Access",
        readonly=True,
    )
    max_projects = fields.Integer(string="Max Projects")
    cv_count = fields.Integer(string="Published CVs", readonly=True)
    source_url = fields.Char(string="Source URL", help="CvHub instance this record was imported from.")
    cvhub_updated_at = fields.Datetime(string="Updated (CvHub)")
    imported_at = fields.Datetime(string="Imported At", default=fields.Datetime.now, readonly=True)
    last_imported_at = fields.Datetime(string="Last Re-import", readonly=True)
    exported = fields.Boolean(string="Exported to CvHub", default=False, readonly=True)
    cvhub_export_id = fields.Integer(string="Exported CvHub Id", readonly=True)

    attribute_ids = fields.One2many("cvhub.position.attribute", "position_id", string="Attributes")
    attribute_count = fields.Integer(compute="_compute_attribute_count", string="Attributes")

    _sql_constraints = [
        # Partial unique index handled below; plain unique would block multiple
        # Odoo-created positions that all start with cvhub_id = 0.
    ]

    def init(self):
        # Unique per imported CvHub id, ignoring Odoo-created rows (cvhub_id = 0).
        self.env.cr.execute(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS cvhub_position_cvhub_id_uniq
            ON cvhub_position (cvhub_id) WHERE cvhub_id <> 0
            """
        )

    @api.depends("attribute_ids")
    def _compute_attribute_count(self):
        for rec in self:
            rec.attribute_count = len(rec.attribute_ids)

    # ------------------------------------------------------------------
    # Import (used by the import wizard's action)
    # ------------------------------------------------------------------
    def _get_config(self):
        """Base URL + token from system parameters (import wizard reads them too)."""
        icp = self.env["ir.config_parameter"].sudo()
        base = (icp.get_param("cvhub_connector.base_url") or "").rstrip("/")
        token = icp.get_param("cvhub_connector.api_token") or ""
        return base, token

    @staticmethod
    def _err(resp, default):
        """Prefer the server's own error text over a generic message."""
        try:
            msg = resp.json().get("message")
        except Exception:
            msg = None
        return msg or default

    def _fetch(self, path):
        base, token = self._get_config()
        if not base or not token:
            raise ValueError(
                "CvHub connection is not configured. Set the API token and base URL "
                "in the import dialog."
            )
        resp = requests.get(f"{base}{path}", headers={"X-Api-Token": token}, timeout=30)
        if resp.status_code == 401:
            raise ValueError("CvHub rejected the API token (401). Check the token value.")
        if resp.status_code == 403:
            # 403 means either a revoked token or one that is not scoped to a position;
            # CvHub sends the reason in the body, so pass it through.
            raise ValueError(_err(resp, "This API token cannot be used (403). Generate a new one in CvHub."))
        resp.raise_for_status()
        return resp.json()

    # ------------------------------------------------------------------
    # Export-back (optional feature)
    # ------------------------------------------------------------------
    def action_export_back(self):
        """POST this position (title, attributes) back to CvHub as a new position."""
        self.ensure_one()
        base, token = self._get_config()
        if not base or not token:
            raise ValueError(
                "CvHub connection is not configured. Run an import first or set the "
                "token in the import dialog."
            )

        payload = {
            "title": self.name,
            "shortDescription": self.short_description or None,
            "company": self.company_name or None,
            "level": self.level or None,
            "maxProjects": self.max_projects or 5,
            "attributes": [
                {
                    "name": a.name,
                    "type": a.attr_type,
                    "required": a.required,
                    "section": a.section or None,
                    # OneOfMany attributes are rejected by CvHub without their option
                    # list, so the options must travel with the export.
                    "options": a.options or None,
                }
                for a in self.attribute_ids
            ],
        }
        if self.exported and self.cvhub_export_id:
            # Already exported: update is not supported by the API; block politely.
            raise ValueError(
                f"This position was already exported to CvHub (id {self.cvhub_export_id})."
            )

        resp = requests.post(
            f"{base}/api/ext/positions",
            headers={"X-Api-Token": token, "Content-Type": "application/json"},
            data=json.dumps(payload),
            timeout=30,
        )
        if resp.status_code in (401, 403):
            raise ValueError(f"CvHub rejected the API token ({resp.status_code}).")
        if resp.status_code >= 400:
            try:
                msg = resp.json().get("message", resp.text[:200])
            except Exception:
                msg = resp.text[:200]
            raise ValueError(f"CvHub returned {resp.status_code}: {msg}")

        new_id = resp.json().get("id")
        self.write({"exported": True, "cvhub_export_id": new_id})
        # Show the notification with the created CvHub id.
        return {
            "type": "ir.actions.client",
            "tag": "display_notification",
            "params": {
                "title": "Exported to CvHub",
                "message": f"Position created in CvHub with id {new_id}.",
                "sticky": False,
                "type": "success",
            },
        }

    def action_reimport(self):
        """Refresh aggregates + fields from CvHub for the selected positions."""
        for rec in self:
            data = rec._fetch(f"/api/ext/positions/{rec.cvhub_id}")
            rec._apply_detail(data)
            rec.last_imported_at = fields.Datetime.now()
        return True

    def _apply_detail(self, data):
        """Apply a /positions/{id} payload to this record (fields + attribute lines)."""
        self.write(
            {
                "name": data.get("title") or self.name,
                "company_name": data.get("company") or None,
                "level": data.get("level") or None,
                "short_description": data.get("shortDescription") or None,
                "access": "public" if data.get("access") == 1 else "restricted",
                "max_projects": data.get("maxProjects") or 5,
                "cv_count": data.get("cvCount") or 0,
                "cvhub_updated_at": self._parse_dt(data.get("updatedAt")),
            }
        )
        # Replace attribute lines with fresh aggregates.
        self.attribute_ids.unlink()
        vals = []
        for a in data.get("attributes") or []:
            vals.append(
                {
                    "position_id": self.id,
                    "name": a.get("name") or "?",
                    "attr_type": a.get("type") or "String",
                    "category": a.get("category"),
                    "required": bool(a.get("required")),
                    "section": a.get("section"),
                    "options": a.get("options"),
                    "aggregate_json": json.dumps(a.get("aggregate") or {}),
                }
            )
        if vals:
            self.env["cvhub.position.attribute"].create(vals)

    @staticmethod
    def _parse_dt(value):
        if not value:
            return False
        try:
            # CvHub sends ISO 8601 with offset; Odoo wants naive UTC datetimes.
            # Convert to UTC explicitly — astimezone(tz=None) would use the server's
            # local zone and silently shift every timestamp.
            from datetime import timezone

            from dateutil import parser as du_parser

            return du_parser.isoparse(value).astimezone(timezone.utc).replace(tzinfo=None)
        except Exception:
            return False


class CvhubPositionAttribute(models.Model):
    _name = "cvhub.position.attribute"
    _description = "CvHub Position Attribute (imported)"
    _order = "position_id, id"

    position_id = fields.Many2one("cvhub.position", required=True, ondelete="cascade", index=True)
    name = fields.Char(string="Attribute", required=True)
    attr_type = fields.Selection(
        [(k, v) for k, v in TYPE_LABELS.items()], string="Type", required=True
    )
    category = fields.Char(string="Category")
    required = fields.Boolean(string="Required")
    section = fields.Char(string="Section")
    options = fields.Text(string="Options")
    aggregate_json = fields.Text(string="Aggregates")

    # Human-friendly aggregate summaries for the views.
    aggregate_summary = fields.Char(compute="_compute_aggregate_summary", string="Aggregated result")

    @api.depends("aggregate_json")
    def _compute_aggregate_summary(self):
        for rec in self:
            try:
                agg = json.loads(rec.aggregate_json or "{}")
            except Exception:
                agg = {}
            rec.aggregate_summary = rec._summarize(agg)

    @staticmethod
    def _summarize(agg):
        if not isinstance(agg, dict) or not agg:
            return "no data"
        parts = [f"count {agg.get('count', 0)}"]
        if "avg" in agg:
            parts.append(f"avg {agg['avg']}")
        if "min" in agg and "max" in agg:
            parts.append(f"min {agg['min']}")
            parts.append(f"max {agg['max']}")
        if "trues" in agg:
            parts.append(f"yes {agg.get('trues', 0)} / no {agg.get('falses', 0)}")
        if "top" in agg and agg.get("top"):
            top = ", ".join(f"{t['value']} ({t['count']})" for t in agg["top"][:3])
            parts.append(f"top: {top}")
        if "distinct" in agg:
            parts.append(f"distinct {agg['distinct']}")
        if "earliestStart" in agg and agg.get("earliestStart"):
            parts.append(f"from {agg['earliestStart'][:10]}")
        if "latestEnd" in agg and agg.get("latestEnd"):
            parts.append(f"to {agg['latestEnd'][:10]}")
        return "; ".join(parts)
