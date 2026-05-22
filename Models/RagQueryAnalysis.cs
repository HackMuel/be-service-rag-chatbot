namespace be_service.Models;

public class RagQueryAnalysis
{
    public string Question { get; set; } = string.Empty;
    public AnswerLevel AnswerLevel { get; set; } = AnswerLevel.Unknown;

    public string Nik { get; set; } = string.Empty;
    public string MaintenanceCode { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;

    public bool IsSopQuery { get; set; }
    public bool IsProfileQuery { get; set; }
    public bool IsAuditQuery { get; set; }
    public bool IsEmployeeQuery { get; set; }
    public bool IsOvertimeQuery { get; set; }
    public bool IsMaintenanceQuery { get; set; }

    public string SopKeyword { get; set; } = string.Empty;
    public string ProfileKeyword { get; set; } = string.Empty;

    public string Division { get; set; } = string.Empty;
    public string Shift { get; set; } = string.Empty;
    public string EmployeeStatus { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;

    public string MaintenanceStatus { get; set; } = string.Empty;
    public string Approval { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Technician { get; set; } = string.Empty;

    public string PersonKeyword { get; set; } = string.Empty;
    public bool LooksLikePersonName { get; set; }

    public bool IsPolicyQuestion { get; set; }
    public bool IsAccessQuestion { get; set; }
    public bool IsPermissionQuestion { get; set; }
    public bool RequiresGroundedLlm { get; set; }
    public string TargetRecordType { get; set; } = string.Empty;
    public string GenericRecordType { get; set; } = string.Empty;
}
