using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Models;

/// <summary>Cases per forum, split by whose side the company is on, each as pending / disposed / status unknown: the summary the
/// litigation report opens with. Every forum is listed, so a forum with nothing found reads as "searched, none".</summary>
public sealed class LitigationForumSummary
{
    public sealed record Counts(int Pending, int Disposed, int Unknown)
    {
        public int Total => Pending + Disposed + Unknown;
        public static Counts operator +(Counts a, Counts b) => new(a.Pending + b.Pending, a.Disposed + b.Disposed, a.Unknown + b.Unknown);
    }

    private readonly Dictionary<(LitigationInstitution Forum, SideColumn Side), Counts> _cells = [];

    /// <summary><see cref="Both"/> is a known result (the company is named on both sides), kept apart from <see cref="NotDetermined"/>, where the side
    /// could not be told.</summary>
    public enum SideColumn { Against, By, Both, NotDetermined }

    public static SideColumn ColumnOf(LitigationCompanySide side) => side switch
    {
        LitigationCompanySide.AgainstCompany => SideColumn.Against,
        LitigationCompanySide.ByCompany => SideColumn.By,
        LitigationCompanySide.Both => SideColumn.Both,
        _ => SideColumn.NotDetermined
    };

    public void Add(LitigationInstitution forum, LitigationCompanySide side, LitigationCaseStatusBucket status)
    {
        var key = (forum, ColumnOf(side));
        var current = _cells.GetValueOrDefault(key, new Counts(0, 0, 0));
        _cells[key] = status switch
        {
            LitigationCaseStatusBucket.Pending => current with { Pending = current.Pending + 1 },
            LitigationCaseStatusBucket.Disposed => current with { Disposed = current.Disposed + 1 },
            _ => current with { Unknown = current.Unknown + 1 }
        };
    }

    public Counts Cell(LitigationInstitution forum, SideColumn side) => _cells.GetValueOrDefault((forum, side), new Counts(0, 0, 0));

    public Counts ForForum(LitigationInstitution forum) =>
        Enum.GetValues<SideColumn>().Aggregate(new Counts(0, 0, 0), (sum, side) => sum + Cell(forum, side));

    public Counts ForSide(SideColumn side) => Enum.GetValues<LitigationInstitution>().Aggregate(new Counts(0, 0, 0), (sum, f) => sum + Cell(f, side));

    public Counts Total => Enum.GetValues<SideColumn>().Aggregate(new Counts(0, 0, 0), (sum, side) => sum + ForSide(side));

    /// <summary>The "not determined" column is only worth showing when a case's side could not be told.</summary>
    public bool HasNotDetermined => ForSide(SideColumn.NotDetermined).Total > 0;

    /// <summary>The "both sides" column is only shown when the company is named on both sides of some case.</summary>
    public bool HasBoth => ForSide(SideColumn.Both).Total > 0;
}
