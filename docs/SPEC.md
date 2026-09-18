# Course Project — CV Management System (Spec)

## Assignment

### Course Project

Use this:
- .NET: C# (required), Blazor web app with **auto render interactivity mode**; of course, **PostgreSQL**
- CSS framework **tailwind**.
- You can use any other libraries, components or even frameworks (but not replace specified above).

There are no limitations in the area of architecture or used services. For example, you are not required to have a separate full-featured application server separated from Web server, or have a lot of things on the client, or use microservices, etc.; you may approach this whatever way you want.

It's recommended to use the simplest and the safest approach to the persistence, namely relational database, e.g. PostgreSQL.

**Idea.** You have to implement a Web application for CV management system (different positions, experience, skills, etc.). Users define "positions" with the set of attributes. Position is a kind of template for CVs; other users fill their CVs for the related positions with specific values. Three of the most important features are customisable positions with arbitrary attributes, reusable library of attributes, and automatic CV generation.

E.g., a Recruiter creates a "Business Analyst" position, adds the "English Level" dropdown attribute and the number-valued field "GPA". Some candidates fill out English levels and GPAs in their profiles and generate CVs tailored for the given position. Then recruiters browse the CVs to find appropriate candidate.

**Warning.** If your app have N buttons (view/edit/delete in each record), your result will be graded -20%. Use toolbars, or animated "appearing" context actions, etc.

This is forbidden: table rows with a column of [Edit][Delete] buttons.

This is OK: selection checkboxes, a per-page [Delete] toolbar action, sortable column headers, inline "appearing" context actions.

Positions, as well as CVs, should be displayed in the table views. The usage "tile" / "gallery" representation will be graded -20%. You have to use table representation for positions and CVs, not gallery or tiles.

Every page provides access to full-text search via the top header. Implement a convenient consistent navigation in your app.

### Overview

The system is a web-based recruitment platform that allows Candidates to maintain reusable professional profiles and generate tailored CVs for positions managed by Recruiters.

The platform is built around three "killer-features":

1. **Reusable Attribute Library** – attributes can be defined once and reused across multiple positions and CVs;
2. **Customizable Position Templates** – Recruiters can build position-specific CV templates using attributes from the library;
3. **Automatically Generated CVs** – CVs are assembled dynamically from candidate profile data and position requirements.

The platform supports three user roles: **Candidate**, **Recruiter**, **Administrator**.

### Authentication

Non-authenticated users may: register an account; sign in; browse available positions in read-only mode; view public statistics (e.g. "10 new CVs created in the last 24 hours").

Non-authenticated users may not: create or edit positions, browse or create CVs, post comments or like CVs, access personal pages.

Users can authenticate using social login providers (at minimum, the platform must support two — Google and Facebook recommended).

### Roles

**Candidates** can:
- Manage their personal profile;
- Select and fill attributes from the attribute library;
- Manage project descriptions;
- View positions available to them;
- Create and edit CVs for positions they are allowed to access (if Candidates lose the access, the filled out CVs are hidden);
- Participate in discussions.

Candidates may access only their own profile and CVs.

All Recruiters share responsibility for the same set of positions. Any Recruiter can modify any position — there is no concept of position ownership. Also, all Recruiters manage the shared pool of attributes.

**Any Recruiter can:**
- Create positions; duplicate existing positions; edit positions; delete positions;
- Configure access rules; manage position templates;
- Manage the attribute library;
- View candidate CVs in read-only mode (using full-text search, accessing CVs through the position or through the personal pages);
- Participate in discussions; like CVs.

**Administrators** have unrestricted access, view all pages as if they were the owner, can edit any candidate profile / CV / position, perform all Recruiter and Candidate actions, and manage users (viewing, blocking, unblocking, deleting, assigning/removing roles). Administrators may remove their own Administrator role.

### Personal Profile

Each authenticated user has a personal profile page. Only the profile owner and Administrators may edit or view the full profile. Recruiters cannot access profile editing and only see CV data as read-only page.

The profile consists of four sections:

