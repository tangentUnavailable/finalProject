#!/usr/bin/env python3
"""Adds/updates the Dropbox section in a CvHub appsettings JSON file.

Usage: python3 add-dropbox-config.py <appsettings.json> <client_id> <client_secret> <refresh_token>

Secrets are passed via argv (visible only in this process), never printed.
"""
import json
import sys

path, cid, sec, rt = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]

with open(path) as f:
    data = json.load(f)

data["Dropbox"] = {
    "ClientId": cid,
    "ClientSecret": sec,
    "RefreshToken": rt,
    "Folder": "/cvhub-support-tickets",
}

with open(path, "w") as f:
    json.dump(data, f, indent=2)
    f.write("\n")

print(f"OK: Dropbox section written to {path}")
