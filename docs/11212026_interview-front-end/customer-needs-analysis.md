# Customer needs analysis for demo plants

November 22, 2026. Input for the interview front end: what Timebase prospects
and customers actually ask for, so a simulated demo tells *their* story.

## Evidence

- **Meetings:** 46 recorded meeting summaries (Fathom) from the last 12 months,
  43 with content, about 37 end-user organizations. Almost all were led by Flow
  sales or solutions staff (discovery calls, demos, proof-of-concept check-ins).
  Summaries only; transcript search is not enabled on the account.
- **By industry:**
  - food, beverage and CPG ~17;
  - packaging and paper ~7;
  - discrete, automotive, metals and building products ~7;
  - chemicals and materials ~5;
  - water, energy and utilities ~4;
  - mining and aggregates 3;
  - pharma and medical devices 3.
- **CRM (HubSpot):** 233 deals since November 2025. Lost reasons are empty on
  all of them, and the industry property cannot be queried, so CRM added
  nothing beyond company size.
- **Privacy:** patterns only. No people, no company names, no long quotes.
  Counts are the number of meetings that mention a point.

## Who is in the room

| Role | Meetings | What they ask about |
| --- | --- | --- |
| IT/OT and enterprise data architects | ~20 | Plant vs central vs cloud, no copying of data, how it fits their message broker, Fabric/Databricks/Snowflake, security, licensing across sites |
| Controls and automation engineers | ~12 | Ignition integration, tag and UDT import, OPC UA/MQTT, historian performance, history when tag paths are renamed |
| CI, OEE and operations excellence leads | ~10 | Standard OEE and downtime across sites, KPI logic in one governed place, replacing Excel |
| Reliability and maintenance | ~6 | CMMS work orders (usually SAP PM) linked to telemetry, repeat failures, which assets a failure affects |
| Process and batch engineers (chemicals, dairy, pharma) | ~6 | Golden batch, cycle time, batch comparison, tracing bad batches back |
| Executives and plant leadership | ~6 | Business outcomes, not architecture; often a separate leadership demo |
| Security and AI governance | ~6 | Which LLM provider is approved, air-gapped networks, where credentials are stored |
| Quality and compliance | ~4 | GxP and 21 CFR 11 audit trails, Cp/Cpk, lot traceability |

## KPIs they want answered

| KPI or question | Meetings |
| --- | --- |
| OEE and availability | 12 |
| Downtime reasons and root cause | 10 |
| Golden batch, cycle time, batch comparison | 5 |
| Scrap and waste, including startup waste | 5 |
| Maintenance planning, repeat failures, MTTR | 5 |
| Operator and technician certification compliance | 5 (often the AI showcase) |
| Genealogy and lot traceability | 3 |
| Schedule feasibility, energy | 2 each |
| Cp/Cpk, CIP optimization, labor effectiveness | 1 each |

## Systems they run (shape the demo's tables and tags)

- **ERP:** SAP (14), often only reachable through a staging database or API.
- **CMMS:** SAP PM, Maximo, MaintainX, or an unnamed CMMS (about 12 in all).
- **MES:** usually homegrown, otherwise Kanoa or Syncade.
- **Quality:** LIMS (3); paper or Excel checklists (4).
- **Historians:** Canary 9, PI/AVEVA 8, Ignition SQL historian 4, Siemens 3, Wonderware 2, plus others.
- **SCADA:** Ignition 22, by far the most common.
- **Downstream:** a message broker or UNS 10, Power BI 7, Databricks 6, Snowflake 6, Fabric 4.

## What landed and what drew pushback

**Landed:**
- An AI agent answering cross-system questions, for example "which uncertified
  technicians closed which work orders?" (4).
- Building the model automatically from Ignition UDTs, tag lists or P&IDs (5).
- Root cause in minutes instead of hours (2).
- "If this MCC trips, which lines go down?" (2).
- A demo built on the prospect's own process. Twice the follow-up was "send us a
  P&ID and we'll build a simulator".

**Pushback:**
- AI governance or a blocked LLM provider (6).
- Poor data quality on their side (5).
- Cost of scaling to many sites (5).
- Air-gapped networks (4).
- No staff to own it (4).
- Beta maturity or a bug during the demo (4).

## Storylines grounded in the evidence

| # | Storyline | Industries | Roles | KPIs |
| --- | --- | --- | --- | --- |
| 1 | Labeler jams make orders late; nobody links the CMMS ticket to the speed drop | F&B, CPG | Plant manager, maintenance, CI | OEE availability, on-time delivery, MTTR |
| 2 | The same bearing fails three times; the PM keeps getting deferred | All | Reliability, maintenance | MTBF, repeat failures |
| 3a | An uncertified technician closed a safety-critical work order | Chemicals, auto, CPG | EHS, quality, maintenance, executive | Compliance % |
| 3b | An operator ran a line or SKU without a current work-instruction certification | Chemicals, discrete, CPG | EHS, quality, operations, executive | Compliance %, first-pass yield |
| 4 | Startup waste after changeovers | Packaging, bakery | CI, operations | Scrap %, changeover time |
| 5a | Golden batch drift in a reactor (jacket fouling) | Chemicals | Process engineer | Cycle time, yield |
| 5b | Syrup or mix batches drift and quality holds follow at the filler | Beverage, dairy | Process engineer, quality | Cycle time, first-pass yield, holds |
| 5c | Golden batch deviation under GxP with an audit trail | Pharma | Process engineer, quality, compliance | Yield, deviations, right-first-time |
| 6 | A quality hold traced to a shift and a sensor | F&B, pharma | Quality | Holds, first-pass yield, genealogy |
| 7 | An MCC trips: what goes down, which orders are at risk | All | Plant manager, electrical | Downtime cost |
| 8 | Downtime is logged by hand; the reason codes are wrong | CPG, medical devices | CI/OEE | OEE, top losses |
| 9 | Upstream/downstream imbalance causes defects | Packaging, metals | Process engineer | Throughput, defect rate |
| 10 | Root cause of a costly event in minutes | Metals, paper | Leadership, process engineer | Event cost, time to cause |
| 11 | Energy per unit spikes on one line | Beverage, CPG | Plant manager, sustainability | kWh per unit |
| 12 | Mine fleet and plant cut off from each other | Mining | Operations | Throughput, availability |

**Engine coverage:**
- **Today:** 1, 2, 6, plus the late-orders part of 1.
- **This plan adds:** 3a and 3b (certification, phase 2) and 5a, 5b and 5c (golden batch, phases 3–4).
- **Partial:** 4 and 8 (changeovers and minor stops exist, but there is no scrap log and no manual-entry error).
- **Later:** 7, 9, 10, 11, 12.

## What this means for the interview

1. Ask first about the audience: role, industry, and the one or two costly
   problems.
2. Then ask what they run. SAP and Ignition are the most likely answers, so the
   tables and tags should feel like their systems.
3. Ask about AI and network constraints early.
4. Offer storylines from the library rather than inventing them. Say plainly
   when a storyline is not supported yet.
5. Finish with a demo guide: which question to ask, in which tool, in what
   order. A plant alone does not make the demo mean something to the audience.
