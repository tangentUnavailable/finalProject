# CvHub — Codebase Walkthrough

> **What this document is.** I'm the intern who built this, and this is the document I'd bring to my demo.
> It explains **every file in the repository**, what each one does, why it exists, and how the whole app
> flows from a URL in the browser to a row in PostgreSQL and back. The target audience is someone seeing
> the code for the first time (my manager, a teammate, or future-me).

---

## Table of contents

1. [The 30-second pitch](#1-the-30-second-pitch)
2. [Repository map](#2-repository-map)
3. [The four projects and how they talk](#3-the-four-projects-and-how-they-talk)
4. [The data model (every table explained)](#4-the-data-model-every-table-explained)
5. [File-by-file walkthrough](#5-file-by-file-walkthrough)
   - 5.1 Root & build files
   - 5.2 `CvHub/` — the server project
   - 5.3 `CvHub/Features/` — vertical feature slices
   - 5.4 `CvHub/Infrastructure/` — core business logic
   - 5.5 `CvHub/Services/` — app services & seeders
   - 5.6 `CvHub/Components/` — UI (layout, shared, account)
   - 5.7 `CvHub.Client/` — WebAssembly runtime & Auto components
   - 5.8 `CvHub.Shared/` — contracts shared by both runtimes
   - 5.9 JavaScript, CSS, migrations, docs
6. [App flow — the important journeys](#6-app-flow--the-important-journeys)
7. [Cross-cutting mechanisms](#7-cross-cutting-mechanisms)
8. [Demo script & likely questions](#8-demo-script--likely-questions)

---

## 1. The 30-second pitch

**CvHub is a CV management platform.** Instead of candidates uploading static CV files, the system
*generates* a CV per job position from the candidate's living profile data:

- A recruiter defines a **Position** and picks required fields from a shared **Attribute Library**
  (the position acts as a CV *template*).
- A candidate fills those fields on their **Profile** — every value is stored **once**, as the single
  master copy.
- The **CV** is *composed live* from that master data every time it's rendered. Editing a value on the
  profile (or in-place on the CV) updates every future render. Nothing is ever copied.
- Recruiters browse **published** CVs of the candidates who gained access, discuss on the position
  board, and like CVs. Admins administer everything.

Stack in one line: **.NET 10 / Blazor Web App (Auto render mode) · EF Core + PostgreSQL (Supabase) · Tailwind CSS v4 · vertical-slice architecture.**

---

## 2. Repository map

```
CvHub.sln                     Solution: ties the three C# projects together
├── CvHub/                    ASP.NET Core server (SSR + Blazor Server circuits + all APIs)
│   ├── Program.cs            Composition root — everything starts here
│   ├── Data/                 EF Core DbContext + ApplicationUser
│   ├── Domain/               All entity classes (the database shape)
│   ├── Infrastructure/       Core business logic (CV engine, access rules, PDF, QR…)
│   ├── Features/             Vertical slices: Positions, CVs, Profile, Attributes, Search, Discussions, Admin, Badges
│   ├── Services/             App services (email, seeders, per-host service impls)
│   ├── Components/           Razor UI: layout, shared widgets, account/Identity pages
│   ├── Endpoints/            Small endpoint groups (prefs/health)
│   ├── Migrations/           EF Core migrations (10 files, 5 changesets)
│   └── wwwroot/              Static assets: JS interop, vendored libs, favicon
├── CvHub.Client/             The WebAssembly host project
│   ├── Program.cs            WASM DI container
│   ├── Components/           InteractiveAuto widgets (picker, discussion, uploader)
│   └── Services/             HTTP implementations of the shared service interfaces
├── CvHub.Shared/             Contracts + DTOs + enums used by BOTH runtimes
├── Tailwind/app.css          Tailwind v4 source stylesheet
├── docs/                     SPEC.md (requirements) and this walkthrough
└── package.json              npm scripts: build CSS, publish, deploy to VPS
```

Tracked files: **149** (about 100 are hand-written source; the rest are migrations, vendored JS libs,
and lock files). Server config (`CvHub/appsettings*.json`) is intentionally **not** in git — it carries
the Supabase connection string and Cloudinary/SMTP credentials.

---

## 3. The four projects and how they talk

### Why three C# projects?

Blazor's **InteractiveAuto** render mode means a component first runs on the server (instant, no
download), then on later visits runs in WebAssembly in the browser. A WASM component can't reach into
server DI containers for `ApplicationDbContext` — it only has an HTTP client. So the trick the codebase
uses everywhere is:

1. **`CvHub.Shared`** defines an *interface* (e.g. `IAttributeLibrary`) plus the DTOs it returns.
2. **`CvHub/Services`** has the *server* implementation (queries the DB directly, knows who the user is
   from the auth state).
3. **`CvHub.Client/Services`** has the *WASM* implementation (same interface, calls our JSON API with
   `HttpClient`; the browser automatically attaches the auth cookie).
4. `Program.cs` of each project registers the right implementation for its runtime. The component just
   injects the interface and works identically in both places.

This pattern is applied to exactly two services — `IAttributeLibrary` and `IDiscussionService` —
because those are the ones consumed by `InteractiveAuto` components living in the Client project.

### Render-mode policy

- **Every page** that touches the database is pinned to `@rendermode InteractiveServer` (see the first
  line of each `.razor` page). Reason (documented in `App.razor`): EF Core lives only on the server.
- **Small widgets** from the Client project (`AttributePicker`, `DiscussionPanel`, `ImageUploader`) are
  `InteractiveAuto` — they upgrade to WASM on subsequent visits, which is the requirement's showcase.
- Everything else (home page, search results) is **static SSR** — plain HTML, no circuit, cheapest.

### The three roles

Seeded in `IdentitySeeder`: **Admin** (god-mode, acts as the owner of any profile), **Recruiter**
(manages positions + attribute library, reads CVs, discusses, likes — *cannot* edit candidate data),
**Candidate** (the product's protagonist: profile → CV → publish).

---

## 4. The data model (every table explained)

All entities live in one file, `CvHub/Domain/Entities.cs`, registered in `Data/ApplicationDbContext.cs`.

| Entity | Table purpose | Key detail |
|---|---|---|
| `ApplicationUser` | users (extends Identity's `IdentityUser`) | adds `DisplayName`, `IsBlocked`, `CreatedAt` |
| `AttributeDef` | **Attribute Library** — one row per field type ("Years of Experience") | unique name, one of 8 `AttributeType`s, category, optional *tuning* (min/max length, regex, numeric range), `IsBuiltIn` flag, **soft delete** |
| `AttributeValue` | the single master value of an attribute for one user | per-type value columns (`StringValue`, `NumericValue`, `PeriodStart/End`, …); unique per (user, attribute) |
| `ProfileAttribute` | "pinned" attributes shown in the profile's **Info** tab | just (userId, attributeId, sortOrder) |
| `Project` | candidate portfolio project | Markdown description, period, `SortOrder` |
| `Tag` / `ProjectTag` / `PositionTag` | reusable tags + the two many-to-many joins | unique tag names |
| `Position` | the job posting + CV template | title, company, level, `Access` (Public/Restricted), `MaxProjects` cap, `CreatedByUserId`, **soft delete**, `SearchVector` (generated `tsvector` column for full-text search) |
| `PositionAttribute` | template row: "this position requires attribute X" | `Required` flag, `SortOrder`, `Section` (CV section heading like "Skills") |
| `PositionFilter` | access rule: `<attribute> <operator> <value>` | e.g. Years of Experience ≥ 3 |
| `Cv` | the CV *instance* — a pointer, not content | unique per (position, user); `Status` Draft/Published; **soft delete** |
| `CvProject` | which projects a given CV includes | pre-selected by tag match, capped by `MaxProjects` |
| `Comment` | position-board discussion post | raw **Markdown** body, chronological |
| `CvLike` | recruiter's like on a CV | unique per (cv, user) |
| `RecentAttribute` | per-user "recently used" for the attribute picker | top 5 by `UsedAt` |
| `CvAttributeVersion` | version tracking of template attributes per position | spec item; unique per (position, attribute) |

Three `DbContext` mechanics worth calling out in the demo:

1. **Optimistic locking via `xmin`** — for the four hot entities (`AttributeDef`, `AttributeValue`,
   `Position`, `Project`) the code maps PostgreSQL's system column `xmin` as a row-version property.
   Every save request echoes the version it last saw; if the row changed in the meantime → HTTP 409
   and the client adopts the server state. This is the "changed elsewhere" machinery.
2. **Global query filters** — `AttributeDef`, `Position`, `Cv` get `HasQueryFilter(!IsDeleted)`:
   soft-deleted rows vanish from every query automatically, but the data stays (spec: "hidden, not
   deleted" for CVs that lost access).
3. **Full-text search** — a raw-SQL migration created `search_vector tsvector GENERATED ALWAYS …`
   plus a GIN index on positions; `OnModelCreating` maps that column so queries can hit the index
   (`p.SearchVector.Matches(...)`).

---

## 5. File-by-file walkthrough

> Format: *path — what it does (and the one thing worth knowing about it).*

### 5.1 Root & build files

| File | What it does |
|---|---|
| `CvHub.sln` | Visual Studio solution listing the three projects. |
| `.gitignore` | Ignores `bin/obj`, `node_modules`, **`CvHub/appsettings*.json`** (secrets), the compiled `wwwroot/app.css`, publish output. |
| `package.json` | npm scripts driving the Tailwind CLI and deployment: `build:css`, `watch:css`, `publish` (CSS + `dotnet publish -c Release`), `deploy` (rsync to the `oracle` VPS **excluding `appsettings*`** so server secrets are never overwritten, then `systemctl restart cvhub`), `deploy:verify` (curl checks). |
| `package-lock.json` | npm dependency lock (Tailwind CLI only — the app itself has no npm runtime dependency). |
| `Tailwind/app.css` | Tailwind v4 entry: `@import "tailwindcss"`, the `brand` color palette, dark-mode variant, and the **print stylesheet** that turns the CV page into a clean paper CV (hides chrome, shows the QR header, forces light colors). |
| `docs/SPEC.md` | The original requirements document this project implements (the source of truth for the demo's "requirement → code" mapping). |
| `docs/CODEBASE_WALKTHROUGH.md` | This file. |
| `CvHub/Properties/launchSettings.json` | Dev server profile — HTTP port **5263**. |

### 5.2 `CvHub/` — the server project

| File | What it does |
|---|---|
| `CvHub/CvHub.csproj` | Server project. Notable NuGet: `Npgsql.EntityFrameworkCore.PostgreSQL` (PostgreSQL provider), `Markdig` + `HtmlSanitizer` (Markdown), `MailKit` (SMTP email), `QRCoder` + `QuestPDF` (PDF export), Google/Facebook auth handlers. |
| `CvHub/Program.cs` | **Composition root — read this file first in any code review.** In order: circuit + SignalR keep-alive tuning; response compression (Brotli/gzip, extended to `application/wasm`); Razor Components with **Server + WASM** interactive modes; cookie auth + conditional Google/Facebook registration (only when config values exist); PostgreSQL connection (env-var overrides → appsettings); Identity Core with roles; the custom claims factory; Markdown pipeline singleton; named HttpClient for interactive components; **per-host service registrations** (the Shared interfaces from §3); then on startup: `MigrateAsync()` + both seeders; middleware (HTTPS, compression, per-request `I18n.Current` from the `cv_lang` cookie, antiforgery, static assets); and finally **every feature's `Map*` call** plus the Blazor root and Identity endpoints. |
| `CvHub/Data/ApplicationDbContext.cs` | The EF Core model — every `DbSet`, all unique indexes, soft-delete query filters, `xmin` row-version mapping, and the generated `tsvector` column mapping (details in §4). |
| `CvHub/Data/ApplicationUser.cs` | The user entity (three added columns, see §4). |
| `CvHub/Domain/Entities.cs` | All 15 domain entities — the whole database shape in one file (details in §4). |
| `CvHub/Endpoints/PrefsEndpoints.cs` | Tiny endpoint group: `GET /prefs/theme/{light|dark}` and `/prefs/lang/{en|es}` set a year-long cookie and redirect back to the Referer (same-host check), plus `GET /health` returning "ok" for monitoring. The actual toggle *behavior* lives in `app.js` which intercepts the clicks (§5.9). |

### 5.3 `CvHub/Features/` — vertical feature slices

Each slice follows the same shape: an **API** file (minimal-API endpoints, all auth-checked), a
**Commands** file (shared create/update logic reused by API + UI), and a **Razor page** (the UI).
That's the vertical-slice architecture requirement.

**Attributes (the Attribute Library)**

| File | What it does |
|---|---|
| `Attributes/AttributesApi.cs` | CRUD for attribute definitions. Mutations are Admin/Recruiter-only; reads are open to all authenticated users (candidates browse the library in the picker). Includes `/lookup` (prefix + category + ids, returns tuning fields + `xmin` version), `/search`, `/recent` (per-user recent 5), `/pinned`, and `POST /recent/{id}` (records picker usage). Delete is *soft* and refused for built-in attributes or attributes still used by positions. |
| `Attributes/AttributeCommands.cs` | The actual create/update logic: globally-unique name enforcement (with a race-condition catch on the unique index), built-in immutability, optimistic-lock version check on update. |
| `Attributes/AttributesPage.razor` | `/attributes` — the library table (Admin/Recruiter). Toolbar pattern: **checkbox rows, no per-row buttons**; selecting rows reveals Edit/Delete actions above the table. Edit opens `AttributeEditorModal`. |

**Positions**

| File | What it does |
|---|---|
| `Positions/PositionsApi.cs` | REST for positions (Admin/Recruiter): list + get (with attributes/filters/tags and the `xmin` version), create, update, **soft** delete, and `POST /{id}/duplicate`. |
| `Positions/PositionCommands.cs` | Create/update/duplicate logic. `UpdateAsync` diffs child rows (attributes preserved when only flags change; filters/tags full-replaced). `DuplicateAsync` deep-copies attributes, filters and tags with "(copy)" suffix — the fix that made duplication real (it used to just open the editor). |
| `Positions/PositionsPage.razor` | `/positions` — sortable table (click column headers), selection toolbar with Edit/Duplicate/Delete, opens `PositionEditor` modal for create/edit. |
| `Positions/PositionDetailsPage.razor` | `/positions/{id}` — the public detail page. Tabs: **Template** (required attributes + access rules), **CVs** (recruiter/admin: published CV list with likes + CSV export button), **Discussion** (`DiscussionPanel`). Action buttons are role-aware: recruiters get Edit/Duplicate/Export; candidates get "Create CV" (only if `AccessRules` grants access and no CV exists yet, else a link to their CV). |

**CVs (the core engine's HTTP surface)**

| File | What it does |
|---|---|
| `Cvs/CvsApi.cs` | All CV endpoints: `POST /api/cvs` (create via `CvFactory`, enforces candidate-only + access + one-CV-per-position), `GET /api/cvs/{id}` (composed view, permission: owner, admin, or recruiter-when-published), project selection get/put, the **recruiter CV list** per position (with like counts), **CSV export** (RFC-4180 quoting via `CvDisplay`), **PDF export** (`CvPdfGenerator` + QR), **publish** (409 until all required fields are filled), **like toggle** (recruiter/admin only), and delete (owner or admin). |
| `Cvs/CvPage.razor` | `/cv/{id}` — the CV document. Owner sees **in-place editing** (`AttributeEditor` per field, values write straight back to the master `AttributeValue`); recruiters see a read-only render. Header: status badge, Publish button (disabled with a count of empty required fields), like button, print button. Required-but-empty fields render with a red ring. Print-only header carries the **QR code** linking back to the page. If a candidate lost access to a restricted position, the CV is shown "hidden" with an amber notice (spec: hidden, not deleted). |

**Profile**

| File | What it does |
|---|---|
| `Profile/ProfileApi.cs` | JSON API behind the profile page's autosave: save one attribute value (tuning validation → 409; optimistic-lock version check → 409; built-in attributes auto-pin on first save), read one value with its version, pin/unpin, project upsert/delete (tag names get upserted into the `Tags` table), tag list + autocomplete, and the two **badge** endpoints (JSON + downloadable SVG). |
| `Profile/ProfilePage.razor` | `/profile` and `/profile/u/{userId}` (admin viewing others). Tabs for candidates: **Me** (built-ins, always editable, profile-style rows with ✏️ icons — not a form), **Info** (pinned library attributes, pin/unpin), **Projects** (cards + `ProjectEditor`, tag autocomplete), **CVs** (list linking to each CV, toolbar delete). Recruiters get only the Me section (spec). Autosave: edits mark fields dirty; a **6-second `System.Timers.Timer`** batches them into one round-trip with tuning validation and xmin conflict handling (conflict → server state adopted + banner). Also renders the **badges panel**. |
| `Profile/PublicProfilePage.razor` | `/public-profile/{userId}` — read-only public profile for **recruiters/admin** (linked from discussion author names): photo, name, headline, about (Markdown), Info fields with "—" for empty values, projects, badges. |

**Search**

| File | What it does |
|---|---|
| `Search/SearchEndpoints.cs` | `/api/search` + the shared query logic. Positions hit the **indexed** `search_vector` column (`WebSearchToTsQuery`); attributes/tags computed inline; plus `SearchCvsAsync` for recruiters — full-text over published CVs' profile values and project text in **one aggregate SQL query** (the spec's no-queries-in-loops rule). |
| `Search/SearchPage.razor` | `/search?q=&scope=` — results grouped by kind; recruiters additionally get CV hits with like counts. |

**Discussions**

| File | What it does |
|---|---|
| `Discussions/CommentsApi.cs` | GET/POST `/api/positions/{id}/comments`. GET reveals `AuthorId` only to recruiter/admin views (candidates see names only — spec's privacy rule). |
| `Discussions/CommentCommands.cs` | Lists up to 500 comments chronologically (append-only), renders Markdown **server-side** (Markdig + HtmlSanitizer — safe for static SSR, no JS needed), validates position existence on post. |

**Admin**

| File | What it does |
|---|---|
| `Admin/AdminApi.cs` | Admin-only user management: list users with roles, block/unblock (`IsBlocked`), add/remove **any** role (including removing another admin — and admins can remove their *own* Admin role), delete user (FK conflicts become a clean 409 telling the admin to remove content first). |
| `Admin/AdminUsersPage.razor` | `/admin/users` — same toolbar table pattern; role changes via clickable role chips. |

**Badges**

| File | What it does |
|---|---|
| `Badges/BadgeService.cs` | Optional requirement: achievement badges. A catalog of 5 badges (10 projects, 5 CVs, 25 likes received, first published CV, 5 likes given); `EarnedAsync` computes them live with **one aggregate SQL query** (no badge table — always in sync with activity); `RenderPanel` produces a standalone SVG (downloadable for GitHub-profile-style use). |

### 5.4 `CvHub/Infrastructure/` — core business logic

| File | What it does |
|---|---|
| `AccessRules.cs` | **The access-control brain.** `HasAccess` short-circuits for admins/recruiters and public positions; a Restricted position requires **all** filters to match the candidate's attribute values. `Matches` implements the 8 `FilterOperator`s **type-aware** (numeric comparisons for numbers, is-true/false for booleans, equality for dropdowns/strings). Also exposes `OperatorsFor` so the filter editor only offers valid operators per attribute type. |
| `CvComposer.cs` | **The CV engine's heart.** `ComposeAsync` builds the live `CvViewDto`: loads the position template, the owner's `AttributeValues` (dictionary keyed by attribute), the CV's selected projects with tags — then maps every template row to a `CvFieldDto`, computing `IsEmpty` per type and counting missing required fields (the publish gate). Also owns `ToField`, `IsEmpty`, `Xmin` (reads the xmin row version), and `EnsureProfileAttributeAsync` (in-place CV edits auto-pin the attribute to the profile). |
| `CvFactory.cs` | CV creation: permission checks (candidates only), `AccessRules.HasAccess`, one-CV-per-position, then **pre-selects projects** whose tags match the position's tags, capped at `MaxProjects`. |
| `CvService.cs` | CV mutations shared by API and UI: project selection replace (cap-enforced), `PublishAsync` (recomposes and refuses while required fields are missing), `ToggleLikeAsync` (insert-or-delete, returns new count). |
| `AttributeValueRules.cs` | **Attribute tuning** enforcement (optional requirement): min/max length + regex for String/Text, numeric min/max — one validator shared by the profile autosave API, the profile page's timer-based batch save, and the in-place CV editor, so rules can't be bypassed from any path. |
| `CvDisplay.cs` | Value formatting for non-Razor contexts (CSV/PDF): per-type human-readable strings + `CsvCell` (RFC-4180 quoting). |
| `CvPdfGenerator.cs` | Optional requirement: real PDF export. QuestPDF renders profile fields + projects + footer; QRCoder (`PngByteQRCode` — no `System.Drawing`) stamps a QR linking back to the CV page. |
| `QrCode.cs` | Same QR generation for the **print view** of the CV page — returns a base64 PNG data-URL rendered in the print-only header. |
| `MarkdownRenderer.cs` | Markdig (advanced extensions) + HtmlSanitizer. Server-side Markdown → safe HTML; used by comments, public profile, CV about field. |
| `ClaimsPrincipalExtensions.cs` | One-line role helpers (`IsAdmin()`, `IsRecruiter()`, `IsRecruiterOrAdmin()`) used across every API file. |

### 5.5 `CvHub/Services/` — app services & seeders

| File | What it does |
|---|---|
| `EmailSender.cs` | `IEmailSender<ApplicationUser>` for Identity's confirmation/reset emails. Sends via SMTP (MailKit, STARTTLS, 15s timeout) when `Smtp:*` config exists; otherwise **logs the link** — so the whole email flow works end-to-end in dev without a mail server, and send failures never break sign-up/sign-in. |
| `IdentitySeeder.cs` | Runs at every startup. Creates roles Admin/Recruiter/Candidate, the admin account, three **demo accounts** (`recruiter@cvhub.local / Recruiter123!`, `candidate@cvhub.local / Candidate123!`, `candidate2@cvhub.local / Candidate123!`), and the six **built-in "Me" attributes** (First/Last Name, Location, Photo, Headline, About) — built on the same engine but flagged `IsBuiltIn` so recruiters can't delete or rename them. |
| `DbSeeder.cs` | Rich demo data, idempotent (safe every startup): 9 library attributes (with tuning examples — e.g. URL attributes carry a `^https?://\S+$` regex), 16 tags, 4 positions (2 Public, 2 Restricted with a Years-of-Experience ≥ threshold filter), full profiles for both candidates, 6 projects, pinned Info sections, 4 CVs (2 published, 2 draft) with pre-selected projects, sample comments and a like. This is what makes the app look alive the moment you sign in. |
| `AttributeLibraryServer.cs` | **Server** implementation of `IAttributeLibrary` (§3) — DB queries for the picker (search / recent / pinned), resolving the current user from the auth state provider. |
| `DiscussionServiceServer.cs` | **Server** implementation of `IDiscussionService` — delegates to `CommentCommands`, resolving the poster's identity from auth state (candidates can't be spoofed because the user id never comes from the client). |
| `AppClaimsPrincipalFactory.cs` | Custom claims factory: adds a `display_name` claim (shown in the header — "Hello, Carl") and an `is_blocked` claim for blocked users. |

### 5.6 `CvHub/Components/` — UI

**Root & layout**

| File | What it does |
|---|---|
| `App.razor` | The HTML shell for every page. Reads theme/language **cookies server-side** and bakes them onto `<html>` (`dark`, `lang-es`) — so the correct theme renders with **zero flash** and no JS dependency. Also emits the **Cloudinary config** as `data-cloud-name`/`data-upload-preset` attributes (the browser uploads images directly to Cloudinary; our server never sees image bytes). Loads CSS via `@Assets` (fingerprinted URLs), Blazor runtime, and the vendored libs. |
| `Routes.razor` | The router: `AuthorizeRouteView` (route-level `[Authorize]` enforcement) → `MainLayout`, with `RedirectToLogin` for anonymous hits on protected pages. |
| `_Imports.razor` | Razor usings inherited by all components. |
| `Layout/MainLayout.razor` | Sticky header: brand, **full-text search box on every page** (spec), theme + language toggles (plain links to `/prefs/*` — behavior in `app.js`), user greeting from the `display_name` claim, logout form (with the antiforgery token + `returnUrl` handling that fixed the 400-on-logout bug), and `NavMenu`. Footer + `@Body`. |
| `Layout/NavMenu.razor` | Role-aware nav links: Positions (everyone), Attributes (Admin/Recruiter), Users (Admin), Profile (authenticated). |
| `Layout/ReconnectModal.razor` (+ `.css`, `.js`) | The "connection lost — reconnecting" overlay when a Server circuit drops; auto-retry with UI states. |

**Pages**

| File | What it does |
|---|---|
| `Pages/Home.razor` | The landing page (static SSR): **5 stat cards** (CVs in 24h, total positions, candidates, recruiters, submitted CVs — computed in two aggregate queries, no N+1), **latest 8 positions** table, **top-5 popular** positions by published-CV count, and the **tag cloud** — tag links are role-aware (recruiters search CVs, candidates search positions). |
| `Pages/Error.razor`, `Pages/NotFound.razor` | Production exception page (dev exception page comes from `UseDatabaseDeveloperPageExceptionFilter`) and the 404 page. |

**Shared widgets** (`Components/Shared/`)

| File | What it does |
|---|---|
| `AttributeEditor.razor` | The universal **value editor** — given a `CvFieldDto` it renders the right input for each of the 8 attribute types (text, textarea/Markdown, image via `ImageUploader`, number, date, period pair, checkbox, dropdown) and invokes `ValueChanged` after each change. Used identically on the profile (autosave) and the CV page (in-place edit). |
| `AttributeEditorModal.razor` | The Admin/Recruiter dialog for creating/editing an **AttributeDef**: name, category (predefined list), type, description, dropdown options — plus the type-aware **tuning fields** (min/max length + regex for text types, min/max for numeric). |
| `PositionEditor.razor` | The big position editor dialog: title/company/level/access, the **template builder** (add attributes from `AttributePicker`, mark Required, set Section headings, reorder), **access-rule editor** (attribute + type-aware operator + value), project tags with autocomplete, `MaxProjects` cap. |
| `ProjectEditor.razor` | Project dialog: name, period, Markdown description, tags with autocomplete (suggestions from `/api/tags/suggest`). |
| `ProjectCard.razor` | Read-only project display card used in profile/public-profile/CV project lists. |
| `CvFieldValue.razor` | Read-only rendering of one CV field per type (images render, Markdown renders, periods show "start – present", empty shows "—"). |
| `MarkdownDisplay.razor` | Small wrapper that takes a raw Markdown string and renders it as sanitized HTML via `MarkdownRenderer`. |
| `LanguageSwitcher.razor` | EN/ES switch markup (links to `/prefs/lang/*`). |
| `RedirectToLogin.razor` | Bounces anonymous users from protected routes to `/Account/Login`. |

**Account / Identity** (`Components/Account/`) — the ASP.NET Core Identity UI, Blazor-ified and
Tailwind-themed to match the site:

| File | What it does |
|---|---|
| `IdentityComponentsEndpointRouteBuilderExtensions.cs` | The minimal-API endpoints behind the Identity pages: `POST /Account/PerformExternalLogin` (social login), `/Account/Logout`, and the **passkey** option endpoints (WebAuthn creation/request JSON). |
| `IdentityRevalidatingAuthenticationStateProvider.cs` | Revalidates the security stamp of connected circuits **every 30 minutes** — and this is also where blocking takes effect: a user with `IsBlocked` fails revalidation and loses their live session. |
| `IdentityRedirectManager.cs` | Identity helper that redirects from within Razor components (handles the prerendering caveat). |
| `IdentityNoOpEmailSender.cs` | Placeholder sender for scaffolding paths that don't need real mail (real mail goes through `Services/EmailSender`). |
| `PasskeyInputModel.cs`, `PasskeyOperation.cs` | Models for the WebAuthn flows. |
| `Shared/ExternalLoginPicker.razor` | Renders Google/Facebook buttons **only for providers actually registered** (asks Identity for the external schemes) — the login page shows nothing when providers aren't configured. |
| `Shared/ManageLayout.razor`, `Shared/ManageNavMenu.razor` | The sub-navigation shell for the `/Account/Manage/*` pages, styled like the rest of the site. |
| `Shared/PasskeySubmit.razor` (+ `.js`) | Browser WebAuthn ceremony invocation. |
| `Shared/ProviderIcon.razor` | Brand SVGs for Google/Facebook. |
| `Shared/StatusMessage.razor` | The temp-data status banner used across account pages. |
| `Pages/*.razor` (18 files) | The full Identity page set: `Register`, `Login` (with per-field validation feedback), `ExternalLogin` (social callback + confirmation), `ConfirmEmail`, `ConfirmEmailChange`, `ForgotPassword`(+Confirmation), `ResetPassword`(+Confirmation, InvalidPasswordReset, InvalidUser), `ResendEmailConfirmation`, `Lockout`, `AccessDenied`, `LoginWith2fa`, `LoginWithRecoveryCode`. |
| `Pages/Manage/*.razor` (15 files) | The **Manage** area (profile settings): `Index` (profile data), `Email`, `ChangePassword`, `SetPassword`, `TwoFactorAuthentication`, `EnableAuthenticator` (TOTP QR), `GenerateRecoveryCodes`, `Disable2fa`, `ResetAuthenticator`, `ExternalLogins`, `Passkeys`, `RenamePasskey`, `PersonalData`, `DeletePersonalData`. |

### 5.7 `CvHub.Client/` — the WebAssembly runtime

| File | What it does |
|---|---|
| `CvHub.Client.csproj` | Client project (Blazor WebAssembly SDK). |
| `Program.cs` | The WASM DI container: authorization core, **authentication-state deserialization** (the server serializes the auth state into the page so WASM knows who you are without a round-trip), base-addressed `HttpClient`, and the **WASM implementations** of `IAttributeLibrary` / `IDiscussionService`. |
| `Services/AttributeLibraryApi.cs` | `IAttributeLibrary` over HTTP (`/api/attributes/search|recent|pinned`). The browser's cookie authenticates automatically. |
| `Services/DiscussionServiceApi.cs` | `IDiscussionService` over HTTP (list/post comments). |
| `Components/AttributePicker.razor` | The **library picker** (`InteractiveAuto`): debounced (250 ms) prefix search, category filter, "recently used" section while searching, pinned-attributes shown with "✓ on profile" and disabled. Works on both runtimes via `IAttributeLibrary`. |
| `Components/DiscussionPanel.razor` | The **live discussion board** (`InteractiveAuto`): loads posts through `IDiscussionService`, then a JS timer (`cvLive.start`) calls back into .NET (`[JSInvokable] Poll`) every **2 seconds** — append-only chronological updates within the spec's 2–5 s window. Recruiter/admin views link author names to public profiles. |
| `Components/ImageUploader.razor` | The **image dropzone** (`InteractiveAuto`): drag-drop or file-picker; JS does the actual upload straight to **Cloudinary** (unsigned preset) and reports the URL back via `JSInvokable` callbacks; the URL lands in the attribute's `ImageUrl`. No image bytes ever touch our server (spec rule). |
| `wwwroot/appsettings(.Development).json` | Logging config for the WASM host (no secrets — kept in git deliberately). |
| `_Imports.razor` | Client-side Razor usings. |

### 5.8 `CvHub.Shared/` — contracts

| File | What it does |
|---|---|
| `Enums.cs` | The 8 `AttributeType`s, `CvStatus`, `PositionAccess`, the predefined `AttributeCategories`, and `BuiltInAttributeNames` (the six "Me" attributes). |
| `FilterOps.cs` | Client-side mirror of the operator rules: which operators are valid per attribute type + their display symbols (`>`, `≥`, `is true`, …). |
| `Dtos.cs` | The DTO records crossing the server↔WASM boundary: `FilterOperator`, `CvFieldDto` (one CV row incl. per-type values + `Version`), `ProjectDto`, `CvViewDto` (the composed CV). |
| `AttributeLibrary.cs` | `IAttributeLibrary` interface + `AttributeRow` record (§3 pattern). |
| `DiscussionService.cs` | `IDiscussionService` interface + `PostDto`. |
| `I18n.cs` | **The i18n system**: `I18n.T(key)` returns the string for `I18n.Current` ("en"/"es", set per-request from the cookie in `Program.cs`). All UI strings are dictionary keys; both languages live in this file. Only UI chrome is translated — user data stays as authored. |
| `_Imports.razor` | Shared usings. |

### 5.9 JavaScript, CSS, migrations, docs

| File | What it does |
|---|---|
| `CvHub/wwwroot/js/app.js` | Thin interop layer: `cvTheme` (dark-class toggle), `cvMarkdown` (marked + DOMPurify — used by client-side previews), `cvLive` (the 2s polling timers for discussions), `cvUtil.debounce`, and `cvPrefs` — intercepts the theme/language toggle links in the **capture phase**: theme applies instantly + cookie, language sets the cookie then does a full reload so the server re-renders every string (Blazor's enhanced navigation would mangle the `/prefs/*` redirect). |
| `CvHub/wwwroot/js/upload.js` | `cvUpload`: drag/drop + file-input wiring, reads the Cloudinary config from `<html data-*>`, POSTs the file **directly to Cloudinary** (unsigned preset) and returns `secure_url` to .NET. Demo fallback (when config is missing): a deterministic `placehold.co` URL so the demo still works — and by design that fake URL never touches our server. |
| `CvHub/wwwroot/lib/marked.min.js`, `lib/purify.min.js` | Vendored: Markdown parsing + HTML sanitizing for the client-side preview path. |
| `CvHub/wwwroot/favicon.png` | Favicon. |
| `CvHub/Migrations/*` (10 files) | 5 changesets: **InitialPostgres** (all tables + raw SQL creating the `tsvector` generated columns and GIN indexes), **PositionMaxProjects**, **AddCvAttributeVersions**, **MapPositionSearchVector** (model alignment so EF targets the indexed column — intentionally a no-op against the DB), **AddAttributeTuning** (the min/max/regex columns). |
| `Tailwind/app.css` | Already covered in §5.1 — the print/PDF-mode styles live here. |

---

## 6. App flow — the important journeys

### 6.1 What happens when you open any URL

```
Browser → nginx (prod) → Kestrel
  ├─ UseResponseCompression          (br/gzip — incl. .wasm payloads)
  ├─ I18n middleware                 (cv_lang cookie → I18n.Current for this request)
  ├─ UseAntiforgery                  (Blazor form posts need it)
  ├─ MapStaticAssets                 (css/js/lib — fingerprinted, immutable cache)
  ├─ Feature APIs                    (Map*Api — /api/* JSON endpoints)
  └─ MapRazorComponents              (SSR the page HTML; attach Blazor runtime if the page is interactive)
```

- **Static SSR page** (Home, Search): full HTML rendered and done. No WebSocket.
- **InteractiveServer page** (Profile, CV, Positions…): SSR HTML arrives first, then `blazor.web.js`
  opens a SignalR circuit and the page re-renders interactively. All events/clicks travel the circuit.
- **InteractiveAuto components** inside a page: on the *first* visit they run on the server circuit;
  the WASM bundle downloads in the background; on *later* visits they boot in the browser and call the
  JSON APIs directly (cookie-authenticated).

### 6.2 Visitor → candidate journey (the main demo path)

1. **Home `/`** — stats + latest positions + tag cloud. Browsing positions read-only is allowed for
   anonymous users (spec).
2. **`/Account/Register`** → Identity creates the user (email confirmation flow works via `EmailSender`;
   in dev the link is logged instead of emailed). Optional **Google/Facebook** buttons when configured.
3. Sign in → redirected to **Home**, header shows "Hello, {display_name}".
4. **Profile `/profile`** → the four tabs:
   - **Me**: six built-ins. Click ✏️ → inline editor (`AttributeEditor`); every change marks the field
     dirty; the **6s autosave timer** POSTs the batch to `/api/profile/attributes/{id}` → tuning
     validation → xmin conflict check → upsert `AttributeValue` → returns the new version.
   - **Info**: "＋ Add from library" opens the **AttributePicker** (prefix search/recent/pinned) →
     picking pins the attribute (a `ProfileAttribute` row) and it joins the tab, already editable.
   - **Projects**: `ProjectEditor` → `/api/profile/projects` (tags upserted, autocomplete from DB).
   - **CVs**: fills as positions are applied to.
5. **Positions `/positions`** (read-only for candidates — the table is recruiter/admin's management view,
   but position *details* are public per spec) → open **`/positions/{id}`**:
   - If Restricted: candidate sees whether they qualify. "Create CV" button → `CvFactory.CreateAsync`
     (access re-checked server-side; one CV per position).
6. **CV `/cv/{id}`** — composed live by `CvComposer`: template fields + master values + pre-selected
   matching projects. Candidate edits **in place** (same editor + same master-value write-back; the
   attribute auto-pins to their profile). Required-empty fields get a red ring.
7. **Publish** — button stays disabled until `MissingRequired == 0` (server re-verifies on click).
8. Now recruiters can see it: **`/positions/{id}` → CVs tab** (list + likes), **CSV export**,
   **PDF export** (owner/admin), and the candidate appears in **search** (`scope=cvs`).

### 6.3 Recruiter journey

1. Sign in as `recruiter@cvhub.local` → sees **Attributes** (library CRUD) and **Positions**.
2. **Create position** → `PositionEditor`: pick attributes from the library (Required flags, Section
   headings), add access rules (e.g. Years of Experience ≥ 3 — operator list is type-aware), project
   tags, MaxProjects.
3. **Duplicate** a position (deep copy) instead of re-entering templates.
4. Open a position → **Template** tab (review), **CVs** tab (published CVs, like/unlike, CSV export),
   **Discussion** tab (live board, author names link to `/public-profile/{id}`).
5. Recruiters **cannot** edit candidate profiles — the API returns 403 and the UI hides those controls.

### 6.4 Admin journey

Admin = recruiter powers **+** ownership everywhere: opens `/profile/u/{userId}` and edits the
candidate's profile **as if it were their own**; `/admin/users` lists accounts with block/unblock,
role add/remove (yes, including demoting themselves), delete (guarded against FK data). Blocked users
are kicked on the next 30-minute security-stamp revalidation.

### 6.5 The autosave + optimistic locking sequence (worth narrating in the demo)

```
keystroke → AttributeEditor.ValueChanged → ProfilePage.OnAutoSave (mark dirty)
   ↓ every 6 s: SaveTick()
   → batch-load dirty defs + existing values (2 queries, no per-field DB hits)
   → AttributeValueRules.Validate          → violation: banner + skip (409-shaped)
   → xmin check                             → changed elsewhere: adopt server state + banner
   → upsert values, SaveChanges             → DbUpdateConcurrencyException also → 409 path
```
The same validator + xmin handshake guards the JSON API path, so nothing bypasses the rules.

### 6.6 Photo upload flow (the Cloudinary one)

```
drop file → upload.js reads data-cloud-name/preset from <html>
   → POST https://api.cloudinary.com/v1_1/{cloud}/image/upload (unsigned preset)
   → secure_url → JSInvokable Uploaded(url) → UrlChanged → autosave stores ImageUrl
```
Our server: zero image bytes. (When config is absent, a `placehold.co` URL keeps the demo functional.)

---

## 7. Cross-cutting mechanisms

| Concern | How it's done | Files |
|---|---|---|
| **AuthN** | Identity cookies; Google/Facebook conditionally registered; passkeys + 2FA from Identity UI | `Program.cs`, `Components/Account/*` |
| **AuthZ** | Role checks on every API group + `[Authorize]`/`AuthorizeView` in UI; `AccessRules` for data-level position access | all `*Api.cs`, `AccessRules.cs` |
| **Blocking** | `IsBlocked` claim + session revalidation every 30 min | `AppClaimsPrincipalFactory`, `IdentityRevalidatingAuthenticationStateProvider` |
| **Optimistic locking** | PostgreSQL `xmin` mapped as row version on the 4 hot entities; version echoed by clients; 409 + server-state adoption | `ApplicationDbContext`, `ProfileApi`, `*Commands` |
| **Soft deletes** | Query filters; positions/attributes/CVs hide, never vanish | `ApplicationDbContext` |
| **Full-text search** | Generated `tsvector` + GIN index (raw SQL migration); `WebSearchToTsQuery` | `Migrations`, `SearchEndpoints` |
| **Markdown safety** | Markdig + HtmlSanitizer server-side; DOMPurify client-side | `MarkdownRenderer`, `app.js` |
| **i18n** | Cookie → `I18n.Current` per request → `I18n.T(key)` everywhere; EN/ES dictionaries | `I18n.cs`, `Program.cs`, `app.js` |
| **Theming** | Server-baked `dark` class from cookie (no flash); instant toggle via capture-phase JS | `App.razor`, `app.js`, `PrefsEndpoints` |
| **Performance** | Brotli/gzip (incl. WASM), `AsNoTracking` on read-only queries, aggregate queries instead of loops, indexes for hot paths | `Program.cs`, all read endpoints |
| **Config safety** | `appsettings*.json` git-ignored; deploy rsync excludes them; env-var overrides for connection string | `.gitignore`, `package.json`, `Program.cs` |

---

## 8. Demo script & likely questions

**Suggested 10-minute flow:**
1. Home as anonymous (public stats, positions readable) → login page (mention Google/Facebook + passkeys).
2. Sign in as **candidate** → walk Me/Info/Projects tabs; show the autosave indicator; edit a CV field
   in place on a CV; show the red-ring + disabled Publish on an incomplete CV, then complete + publish.
3. Open a Restricted position as **recruiter** in a second browser → CVs tab, like a CV, CSV export,
   discussion live-update (two windows side by side).
4. As **recruiter**: create a position with an access filter, duplicate it; show the attribute library
   and tuning fields (set Max on a numeric attribute, then violate it as candidate → banner).
5. As **admin**: `/admin/users` — change roles, block someone, show them failing revalidation.
6. Close with: PDF export (QR visible in print preview), public profile via a discussion author link,
   badges panel, and the dark-mode + Spanish toggle.

**Likely Q&A:**

- *"Where does the CV content live?"* — Nowhere as a copy. `Cv` rows are pointers; `CvComposer`
  composes from `AttributeValue` (single master value) + `PositionAttribute` (template) every render.
- *"Two users edit the same field — what happens?"* — Last save wins, but not silently: xmin version
  mismatch → 409 → the client adopts server state and shows the "changed elsewhere" banner.
- *"Why are some components in a separate project?"* — InteractiveAuto: the Client project holds the
  WASM implementations; per-host service interfaces (`CvHub.Shared`) let the same component run on
  server or WASM.
- *"How do you prevent SQL injection / XSS?"* — EF parameterizes everything; Markdown is sanitized
  twice (HtmlSanitizer server, DOMPurify client); raw SQL exists only in migrations.
- *"What happens to a CV if access is revoked?"* — It's hidden, not deleted (query filter + amber
  banner on the page).
- *"Is anything slow?"* — Hot reads are `AsNoTracking` and index-backed; position search uses the GIN
  index; badges and stats are single aggregate queries; compression is on for APIs and WASM.

---

*Generated as part of the CvHub final project — see `docs/SPEC.md` for the original requirements this
code implements.*
