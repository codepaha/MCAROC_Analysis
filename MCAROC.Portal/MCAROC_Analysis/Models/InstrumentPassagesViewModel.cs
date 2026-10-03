using MCAROC_Analysis.Services.PropertyParticulars;

namespace MCAROC_Analysis.Models;

/// <summary>Feeds <c>Details/_InstrumentPassages.cshtml</c>: the property passages quoted from one charge document.</summary>
public sealed record InstrumentPassagesViewModel(long RequestId, long DocumentId, ChargeInstrumentAiResult Result);