- **Me** — mandatory built-in attributes. These attributes always exist and cannot be removed. E.g., First Name, Last Name, Location, Personal Photo. These attributes should be built on the same "engine" (e.g., can be added to position template), but cannot be removed by Recruiters.
- **Info** — user-selected attributes from the Attribute Library. Candidates may add/remove attributes from the library and fill values for those attributes.
- **Projects** — candidates maintain a list of projects: Name, Period (date range), Description (Markdown), Technology Tags (autocomplete for previously entered tags; nice tag UI component). Projects can be added, edited, removed.
- **CVs** — displays all CVs created by the Candidate. At most one CV per position. Existing CVs can be edited or deleted. New CVs may be created only for positions accessible to the Candidate. Each CV entry acts as a link to the CV page.

### Auto-Save

Personal profile pages support automatic saving:
- Changes are tracked locally;
- Changes are saved every 5–10 seconds;
- Saving must not occur on every keystroke;
- The auto-save mechanism must use **optimistic locking**.

The system must use **optimistic locking**. Each save operation (for attributes, positions and auto-saves of profiles):
- Sends the current version number;
- Updates the record if the version matches;
- Returns a new version number;
- Fails if the version has changed.

The client must handle version conflicts gracefully.

### Killer Feature #1: Attribute Library

The Attribute Library enables reusable structured data across profiles, positions, and CVs.

All Recruiters can: create attributes, edit attributes, delete attributes.

Each attribute contains:
- **Category** — one from a predefined list;
- **Name** — globally unique attribute name;
- Some kind of description;
- **Attribute data type**.

Examples of categories: Certification, Domain Knowledge, Personal Information, Soft Skills.

Supported attribute types:
- String (single-line plain text)
- Text (Markdown)
- Image (external cloud storage, by drag-n-drop)
- Numeric
- Date
- Period (date range)
- Boolean (checkbox)
- One of many (dropdown)

Because the library may become large, attribute selection must support: lookup by prefix, recently used attributes, category filtering.

### Killer Feature #2: Positions

Positions serve as customizable CV templates. All Recruiters manage a shared list of positions; a Recruiter may create a blank position or duplicate an existing one, edit any position, delete any position.

Each position contains:
- Basic information, incl. Title and Short Description;
- **Access Rules** — the position may be either public (accessible to all authenticated users) or restricted using filters;
- Attributes selected from the Attribute Library;
- Project tags (for selecting relevant projects) as well as maximum number of projects included in the generated CV.

Examples of access rules:
- "IELTS Score" numeric value is > 7.0;
- "Remote Work" checkbox is checked;
- "Presentation Skills" value in dropdown = "Advanced".

Available filter operators depend on attribute type. Each position provides the list of the CVs created from that position (accessible by Recruiters and Administrators only).

Candidates may create CVs only for positions they are authorized to access. If a Candidate loses access, already created CVs aren't deleted — they are hidden in UI.

It would be nice to have additional position attributes like "Company", "Level" (Junior/Middle/Senior/C-level) etc. to filter/sort positions in tables — not crucial.

### Killer Feature #3: CV Generation

CVs are generated automatically from:
- Candidate profile data (undeletable attributes like name);
- Selected library attributes (values fetched automatically from the attributes set by the Candidate);
- Candidate projects (filtered).

When a user edits an attribute in a CV, the attribute is added to the profile (if necessary; some may be already filled out from the profile); attributes not present in the profile are empty by default.

The generated CV should: be professionally formatted; be divided into clearly structured sections; display only relevant information (attributes specified in the position and filtered projects).

When a Candidate opens own CV: attributes are pre-filled automatically from the profile; missing info can be entered manually in-place. Each attribute in the CV can be edited in place (only one common master value per attribute is stored in the profile). If value is empty it's highlighted in red.

Editing an attribute in a CV modifies the original profile value.

Recruiters can only view CVs in rendered read-only mode; empty values highlighted in red. They cannot directly modify candidate CVs (Administrators can).

It's necessary to have track states for CVs: a separate "Publish" action available only if all the attributes are filled out. This action makes the CV visible to Recruiters.

### Discussions

Each position contains a Discussion tab. Discussion posts include: author name, timestamp, text content (Markdown-formatted). If page is viewed by Recruiters, the author name links to the user's public profile view. Posts are displayed in chronological order; new posts are always appended to the end; posts cannot be inserted between existing posts. Updates should appear for all active viewers within 2–5 seconds (WebSockets, polling, or whatever).

### Likes

