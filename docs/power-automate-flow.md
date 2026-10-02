# Power Automate flow: CvHub support tickets → admin e-mails

This is the exact setup for the demo video. CvHub uploads a JSON file to the Dropbox
app folder `/cvhub-support-tickets` when a user submits a support ticket; the flow
below watches that folder, parses the JSON, and e-mails the admins.

---

## 0. What CvHub uploads

One file per ticket, e.g. `support-ticket-20261002-101530-4f2a1c.json`:

```json
{
  "reported_by": "Jane Candidate (Candidate)",
  "reported_by_email": "candidate@cvhub.local",
  "position": "Senior .NET Backend Engineer",
  "link": "http://cvms.tangent.website/positions/55",
  "priority": "High",
  "summary": "Cannot open the CV list from the position page.",
  "admin_emails": ["admin@cvhub.local", "you@gmail.com"],
  "created_at": "2026-10-02T10:15:30Z",
  "application": "CvHub"
}
```

`position` is `null` when the ticket is raised from a non-position page.

---

## 1. One-time Dropbox setup (already done with you)

1. https://www.dropbox.com/developers/apps → **Create app**:
   - API: **Scoped access**, access: **App folder**, name: `CvHub Support Tickets`.
2. **Permissions tab**: enable `files.content.write`, `files.content.read` → Submit.
3. **Settings tab**: generate access code via OAuth2 authorize URL
   (`https://www.dropbox.com/oauth2/authorize?client_id=…&response_type=code&token_access_type=offline`)
   and exchange the code once at `https://api.dropboxapi.com/oauth2/token` to get the
   long-lived **refresh token**.
4. Paste `ClientId`, `ClientSecret`, `RefreshToken` into CvHub's `appsettings.json`
   → `Dropbox` section (done together).

---

## 2. Build the flow (make.powerautomate.com)

**Trigger:**
1. **Create → Automated cloud flow** → name `CvHub support tickets` → skip trigger picker,
   add: **Dropbox — "When a file is added"**.
2. Folder: `/cvhub-support-tickets` · *Include subfolders*: No.
3. After signing in, open trigger **Settings** → set **Interval = 1 Minute** (default 5–15
   min makes the demo wait too long). The polling delay resets on save, so re-save 1 min
   before you record the demo.

**Action 1 — Parse the JSON:**
1. Add action: **Data Operation — Parse JSON**.
2. Content: from dynamic content pick **File content** (Dropbox trigger).
3. Schema → **Generate from sample** → paste a sample from section 0 → Done.

**Action 2 — Compose a nicely formatted body:**
1. Add action: **Data Operation — Compose**, paste:

```text
New support ticket from CvHub

Priority: @{triggerOutputs()?['body/priority']}
Reported by: @{triggerOutputs()?['body/reported_by']}
Position: @{coalesce(triggerOutputs()?['body/position'], '—')}
Summary: @{triggerOutputs()?['body/summary']}

Page: @{triggerOutputs()?['body/link']}

Reply to: @{triggerOutputs()?['body/reported_by_email']}
```

(If you prefer the Parse JSON outputs, use the dynamic-content chips instead of the
expressions — both work; the expressions above read straight from the trigger file.)

**Action 3 — Send the e-mail** (either):
- **Office 365 Outlook — Send an email (V2)** (recommended if you have an O365 account), or
- **Gmail — Send an email** (easiest per assignment; sign in with your Gmail).

| Field | Value |
|---|---|
| To | `@{join(triggerOutputs()?['body/admin_emails'], ';')}` |
| Subject | `[@{triggerOutputs()?['body/priority']}] CvHub support ticket — @{triggerOutputs()?['body/summary']}` |
| Body | Output of the Compose action |
| Importance | expression: `@{if(equals(triggerOutputs()?['body/priority'], 'High'), 'High', 'Normal')}` |

**(Optional) Action 4 — Teams notification:**
Add **Microsoft Teams — Post message in a chat or channel**, pick your user chat, message
= the Compose output. Requires the Teams connector sign-in; skip if you don't use Teams.

Turn the flow **On**.

---

## 3. Demo video script (the checklist)

1. Show Dropbox app folder `/cvhub-support-tickets` (empty).
2. Show the flow in Power Automate: trigger (1-min polling), Parse JSON, Send an email.
3. CvHub → log in as `candidate@cvhub.local` → click the **? help icon** (header).
4. Fill summary "Cannot open the CV list from the position page", Priority **High** → Create ticket.
5. Show the dialog's success screen (JSON preview) — read the fields out loud.
6. Back in Dropbox: the new `support-ticket-*.json` file appears.
7. Wait ~1–2 min → Power Automate flow run history shows the run succeeded (open it to show steps).
8. Open the admin inbox: e-mail arrived — subject `[High] CvHub support ticket — …`, formatted body.
9. Repeat quickly with a **Low** priority ticket from a **position page** (show `Position` and `Link` filled, admins listed) to demonstrate both cases.

**Timing tip:** save the flow 60–90 s before recording step 4, so the first poll fires fast.

---

## 4. Troubleshooting

| Symptom | Fix |
|---|---|
| Upload error in CvHub dialog | `Dropbox` section in appsettings missing/typo → check `RefreshToken` still valid (regenerate), restart service. |
| Flow never triggers | Check folder path matches; check run history has no skipped runs; re-save flow (resets poll timer). |
| E-mail to admins fails | `admin_emails` empty → assign the Admin role to at least one user, or add your Gmail as a fallback. |
| Parse JSON error | File uploaded before flow existed → create a fresh ticket; the schema matches section 0. |
