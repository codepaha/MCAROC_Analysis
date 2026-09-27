# Company-master name normalization (identity resolution I1, #293)

`CompanyMasterRecords` carries three columns derived from `Name` by `CompanyNameNormalizer`, so a name lookup
is an index seek instead of a scan of ~3.7M rows:

| Column | Example (`M/s. Sharma & Sons Pvt. Ltd.`) |
|---|---|
| `NameNormalized` | `SHARMA AND SONS PRIVATE LIMITED` |
| `NameCore` | `SHARMA AND SONS` |
| `EntityForm` | `Private` |

Indexed as `(RecordType, NameNormalized)` and `(RecordType, NameCore)`, and read by the name resolver (#294).

## Word index (`CompanyNameTokens`, #294)

One row per (word of `NameCore`, identifier), for Company and LLP rows. It lets the resolver reach a
reordered or misspelt name through the words that did survive. It is a plain table, not SQL Server
Full-Text Search, so it works on every edition including Express. The words are built by splitting the
stored `NameCore` on spaces (`STRING_SPLIT`, database compatibility level 130+), so no normalization rule is
repeated in T-SQL.

- Rebuilt in full after `--backfill-names` and after a bulk import (both change `NameCore` across the table).
- Refreshed by the MCA sync for exactly the rows each promotion batch inserts or updates, in the same
  transaction.

## Who writes them

- **Bulk import** (`Tools/ImportCompanyMasterData`): computed for every row loaded. The import rebuilds the
  table from a copy, so it also recreates both indexes and renames them in its swap.
- **MCA sync promotion** (`CompanyMasterDeltaService`): computed when rows are staged, copied on promotion.
  A row whose `Name` is unchanged but whose derived columns are missing or stale is also refreshed.
- **Backfill** (below): for rows already in the table. It also rebuilds the word index.

## Deploying

1. Apply the migrations `AddCompanyMasterNameNormalization` (adds nullable columns and the two indexes; no
   data is rewritten, so it is quick even on the full table) and `AddIdentityResolution` (the empty
   `CompanyNameTokens` and `IdentityResolutions` tables).
2. Backfill the existing rows:

   ```
   dotnet run --project MCAROC.Portal/Tools/ImportCompanyMasterData -- --backfill-names [--connection="..."]
   ```

   Works in primary-key batches of 50,000, so it can be stopped and re-run safely, then rebuilds the word
   index. It exits with code 0 only when no row is left missing a derived column, and prints the remaining
   count otherwise.
3. Optionally measure: `dotnet run --project MCAROC.Portal/Tools/EvaluateNameResolution -- --out=report.md`
   (read-only; see below).

## When `CompanyNameNormalizer.Version` changes

Every stored value may now differ from what the code would compute. Recompute all rows:

```
dotnet run --project MCAROC.Portal/Tools/ImportCompanyMasterData -- --backfill-names --all
```

Rows whose values are already correct are read but not rewritten.

## Offline evaluation harness

`Tools/EvaluateNameResolution` builds a case set and reports, per threshold, how often each strategy would
auto-select and how often that selection is right (precision with a 95% Wilson interval, and recall). It
never writes anything. Case categories:

- **Labelled:** real requests whose company was ingested: the name the requester typed, and the CIN/LLPIN
  it turned out to be.
- **Synthetic** (a deterministic 1-in-1000 sample of the master):
  - `CaseAndPunctuation`, `SuffixVariant` and `Ampersand` should be fully absorbed by normalization.
  - `WordOrder` and `Typo` deliberately are not; they show what the resolver's ranking must add.
- **Duplicate:** names shared by two or more rows. No answer is correct, so the only right outcome is to
  abstain.
- **CompanyLlpTwin:** a company and an LLP with the same core name. Asked for the company, the strategy
  must not pick the LLP.

It compares three strategies: today's AutoFetch prefix search, the normalized lookup and the #294 resolver.
The resolver's auto-select stays disabled until precision on the auto-selected subset reaches ≥ 99.5%,
judged on the lower bound of the confidence interval (plan §5A.4). Its `AutoSelectThreshold` and
`MinimumMargin` should then be set from this report.

## Resolver settings (`Resolve` section of appsettings)

| Key | Default | Meaning |
|---|---|---|
| `Resolve:AutoSelect:Enabled` | `false` | Off: the resolver only recommends; a person confirms. |
| `Resolve:AutoSelectThreshold` | `0.95` | Minimum top score for auto-select. |
| `Resolve:MinimumMargin` | `0.05` | Minimum lead over the runner-up. |
| `Resolve:AllowSoleActive` | `false` | Among same-name twins, allow the only Active one to be chosen. |
| `Resolve:SuggestionFloor` | `0.5` | Below this top score the name counts as not found. |
| `Resolve:MaxCandidates` | `10` | Candidates kept on each resolution record. |

Every resolution is recorded in `IdentityResolutions` with its candidates, scores, reasons, the settings in
force and the algorithm version.
