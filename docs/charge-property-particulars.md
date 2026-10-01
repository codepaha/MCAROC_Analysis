# Charge property particulars: normalisation

The charge report's **PROPERTY PARTICULARS** column (column M in the Probe "Open/Satisfied Charges in Details"
sheets; `RocChargeEvent.PropertyParticulars`) is one run-on paragraph. A single cell often mixes several assets,
for example:

- a mortgaged office unit with its parking;
- the land it stands on, and the larger parcel that land forms part of;
- a hypothecation clause;
- another group company's current assets.

The portal now shows a **normalised reading** of it in the charge drawer ("Property charged (normalised)") and as a
one-line summary in the dossier annexure. The raw wording is always kept beneath it.

## Two readers

| | Rules (`PropertyParticularsNormalizer`) | Gemini (`PropertyParticularsAi`) |
|---|---|---|
| When | At render time, for every charge, including already-ingested data | After ingestion, once per distinct text |
| Output | One merged reading per text | **Separate properties**: one per unit, parcel, project, asset block or other entity's assets |
| Cost | Free | One Flash-Lite call per distinct text |
| Can invent a value? | No, it only extracts matches | No: every value is checked against the source (below) |

The drawer and dossier use a **completed Gemini extraction when one exists for the text, and the rules reading
otherwise**. They never mix the two for one text.

### Fields

For each property:

- asset class (Immovable / Movable) and kind (premises, land, building/project, parking, current assets, book debts,
  plant & machinery, intangibles, shares, uncalled capital, vehicle);
- the owning entity, when it is another named company;
- unit number, floor and building;
- project;
- areas in **both sq m and sq ft**, with their basis (carpet / built-up / FSI / plot / land / larger parent parcel).
  A value the text states is used as written; only a missing unit is converted;
- parking count and space numbers;
- CTS / survey / plot / gat / khasra numbers (New/Old), with ranges such as `52/1–52/17` compressed;
- locality, village, taluka, district, city, state and PIN.

## Grounding: why Gemini cannot invent a value

`PropertyParticularsAi.Validate` (prompt version 2.0) checks every returned value against the source text, aligned to
word and number boundaries. A value can never be "found" by gluing the end of one word to the start of the next
("75 Church" is not unit "5C").

- **Each property quotes its own clause** (`sourceText`). The quote must occur verbatim in the source, and the clauses
  of different properties must not overlap. Otherwise the property is dropped.
- **Checked inside that property's own clause**, so two properties in one paragraph can never trade them:
  - **Unit number and floor.**
  - **Areas.** The number must be written **followed by the stated unit**: 100 sq m is not 100 acres. An equivalent
    restatement is checked the same way.
  - **Parking count.** The number must be written as a parking count ("17 parking spaces"), never borrowed from a
    space number ("B-43") or an area.
  - **CTS/survey/plot numbers.** The **whole identifier**, suffixes included, must be written ("52/17XYZ" is not
    "52/17"). The only exception is a number inside a range the clause **states explicitly** ("52/1 to 17" gives
    52/1 … 52/17).
- **Checked against the whole source** (word-aligned): building, project, owner and location fields. The PIN must be
  six standalone digits, not an area or a comma-grouped amount.

A value that fails is **dropped and recorded** in `RejectedFieldsJson`, and the drawer states how many were dropped.
Invalid JSON, or no surviving property at all, fails the extraction; the rules reading is then used.

## Scheduling, storage, switches

- **Storage:** `PropertyParticularsExtractions`, keyed by `(TextHash, PromptVersion)`. The hash is taken over the
  whitespace-normalised text plus the property-type column. Identical wording, which recurs across a charge's
  modifications and across requests, is extracted once. A request only ever looks up hashes of its own charges'
  texts.
- **When it runs:** it is scheduled after each ingestion. It also runs lazily when an older request's details page
  is opened, which backfills data ingested before this feature.
- **The worker:** it is lease-fenced and retries with backoff (`MaxAttempts`). On startup, recovery re-wakes pending
  rows.
- **Config:**

  ```json
  "PropertyParticularsExtraction": { "Enabled": true, "MaxAttempts": 3, "TimeoutSeconds": 60 }
  ```

  Nothing is scheduled when `Enabled` is false **or** when `GoogleCloud:ProjectId` / `CredentialsPath` are not
  configured.
- **Dossier caches:** a version derived from the request's completed extractions is part of both the in-memory
  dossier key and the rendered PDF's file name. A dossier cached before an extraction completed is rebuilt, never
  served stale.
- **Prompt changes:** bump `PropertyParticularsAi.PromptVersion`, and texts get re-extracted as their requests are
  ingested or opened.

## Not yet

The structured records are not yet fed into the address matchers (`ChargedPropertyAddressRules`,
`LitigationOrderAddressMatcher`, #191). Those still compare the raw paragraph. Matching on unit, CTS number and PIN
per property is the natural next step.
