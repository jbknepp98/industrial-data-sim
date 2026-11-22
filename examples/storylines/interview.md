# Demo plant interview script

Shared by every Flow Data Simulator front end: the Claude Code skill
(`skills/flow-data-simulator`) and the web interview. It is written as
instructions to the interviewer.

The storylines come from `storylines.json`. They were distilled from 43
recorded customer meetings
(`docs/11212026_interview-front-end/customer-needs-analysis.md`). The plant
exists to make the audience see their own problem, so start with the people
who will watch, not with machines.

## Ground rules

- The stages below are a checklist, not a strict order. If the user starts
  with the audience and a storyline, follow them. Cover every stage before
  composing the model, and ask about systems and constraints before approval.

- Ask a few questions at a time, in plain language. Offer a sensible default
  and skip anything the user has already said.
- Use the audience's words for KPIs: "late orders", "repeat failures",
  "holds", not engine field names.
- Only promise what the library marks `available`. For `planned` or `later`
  storylines, say so plainly ("that one isn't in the simulator yet") and offer
  the storyline named in its `closest` field. If `closest` is null, say nothing
  fits yet. Record the request in the demo guide's "Asked for, not in the
  simulator yet" section. In the web interview it is also saved with the
  needs profile.
- Evidence counts: quote the `evidence` of the storyline you are discussing.
  It counts the customer meetings that raised it.
- Map the user's words to library ids yourself: "bottler" or "brewery" is
  `food-beverage`; "snacks" or "personal care" is `cpg`; "maintenance lead" is
  `maintenance`; "reliability engineer" is `reliability`; "OEE" or "CI lead"
  is `ci-oee`; "EHS" or "safety" is `ehs`. When unsure, ask.
- Keep plants small: at most 2 lines and the minimum staff the story needs.
- Never build or run without explicit approval of the preview.

## 1. Audience

- **Who will watch?** Roles: plant manager, maintenance or reliability, CI/OEE,
  quality, process engineer, IT/OT architect, controls, executive, EHS.
  - If leadership will be there, plan one business outcome they can repeat
    afterwards.
  - Architects care where it runs and how data flows, not about breakdowns.
- **Industry and product:** what they make, and how (packaging lines, batch
  vessels or both).
- **The one or two problems that cost them the most.** Ask for the problem and
  for a recent example. In the meetings, people asked most about OEE and
  downtime root cause, repeat failures, quality holds, certification
  compliance, and golden batch and cycle time.

## 2. Their systems

- **ERP and CMMS:** SAP and SAP PM are the most common; ask whether it is
  another one.
- **MES, LIMS, batch records:** often homegrown, or kept in paper and Excel.
- **Historian and SCADA:** Ignition is by far the most common. Canary and PI
  are common historians.

The answers shape the demo guide's notes ("these tables stand in for your SAP
PM work orders"). They don't change the plant.

## 3. Constraints

- **AI and network.** Tell them:
  - the plant runs locally;
  - its rows go to the `flow_data_sim` Postgres database and its tags to a
    Historian Dataset (on the web page, a new one named after the plant,
    created when they approve it);
  - any AI in the demo uses the provider already approved for Gage.

  Ask whether the audience is sensitive to AI or to cloud use.
- **History:** how many days of history the demo needs. Leave it to the
  storylines unless the user asks: each has its own length. On the web page the
  plant's own Dataset keeps 365 days, so any history up to that is kept. When
  building with the CLI into an existing Dataset, the history must fit that
  Dataset's purge age.
- **When:** demo dates are relative to now. If the demo is later, run the plant
  continuously, or rebuild it nearer the day.

## 4. Storylines

A storyline's `requires` list says which areas its base plant must include
(Quality, Shipping). The bases already include them, so combining storylines
adds no extra lines or staff.

- **Rank the storylines:** industry match first, then the roles in the room,
  then the KPIs they named.
- **Propose one to three** in their own words: the pitch, who it speaks to, and
  the moment the audience will see.
- **Check the fit.** Storylines must share a base plant. Some run on several
  (their `Bases`), so check every chosen storyline's list before you say two
  clash; compose picks a base they all run on. If they share none, offer a
  choice.
- **Rare stories:** if what they want is rare in the evidence, say how many
  meetings raised it. It helps them judge whether it will land.

## 5. Plant

- Start from the storylines' base plant. Ask only what makes it feel like
  theirs:
  - plant, line and product names;
  - the simulation name, in PascalCase, for example `AcmeNuggs`.
- Change rates, staff or schedules only if the story or the user requires it.
  Respect the limits.
- Then compose the model, validate it, and fix every reported error.

## 6. Preview and approval

- Show the preview, with the headline numbers: orders, on-time percentage,
  breakdowns, tickets, holds, and how long each line spends in each state.
- For each storyline, show where and when it happens, for example: "the
  labeler's vibration starts climbing at 06:40 on Thursday; the ticket opens
  at 14:45".
- Ask for approval, or for what to change. Repeat until it's approved.

## 7. Build, run and the demo guide

- After approval, build and run the plant.
- Then produce the demo guide from `demo-guide.md`. Fill in:
  - the audience and their problems;
  - the storylines in the order to show them;
  - for each talk-track step: the tool, the exact tags or tables (with the real
    tag root and table prefix), the question to ask, and what the audience
    should see, with the real times.
