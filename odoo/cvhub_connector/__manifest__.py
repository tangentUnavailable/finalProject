{
    "name": "CvHub Connector",
    "summary": "Import positions and aggregated results from CvHub; optionally create positions and export them back.",
    "description": """
================================================================
CvHub Connector
================================================================
Read-only viewer + optional export-back for the CvHub CV platform.

* Import positions and their aggregated results (average/min/max
  for numbers, most popular values for texts, ...) from a CvHub
  instance using a per-inventory API token.
* View the list of imported positions with detailed information:
  attributes (title, type) and their aggregated results.
* Optional: create a position in Odoo and export it back to CvHub
  (creates the position and its attributes there).
""",
    "author": "CvHub course project",
    "category": "Extra Tools",
    "version": "18.0.1.0.0",
    "depends": ["base"],
    "data": [
        "security/cvhub_security.xml",
        "security/ir.model.access.csv",
        "views/cvhub_position_views.xml",
        "views/cvhub_import_wizard_views.xml",
        "views/cvhub_export_wizard_views.xml",
        "views/menus.xml",
    ],
    "external_dependencies": {
        "python": ["requests"],
    },
    "application": True,
    "installable": True,
    "license": "LGPL-3",
}
