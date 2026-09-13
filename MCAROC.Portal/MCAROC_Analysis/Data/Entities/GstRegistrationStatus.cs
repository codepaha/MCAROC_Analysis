namespace MCAROC_Analysis.Data.Entities;

/// <summary>Central GST registration-status interpretation for every report surface.</summary>
public static class GstRegistrationStatus
{
    /// <summary>
    /// A registration is active only when it has not been cancelled and its source status explicitly
    /// says active. Missing or unfamiliar status values must not be represented as active.
    /// </summary>
    public static bool IsActive(GstRegistration registration) =>
        registration.CancellationDate is null && IsActiveStatus(registration.Status);

    /// <summary>
    /// Determines whether a source status explicitly represents an active registration. The check
    /// deliberately excludes values such as <c>Inactive</c>, which contain the word "active".
    /// </summary>
    public static bool IsActiveStatus(string? status) =>
        status is not null
        && status.Contains("active", StringComparison.OrdinalIgnoreCase)
        && !status.Contains("inactive", StringComparison.OrdinalIgnoreCase);
}
