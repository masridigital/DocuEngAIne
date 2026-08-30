using Microsoft.Data.SqlClient;

namespace DocuEngAIne.Infrastructure.Data;

/// <summary>
/// Resolves the SQL connection string for production managed identity vs local SQL auth.
/// Local user-secrets and appsettings.Development keep a SQL (or LocalDB) connection
/// string unchanged. Production sets Azure:Sql:UseManagedIdentity and uses
/// Authentication=Active Directory Default (DefaultAzureCredential / App Service MI).
/// </summary>
public static class SqlConnectionDefaults
{
    public const string ManagedIdentityAuthentication = "Active Directory Default";

    public static string Resolve(string? connectionString, bool useManagedIdentity)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:DocuEngAIne is missing.");

        if (!useManagedIdentity)
            return connectionString;

        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault,
            UserID = string.Empty,
            Password = string.Empty,
            Encrypt = true,
            TrustServerCertificate = false,
        };

        return builder.ConnectionString;
    }
}
