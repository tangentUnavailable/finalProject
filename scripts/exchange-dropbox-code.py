#!/usr/bin/env python3
"""Exchange a Dropbox OAuth authorization code for a permanent refresh token.

Usage: python3 exchange-dropbox-code.py <code> <client_id> <client_secret>

The refresh token is printed to stdout (capture it immediately into appsettings).
Access tokens are short-lived and not needed — CvHub uses the refresh token
to get fresh access tokens on demand.

The code is single-use and expires in ~10 minutes after the user authorises.
"""
import json
import sys
import urllib.parse
import urllib.request

code = sys.argv[1]
client_id = sys.argv[2]
client_secret = sys.argv[3]

data = urllib.parse.urlencode({
    "code": code,
    "client_id": client_id,
    "client_secret": client_secret,
    "grant_type": "authorization_code",
    "redirect_uri": "http://localhost",
}).encode()

req = urllib.request.Request(
    "https://api.dropboxapi.com/oauth2/token",
    data=data,
    headers={"Content-Type": "application/x-www-form-urlencoded"},
)

with urllib.request.urlopen(req, timeout=30) as resp:
    body = json.load(resp.fp if hasattr(resp, "fp") else resp)

refresh_token = body.get("refresh_token")
if not refresh_token:
    # Fallback: try parsing as raw text
    raw = json.loads(resp.read())
    refresh_token = raw.get("refresh_token", "")

print(refresh_token)
