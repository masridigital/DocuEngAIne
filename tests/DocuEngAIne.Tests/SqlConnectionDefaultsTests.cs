using DocuEngAIne.Infrastructure.Data;

namespace DocuEngAIne.Tests;

public class SqlConnectionDefaultsTests
{
    private const string SqlAuth =
        "Server=tcp:localhost,1433;Initial Catalog=DocuEngAIne;User ID=sa;Password=Secret;Encrypt=True;TrustServerCertificate=True;";

    [Fact]
    public void Resolve_Leaves_Sql_Auth_Connection_String_Unchanged_When_Managed_Identity_Is_Off()
    {
        var resolved = SqlConnectionDefaults.Resolve(SqlAuth, useManagedIdentity: false);

        Assert.Equal(SqlAuth, resolved);
    }

    [Fact]
    public void Resolve_Strips_Sql_Credentials_And_Sets_Active_Directory_Default()
    {
        var resolved = SqlConnectionDefaults.Resolve(SqlAuth, useManagedIdentity: true);

        Assert.Contains("Authentication=Active Directory Default", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID=sa", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=Secret", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Initial Catalog=DocuEngAIne", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Throws_When_Connection_String_Is_Missing()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlConnectionDefaults.Resolve("  ", useManagedIdentity: false));
    }
}
