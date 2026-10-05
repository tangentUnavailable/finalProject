#!/usr/bin/env python3
"""Adds/updates the Salesforce section in a CvHub appsettings JSON file.

Usage: python3 add-salesforce-config.py <appsettings.json> <client_id> <client_secret>

Mirrors add-dropbox-config.py: secrets arrive via argv (visible only in this process)
and are never printed. Existing keys are preserved, so this is safe to re-run to
rotate the Connected App secret without rewriting the whole file.

CentralOrg=true uses the client-credentials flow, so every CvHub user syncs into the
dev org without holding a Salesforce account. Pass --per-user to switch to the Web
Server OAuth flow instead (each user consents individually).
"""
import json
import sys

USAGE = (
    "usage: add-salesforce-config.py <appsettings.json> <client_id> <client_secret> "
    "[--per-user] [--callback <path>] [--login <url>] [--api-version <vXX.0>] [--token-url <auto|login|instance|url>]"
)

callback = "/signin-salesforce"
login_url = "https://login.salesforce.com"
api_version = "v62.0"
token_url = "auto"

# Parse sequentially rather than by filtering: a flag's value (e.g. a URL) is a
# positional-looking argument, so "everything that isn't a flag" picks it up and
# shifts the real operands.
positional = []
per_user = False
argv = sys.argv[1:]
i = 0
while i < len(argv):
    a = argv[i]
    if a in ("--callback", "--login", "--api-version", "--token-url"):
        if i + 1 >= len(argv):
            sys.exit(f"{a} needs a value\n{USAGE}")
        value = argv[i + 1]
        if a == "--callback":
            callback = value
        elif a == "--login":
            login_url = value
        elif a == "--api-version":
            api_version = value
        else:
            token_url = value
        i += 2
        continue
    if a == "--per-user":
        per_user = True
    elif a.startswith("-"):
        sys.exit(f"unknown option: {a}\n{USAGE}")
    else:
        positional.append(a)
    i += 1

if len(positional) < 3:
    sys.exit(USAGE)

path, cid, sec = positional[0], positional[1], positional[2]

if not cid.strip() or not sec.strip():
    sys.exit("ClientId and ClientSecret must both be non-empty.")

with open(path) as f:
    data = json.load(f)

section = data.get("Salesforce") or {}

# Preserve anything already there (e.g. a hand-tuned CentralTokenUrl) unless overridden.
section.update(
    {
        "ClientId": cid.strip(),
        "ClientSecret": sec.strip(),
        "CallbackPath": callback,
        "LoginUrl": login_url.rstrip("/"),
        "ApiVersion": api_version,
        "CentralTokenUrl": token_url,
        "CentralOrg": not per_user,
    }
)

data["Salesforce"] = section

with open(path, "w") as f:
    json.dump(data, f, indent=2)
    f.write("\n")

print(f"OK: Salesforce section written to {path}")
print(f"    flow        = {'client credentials (central org)' if section['CentralOrg'] else 'web server OAuth (per user)'}")
print(f"    callback    = {login_url.rstrip('/').split('//')[-1].split('.')[0]} host + {callback}")
print(f"    token url   = {section['CentralTokenUrl']}")
print("    restart: sudo systemctl restart cvhub.service")