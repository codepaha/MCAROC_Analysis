# Local reconciliation fixtures (not committed)

`SourceReconciliationTests` ingests two **real** `.xls` workbooks end-to-end and asserts that the
Layer-0 `SourceRows` table balances against what the Excel reader sees — i.e. that ingestion drops
nothing. These workbooks contain real company data and are **deliberately git-ignored**
(`Fixtures/workbooks/` is in `.gitignore`), so the test `Assert.Skip`s on CI and anywhere the files
aren't present.

To run it locally, drop the two workbooks here:

```
MCAROC.Portal/MCAROC_Analysis.Tests/Fixtures/workbooks/roc.xls      <- the MCA/ROC report
MCAROC.Portal/MCAROC_Analysis.Tests/Fixtures/workbooks/charge.xls   <- the detailed charge report
```

(For the COASTAL PROJECTS LIMITED sample: `U45203OR1995PLC003982.xls` → `roc.xls`,
`U45203OR1995PLC003982-charge.xls` → `charge.xls`.)

Then `dotnet test --filter FullyQualifiedName~SourceReconciliation`.
