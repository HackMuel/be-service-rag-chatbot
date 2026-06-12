namespace be_service.Services;

public static class RetrievalModes
{
    public const string Blocked            = "blocked";
    public const string Semantic           = "semantic";
    public const string ExactNik           = "exact-nik";
    public const string ExactMaintenanceCode = "exact-maintenance-code";
    public const string ExactDate          = "exact-date";
    public const string ExactName          = "exact-name";
    public const string ExactNameOvertime  = "exact-name-overtime";
    public const string Audit              = "audit";
    public const string AuditGeneral       = "audit_general";
    public const string Sop                = "sop";
    public const string SopGeneral         = "sop_general";
    public const string Profile            = "profile";
    public const string ItInfra            = "it_infra";
    public const string EmployeeByDivision = "employee_by_division";
    public const string EmployeeByShift    = "employee_by_shift";
    public const string EmployeeByStatus   = "employee_by_status";
    public const string EmployeeByPosition = "employee_by_position";
    public const string OvertimeByApproval = "overtime_by_approval";
    public const string OvertimeByDivision = "overtime_by_division";
    public const string MaintenanceByStatus    = "maintenance_by_status";
    public const string MaintenanceByLocation  = "maintenance_by_location";
    public const string MaintenanceByTechnician= "maintenance_by_technician";
    public const string MaintenanceByEquipment = "maintenance_by_equipment";
    public const string StructuredEntity   = "structured_entity";
}
