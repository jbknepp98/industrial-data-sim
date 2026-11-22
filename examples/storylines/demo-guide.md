# Demo guide: {PlantName}

**Audience:** {Roles}, {Industry}.

**What costs them the most:** {Problems}.

**Their systems:** {Systems}. The simulated tables stand in for these.

## The plant

| | |
| --- | --- |
| Simulation | `{Simulation}` |
| Tag root | `{TagRoot}` |
| Tables | `{prefix}<purpose>` in `flow_data_sim` |
| History | from {StartUtc} to now, still running |
| Historian Dataset | {Dataset} |

Connect's read-only login is user `flowsim_reader`. The password is in
`~/flow-data-simulator/config/postgres.env`.

## Storylines, in the order to show them

<!-- Repeat this block for each chosen storyline. -->

### {StorylineNumber}. {StorylineTitle}

{Pitch}

It speaks to: {StorylineRoles}. KPIs: {StorylineKpis}.

**The moment:** {Moment}, for example "Labeler 202's vibration starts climbing
on Thursday at 06:40; the line stops at 14:40; the ticket opens at 14:45."

<!-- Repeat for each talk-track step. -->

{StepNumber}. **{Tool}:** open {TagsOrTables}. Ask: *{Ask}*. They should see:
{Expect}

## Asked for, not in the simulator yet

{UnmetRequests}. For example: "MCC trip impact (later); showed the
labeler-jam storyline instead".

## Close

Restate the problem in their words, and the number that changed. For example:
"three late orders this week, all on Line 1, all while Labeler 202 was down".
Then ask what that costs them today.
