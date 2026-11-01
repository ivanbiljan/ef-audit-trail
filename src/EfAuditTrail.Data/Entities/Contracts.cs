namespace EfAuditTrail.Data.Entities;

/// <summary>Housekeeping timestamps — stamped by the context on save and ignored by the audit trail.</summary>
public interface IHaveTimestamps
{
    DateTime CreatedAt { get; set; }

    DateTime UpdatedAt { get; set; }
}

/// <summary>Rows owned by a tenant. The tenant is copied onto the audit row instead of into <c>changes</c>.</summary>
public interface ICompanyScoped
{
    int CompanyId { get; set; }
}
