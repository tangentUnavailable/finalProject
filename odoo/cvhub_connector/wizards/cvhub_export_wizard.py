# -*- coding: utf-8 -*-
import json
import logging

import requests
from odoo import api, fields, models

_logger = logging.getLogger(__name__)


class CvhubExportWizard(models.TransientModel):
    _name = "cvhub.export.wizard"
    _description = "Create position in Odoo and export it to CvHub"

    name = fields.Char(string="Title", required=True)
    company_name = fields.Char(string="Company")
    level = fields.Char(string="Level")
    short_description = fields.Text(string="Description")
    max_projects = fields.Integer(string="Max Projects", default=5)
    attribute_line_ids = fields.One2many("cvhub.export.wizard.line", "wizard_id", string="Attributes")
    result_message = fields.Text(string="Result", readonly=True)

    def action_export(self):
        self.ensure_one()
        # 1) Create the position in Odoo first (the user's data lives here).
        attr_vals = [
            {
                "name": line.name,
                "attr_type": line.attr_type,
                "category": line.category or None,
                "required": line.required,
                "section": line.section or None,
                "options": line.options or None,
                "aggregate_json": "{}",
            }
            for line in self.attribute_line_ids
        ]
        position = self.env["cvhub.position"].create(
            {
                "name": self.name,
                "company_name": self.company_name or None,
                "level": self.level or None,
                "short_description": self.short_description or None,
                "max_projects": self.max_projects or 5,
                "access": "public",
                "cvhub_id": 0,  # not imported from CvHub; created here
                "attribute_ids": [(0, 0, v) for v in attr_vals],
            }
        )  # name is required at create — set here explicitly for clarity
        # 2) Push it to CvHub.
        result = position.action_export_back()
        self.result_message = f"Created in Odoo (id {position.id}) and exported to CvHub."
        return result


class CvhubExportWizardLine(models.TransientModel):
    _name = "cvhub.export.wizard.line"
    _description = "Attribute line for the CvHub export wizard"

    wizard_id = fields.Many2one("cvhub.export.wizard", required=True, ondelete="cascade")
    name = fields.Char(string="Attribute", required=True)
    attr_type = fields.Selection(
        [("String", "String"), ("Text", "Text"), ("Numeric", "Numeric"), ("Date", "Date"),
         ("Period", "Period"), ("Boolean", "Boolean"), ("OneOfMany", "One of many")],
        string="Type", required=True, default="String",
    )
    category = fields.Char(string="Category")
    required = fields.Boolean(string="Required")
    section = fields.Char(string="Section")
    options = fields.Text(string="Options (one per line, for OneOfMany)")
