# -*- coding: utf-8 -*-
import json
import logging

import requests
from odoo import api, fields, models

_logger = logging.getLogger(__name__)


class CvhubImportWizard(models.TransientModel):
    _name = "cvhub.import.wizard"
    _description = "Import positions from CvHub"

    base_url = fields.Char(
        string="CvHub URL",
        required=True,
        default=lambda self: self.env["ir.config_parameter"]
        .sudo()
        .get_param("cvhub_connector.base_url", "http://cvms.tangent.website"),
        help="Base URL of the CvHub instance, e.g. http://cvms.tangent.website",
    )
    api_token = fields.Char(
        string="API Token",
        required=True,
        password=True,
        default=lambda self: self.env["ir.config_parameter"]
        .sudo()
        .get_param("cvhub_connector.api_token", ""),
    )
    result_summary = fields.Text(string="Result", readonly=True)

    def action_import(self):
        self.ensure_one()
        base = (self.base_url or "").rstrip("/")
        token = (self.api_token or "").strip()

        def fetch(path):
            resp = requests.get(f"{base}{path}", headers={"X-Api-Token": token}, timeout=30)
            if resp.status_code == 401:
                raise ValueError("CvHub rejected the API token (401). Check the token value.")
            if resp.status_code == 403:
                raise ValueError("This API token has been revoked (403). Generate a new one in CvHub.")
            resp.raise_for_status()
            return resp.json()

        # Persist connection settings for later re-imports and exports.
        icp = self.env["ir.config_parameter"].sudo()
        icp.set_param("cvhub_connector.base_url", base)
        icp.set_param("cvhub_connector.api_token", token)

        positions = fetch("/api/ext/positions")
        imported, updated = 0, 0
        Position = self.env["cvhub.position"]
        for row in positions:
            existing = Position.search([("cvhub_id", "=", row["id"])], limit=1)
            detail = fetch(f"/api/ext/positions/{row['id']}")
            if existing:
                existing._apply_detail(detail)
                existing.last_imported_at = fields.Datetime.now()
                updated += 1
            else:
                rec = Position.create(
                    {
                        "cvhub_id": row["id"],
                        "name": detail.get("title") or row.get("title") or "?",
                        "source_url": base,
                        "imported_at": fields.Datetime.now(),
                    }
                )
                rec._apply_detail(detail)
                imported += 1
        self.result_summary = f"Imported {imported} new position(s), refreshed {updated} existing."

        return {
            "type": "ir.actions.act_window",
            "name": "Import from CvHub",
            "res_model": "cvhub.import.wizard",
            "res_id": self.id,
            "view_mode": "form",
            "target": "new",
        }