Each CV supports likes. Only Recruiters may like CVs. One Recruiter may give at most one like to a specific CV. A Recruiter may remove their like. The total number of likes is displayed in the CV lists and in search results.

### Main Page

The main page contains:
- Latest Positions (table of most recently created or updated positions);
- Most Popular Positions (top 5 ranked by number of submitted CVs);
- Tag Cloud with technology tags (linked to CVs for Recruiters or positions for Candidates);
- Statistics (number of CVs created in last 24 hours, total positions, total Candidates, total Recruiters, total submitted CVs).

### More

- Two UI languages: English and Spanish. User selects the language; choice is saved. Only UI is translated — user content is not.
- Two visual themes: light and dark; choice is saved.
- CSS framework: Tailwind; responsive design including mobile phones.
- ORM (e.g. Entity Framework).
- Full-text search engine (external library or native DB features).

**DON'Ts:**
- Don't perform full database scans using raw `SELECT *` queries;
- Don't upload images to your web server or database;
- Don't execute database queries inside loops;
- Don't add buttons in the table rows.

- "Is it possible to use the X library?" — Yes, yes to all — remember my choice.

### Optional Requirements (only if all core requirements are fully implemented)

- Generate printable documents in PDF with QR codes linked back to the app;
- Form authentication with email confirmation as an alternative to social login;
- "Badges"/"achievements" system (e.g. "10 projects", "5 CVs", "25 likes"), nice SVG panel on profile page, downloadable;
- Additional attribute "tuning" options: text length limits, regex validators, numeric ranges, etc.;
- Export CVs for a given position to an aggregate CSV/Excel file for analysis.

### General directives

- Do not copy. Use libraries as much as possible — don't copy-paste code. Use ready-made components, libraries and controls (Markdown renderer, drag-n-drop image uploader, tag input, tag cloud, etc.). The less custom code, the better.
- Don't serialize CVs as JSON for storage. All CVs for a position must remain "compatible" — display them in a structured table, aggregate, sort, filter. Don't generate tables in the DB on the fly. Store attributes in one table and reference them from another; the relational database fits this perfectly.
- CV is generated for a position: Candidate selects a position and the CV is generated "automagically". The CV row is "stored as created" but content is looked up, not stored as a copy (except technical internal fields like created_by, id, etc.). If the accessibility filter changes, the CV may become hidden (both from the Candidate and Recruiters) — that's a UI thing, because content is generated from attributes and projects, not stored. If the user changes an attribute on the profile or in one of the CVs, it's changed everywhere.

## Implementation decisions (our build)

- **Stack**: .NET 10, C#, Blazor Web App (Auto interactivity), PostgreSQL 16, EF Core 10 (Npgsql), Tailwind CSS v4, marked.js + DOMPurify (Markdown), dropzone-style image upload to Cloudinary (unsigned), Grid.js-free custom table with animated appearing context actions, Tagify-style tag input, QRCode.js for PDF mode (print CSS).
- **Architecture**: Vertical Slice Architecture — each feature slice owns its endpoints, handlers, DTOs and (shared-core) entities. `Features/Attributes`, `Features/Positions`, `Features/Cvs`, `Features/Profile`, `Features/Discussions`, `Features/Likes`, `Features/Home`, `Features/Admin`. Shared infra: `Data/` (AppDbContext), `Domain/` (entities), `Auth/`, `Services/`.
- **Persistence**: `attributes` table + `attribute_values` (single master value per user per attribute) + `position_attributes` join + `cv` rows (id, position_id, candidate_id, status, published_at, version...) + `cv_attribute_versions` join referencing the same `attribute_values` (content looked up, not copied). Projects & tags normalized. `tsvector` generated columns + GIN indexes for full-text search.
- **Optimistic locking**: PostgreSQL `xmin` system column mapped as concurrency token on profile attribute rows, positions, CVs; version returned to client after each save.
- **Social login**: Google + Facebook. Email/password form auth included as well.
- **Images**: Cloudinary unsigned upload (drag-n-drop), URL stored only.
- **Localization**: EN/ES via cookie; theme light/dark via cookie.
- **Live updates**: 2s polling for discussions.
- **Full-text search**: PostgreSQL `tsvector` + GIN, `websearch_to_tsquery`, global header search.
