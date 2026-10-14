using Microsoft.Data.SqlTypes;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// One kind of question, across clients (#499): questions that mean the same thing, grouped by their meaning. The name is the member nearest the
/// group's centre, a real (masked) question, so no call is spent naming groups. Counts and outcomes say how often it is asked, by how many clients, and
/// how often the answer had evidence — the gaps to fix.
/// </summary>
public class ChatQuestionGroup
{
    public long ChatQuestionGroupId { get; set; }

    /// <summary>The member question nearest the group's centre, masked.</summary>
    public string CanonicalQuestion { get; set; } = string.Empty;

    /// <summary>The mean of the members' (unit-length) embeddings, re-normalised.</summary>
    public SqlVector<float> Centroid { get; set; }

    public int AskedCount { get; set; }
    public int ClientCount { get; set; }
    public int AnsweredWithEvidenceCount { get; set; }
    public int AnsweredFromIndexCount { get; set; }
    public DateTime FirstAskedUtc { get; set; }
    public DateTime LastAskedUtc { get; set; }
    public List<ChatQuestionEntry> Entries { get; set; } = [];
}

/// <summary>One question asked in the chat, masked, with its group and how it was answered. Never holds the client's unmasked text.</summary>
public class ChatQuestionEntry
{
    public long ChatQuestionEntryId { get; set; }

    /// <summary>The user's chat message (unique: each question is entered once).</summary>
    public long ChatMessageId { get; set; }
    public long RequestId { get; set; }
    public long ClientId { get; set; }

    /// <summary>The question with the company's names, its people, identifiers, case numbers and amounts replaced by placeholders.</summary>
    public string MaskedText { get; set; } = string.Empty;

    public SqlVector<float> Embedding { get; set; }
    public long ChatQuestionGroupId { get; set; }
    public ChatQuestionGroup? Group { get; set; }

    /// <summary>The answer cited at least one source.</summary>
    public bool AnsweredWithEvidence { get; set; }
    /// <summary>The answer came from the document index (#506), with no model call.</summary>
    public bool AnsweredFromIndex { get; set; }

    public DateTime AskedUtc { get; set; }
    public DateTime ProcessedUtc { get; set; }
}
