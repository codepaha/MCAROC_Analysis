namespace MCAROC_Analysis.Services.PreLoginReports;

public class PreLoginConcurrencyException(string message) : Exception(message);

public class PreLoginReportNotFoundException(string message = "Assignment not found.") : Exception(message);

