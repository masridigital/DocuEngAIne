namespace DocuEngAIne.Core.Enums;

/// <summary>
/// What a security-group grant allows on one company. Ordered: each level includes the ones below.
/// View reads the company and its records; Edit creates and changes them; Manage also archives,
/// restores and deletes them.
/// </summary>
public enum CompanyAccessLevel
{
    None = 0,
    View = 1,
    Edit = 2,
    Manage = 3,
}
