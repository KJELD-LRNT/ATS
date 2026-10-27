using Xunit;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>
/// A test that needs a real SQL Server (for example to run the EF migrations, which are SQL Server only). It is skipped
/// unless <c>ATS_TEST_SQLSERVER</c> is set, as it is in CI.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!AppFactory.UsingSqlServer) Skip = $"Needs SQL Server: set {AppFactory.SqlServerEnvVar}.";
    }
}
