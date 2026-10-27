# Floor plans v3: plan

Status: **draft for review**. Nothing is built yet.
Base: `main` @ `efd193c` (PR #29 floor plans v2 + PR #30 Midnight Command).
Reviewed twice against the code on 2026-10-28 (second verdict: ready with fixes); all corrections are folded in below.

## Why

Kjeld's feedback on v2 (27 Oct 2026):

1. **Placing is hard.** You drag from a long list in the side panel, and the people rows are misaligned.
2. **The map is too small.** On load it is zoomed out, with empty bands above and below the drawing. Names and desktop tags only appear after you zoom in.
3. **The PDF is unreadable at crowded tables.** Names and tags overlap, for example in Finance and Engineering on AMS Floor 1.
4. **"Team" is the wrong word.** A coloured cluster of desks can be a team, part of a department, or something else.
5. **Keep:** *Fit* and *Select area*.

Confirmed decisions:

| Topic | Decision |
|---|---|
| Name for the coloured cluster | **Zone**: one flexible label (a team, a department, a project, anything) |
| Number on the map | The **desktop's asset tag** (e.g. `AMS-DT-001`), not a desk number |
| PDF crowding | Hermes proposes; see §4 |
| Placing | Clicking a desk opens a **desk view** where you find and add the person, desktop and screens; see §2 |

## 1. Zone instead of Team

- **Wording.** Every screen, hint, message, the PDF, the docs and the testing guide say *Zone*. Examples: "Set zone for 6 desks", "New zone…", "No zone", the "Zones" box, and the "Zone" column in the PDF.
- **Code rename.** `FloorTeam` becomes `FloorZone`, `TeamService` becomes `ZoneService`, and `Workspot.TeamId` becomes `ZoneId`.
- **CSS classes.**
  - The map colour classes become `zc-0` … `zc-7`, a prefix the drawing importer can never produce. The importer prefixes every drawing class with `fp-` (`FloorplanCss.cs:16`), so a class called `zone-1` in a drawing would collide with `fp-zone-*`.
  - The side-panel classes `.wp-team-*` become `.wp-zone-*`.
- **Migration.** It is hand-written: a generated one could drop and recreate the table, which would lose the data.
  - Route: rename the model, run `migrations add`, then replace the generated Up/Down with renames. Indexes use `RenameIndex`; the primary and foreign keys use `sp_rename`.
  - This is a stated exception to the rule that the integrator regenerates migrations (`development.md:30`).
  - It renames the table `FloorTeams` to `FloorZones` and the column `Workspots.TeamId` to `ZoneId`.
  - It also renames `PK_FloorTeams`, `FK_FloorTeams_Floors_FloorId`, `IX_FloorTeams_FloorId_Name`, `IX_Workspots_TeamId` and `FK_Workspots_FloorTeams_TeamId`, so the database matches the EF snapshot.
  - Migrations run on **SQL Server only**. SQLite (the demo and the tests) builds the schema with `EnsureCreated`.
  - A new upgrade test, on its own database, is skipped when `ATS_TEST_SQLSERVER` isn't set. It migrates up to `FloorPlanDesks` by name, adds zones and desks, applies the rename, then checks that names, colours and desk links are intact.
  - The CI model check only proves the model and snapshot match. The snapshot doesn't store constraint names, so this test is what proves the database renames.
  - Testers need to delete `demo.db` once. The PR description says so.
- **Audit.** The ledger is append-only, so old `team.*` rows stay as they are, and new rows use `zone.*`.
  - The audit page gets a small display mapping, so both show as "Zone …".
  - Action filter: `zone*` also matches `team*`. This catches `team.remove`, which is logged against `Floor`.
  - Entity filter: the dropdown merges `FloorTeam` and `FloorZone` into one *Zone* entry.
- **Compatibility.** Old `?focus=team:ID` links and the `team` search result kind keep working as aliases of `zone:`.
- **Behaviour is unchanged:**
  - A desk is in at most one zone.
  - A table, part of a table, or desks spread across tables can share a zone.
  - Names are unique per floor, and there are 8 colours.
  - The zone name is already drawn once per zone per room. Only the outline is drawn per table.

## 2. Desk view: click a desk to fill it

**This builds on code that is already there:**
- `AssignmentService.SearchAsync`, reached through `OnGetPlaceAsync`. It searches people and assets, says where something is placed now and why it can't be placed. No page uses it yet.
- The one-seat panel (`OnGetSpotAsync`).

**Clicking a desk** does two things for every role that can see the floor (IT, Facility and Managers). Managers get it read-only.
1. The map zooms to the desk's table and keeps the desk highlighted.
2. The side panel switches to the **desk view**. The panel becomes sticky next to the map, as the map already is.

```
┌ Desk ─────────────────────────────── ✕ ┐
│ Finance · zone Finance                 │
│ PERSON                                 │
│ [🔍 Type a name…                     ] │  ← focused at once
│   NOT PLACED YET                       │
│   Gabi Demoson   Accountant · AMS      │
│   PLACED ELSEWHERE                     │
│   Gabi Dummett   Floor 2 · Sales –     │
│                  move here             │
│ DESKTOP   [🔍 Tag, serial or model…  ] │
│ SCREENS   AMS-SC-001 Dell P2422H [Release]
│           [＋ Add a screen…          ] │
│ ⚠ No desktop at this desk              │
│ [← Previous desk]   [Next free desk →] │
└────────────────────────────────────────┘
```

**How it works**
- Each slot is a search box with suggestions as you type. Pick with the arrow keys and Enter, or click.
- **Search is reworked**, which is more than adding a filter:
  - Today `SearchAsync` returns nothing below 2 characters (`AssignmentService.cs:226`), and it cuts results by name before it knows where things are (`:233-246`).
  - The new search takes a *kind* (person, desktop or screen) and the desk's id.
  - With an empty box it lists the not-yet-placed items at this site, as `PaletteAsync` already does.
  - Items already at this desk are marked "here", not "move here".
  - Inactive people are marked, as today.
  - People from another site can be placed (they visit); assets from another site can't.
- Results are ranked in SQL:
  1. Unplaced items at this site come first.
  2. Then items placed elsewhere, shown with where they are and "move here".
  3. Assets from another site are listed but can't be picked, with the reason. The service already refuses them (`AssignmentService.cs:316`).
  4. Desktops and screens are limited to types with that floor-plan role, as today.
- **A filled slot** shows a card with the name or tag and the job title or model, plus **Replace** and **Release**. *Release* is the word the app already uses.
  - Replace applies only to the person and desktop slots; screens are just added.
  - Replace is one new service method with one transaction: close the current occupant, close the incoming item's old row, then insert the new one. It doesn't wrap `PlaceAsync`, which opens its own transaction.
  - Audit: a `release`, then an `assign` with `MovedFrom`.
- **Keyboard flow for seating a table:**
  1. Type a name and press Enter. Focus moves to the desktop field.
  2. Type a tag and press Enter. Focus moves to the screen field.
  3. Press Enter on an empty field to jump to **Next free desk**: the same table first, then the same room.
- **Saving.** Every change saves at once, and the map refreshes without losing the zoom, as drops do today. A "Saved" toast confirms it.
  - There is **no Undo button**. A released placement can't be reopened, so undo would really be a new placement that can conflict.
  - The toast names what changed ("Released AMS-DT-004 from this desk"), because Facility can't see the audit log.
- **The "Place on the map" list stays as a second way to place** (decided 2026-10-28). It moves below the desk view and is tidied up:
  - Rows are aligned the same way: icon, name, then a muted second line.
  - It is collapsed by default ("Place from a list").
  - Dragging from it onto a desk, or clicking an entry with a desk selected, still works.
  - After any save the map refreshes and restores the selection (`site.js:861`); the desk view re-opens from that restored selection.
- **Moving a placed item stays.** You drag the card from the desk view onto another desk, as you drag from the seat panel today. A drag on the map itself keeps panning.
- **Multi-select stays.** Shift-click, clicking a table and *Select area* still show the selection box, which is used for zones and seat settings. The desk view is for one desk at a time.
- **Managers** (read only) see the same view without the search boxes. The role and site checks already in the services are kept.
- **Esc**:
  - Inside a search box, Esc first closes its suggestion list.
  - Otherwise, in order: cancel print-area mode, clear search highlights, close the desk view, clear the selection.
  - Full screen is an in-page mode (a CSS class, not the browser's Fullscreen API), so Esc can leave it last.

**No loan-based suggestions** (decided 2026-10-28). The desk view never uses loan data, so the Facility role's boundary is untouched.

## 3. A bigger map that is easier to move around

- **The canvas fills the screen.** Its height is the window minus the top bar and page header, at least 480 px. The side panel scrolls on its own.
  - A **⤢ Full screen** button hides the panel and the header.
  - In full screen, the desk view opens as an overlay on the right.
- **Fit fits the drawing, not its paper.** *Fit*, and the first view, zoom to the box around rooms and desks, ignoring the empty margins in the SVG.
- **Floating map controls.** Zoom in, zoom out, *Fit*, *Select area* and full screen move into a button group in the map's top-right corner, with the zoom level next to them. The search box and the *Zones* and *Names* toggles stay above the map.
- **Navigation:**
  - Double-click a room to zoom to it.
  - The mouse wheel zooms around the pointer and dragging pans, as today. The touchpad isn't treated separately, because a browser can't reliably tell it apart from a mouse wheel.
  - + / − / 0 already zoom (`site.js:1199`). New: arrow keys pan when the map has focus.
- **Names and tags at Fit:** see §4.
- **Unchanged:** the legend, the *Free desks* and *To check* highlights, and the rule that the drawing stays light in dark mode.

## 4. Labels that never overlap (map and PDF)

**Today:**
- The server writes each label: the name, cut at 22 characters, and the tag, plus where it sits and which side of the desk it goes.
- JS only scales the labels and hides them all below 58 px of desk spacing.

**After:**
- **The server keeps writing labels** as a fallback for no-JS and for the existing `FloorplanRendererTests`. It also writes the full name and tag as data attributes, as it does now.
- **A JS label engine** (`site.js`) improves on that fallback on both the live map and the print page:
  1. **Space per desk:** once per page load, each desk gets a label slot in drawing units on its outside, away from its table. The slot is as wide as the gap to the neighbouring desks on that side.
  2. **Shrink:** when the zoom changes enough, the text is re-fitted, down to a minimum of 7 pt on paper and 10 px on screen. Text isn't re-measured on every animation frame.
  3. **Shorten:**
     - Names step down from *Gabi Demoson* to *Gabi D.* to *GD*.
     - Tags drop the site prefix, so `AMS-DT-001` becomes `DT-001`, because the header already names the site.
     - The full text is always in the tooltip, the desk view and the PDF list.
  4. **Callout:** if even the shortest form doesn't fit, the desk gets a small number, and a key box in the room's free space (or beside the drawing) lists the names. On AMS Floor 1 this shouldn't be needed.
  5. **Final check:** a last pass hides lower-priority text where two boxes still touch. The tag goes before the name. Callout numbers are never hidden.
- **CSP.** The engine only sets SVG attributes and `element.style` properties. It never uses `setAttribute('style', …)` or inline `<style>`, both of which `style-src 'self'` blocks.

**PDF:**
- **Page 1** prints the chosen area (whole floor, current view, selected desks, or a drawn area) with the engine's labels.
- **Paper size:**
  - A4 landscape stays the default.
  - When the engine needs callouts on A4, the print page shows "This floor is easier to read on A3", with an **A3** switch.
  - The switch adds a class to the sheet that selects a second named `@page` rule (A3 landscape).
  - It also changes the sheet's size: today it is fixed at 1047 px wide with a 600 px map, sized for A4 (`site.css:936,941,954`). The A3 class gets its own sizes, and the label engine's 7 pt minimum follows the active paper scale.
  - The "landscape" hint on the print page (`Print.cshtml:16`) names the chosen size.
  - It never switches to A3 automatically: printing an A3 PDF on an A4 printer shrinks it back down anyway.
  - Safari has weak support for named page sizes, so the hint tells Safari users to pick A3 in the print dialog themselves.
- **Next pages** hold the desk list grouped by zone, then room: person, desktop, screens, note. Free desks are collapsed to one line per room, e.g. "Sales: 12 free".
- **Header:** as today.

**Why not "one page per room"?**
It doubles the page count for every print and splits zones that span rooms. *Draw an area* and *Selected desks* already cover printing one room large.

## 5. Delivery

Three PRs, in order. Each needs green CI (SQLite build and test with `-warnaserror`, SQL Server plus the migration check, Windows build) and is left open for Kjeld's review. Nothing is merged automatically.

| PR | Contents | Tests and docs |
|---|---|---|
| **A: Zone rename** | §1 | Rename the existing team tests. New SQL Server upgrade test (skipped without `ATS_TEST_SQLSERVER`). Audit display mapping and filter test. Tests for the `team:` focus alias and the `wp-zone-*` classes. Update `development.md` and `testing-guide.md`. |
| **B: Map and labels** | §3, §4, the PDF, and the dense demo floor | `FloorplanRendererTests` keep passing (fallback labels). Recheck tests that count demo floors. Page tests for the A3 switch and the grouped list. Browser checks on AMS 1, AMS 2, NYC 1 and a new dense demo floor, at A4 and A3 and on screen at Fit, at desktop and phone width, in light and dark. Screenshots go in the PR. |
| **C: Desk view** | §2 | Existing palette tests keep passing (`AssignmentTests.cs`, `AssignmentPagesTests.cs`). New handler tests: kind filter, empty-box list and ranking, "here" marking, other-site assets shown but not pickable, other-site people placeable, move from elsewhere, atomic replace with its audit rows, next free desk, manager read-only. The release endpoint already exists and is only re-tested. Browser walkthrough: seat a full table using only the keyboard, and once more from the list. Fix the row alignment in the list. Update `testing-guide.md` and `development.md` for the desk view. |

The demo seed gets a **dense floor** (AMS Floor 3, "Open plan": 8-seat tables, long names) in PR B, so overlap problems can be seen and tested. The demo only seeds an empty database, so testers delete `demo.db` after PR A and after PR B.

All three PRs touch `site.js`, so they land one after another.

## 6. Decisions (Kjeld, 2026-10-28)

1. **The list stays** as a second way to place, below the desk view.
2. **No loan suggestion** at all.
3. **A3 switch:** yes. A4 stays the default, and the print page offers A3 when a floor is crowded.

## Out of scope

Desk numbers, desk booking, a link from zones to AD departments, and import or replacement of drawings (unchanged).
